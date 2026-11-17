using System;
using System.Collections.Generic;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using GameThing = Assets.Scripts.Objects.Thing;
using DynamicThing = Assets.Scripts.Objects.DynamicThing;

namespace StationeersPlc
{
    /// <summary>
    /// The only class that touches game objects. Main thread only.
    /// Finds every device with a logic interface, gives each readable signal an input address and each
    /// writable signal an output address, copies values into PointStore, and applies queued PLC writes.
    /// </summary>
    internal static class GameAdapter
    {
        private sealed class Sig
        {
            public LogicType Type;
            public string Name;
            public bool IsBool;
            public Alloc In;   // DI or IR
            public Alloc Out;  // COIL or HR
        }

        private sealed class Dev
        {
            public GameThing Thing;
            public ILogicable Logic;
            public long Id;
            public string Tag;
            public string RawName;   // the game's CustomName as last seen; used to notice a rename cheaply
            public string TypeName;
            public readonly List<Sig> Sigs = new List<Sig>();
            public bool Seen;
        }

        private sealed class OutRef { public Dev Dev; public Sig Sig; }

        private static readonly Dictionary<long, Dev> Devices = new Dictionary<long, Dev>();
        private static readonly Dictionary<int, OutRef> CoilOut = new Dictionary<int, OutRef>();
        private static readonly Dictionary<int, OutRef> HrOut = new Dictionary<int, OutRef>();
        private static readonly List<GameThing> Scratch = new List<GameThing>();
        private static LogicType[] _allTypes;
        // A newly found device costs up to ~100 LogicType probes. Limiting new devices per refresh avoids a long frame
        // when a big base loads; the rest are picked up on the following refreshes.
        private const int ScansPerRefresh = 8;

        public static bool IncludeItems;

        // True when the last refresh skipped new devices because of ScansPerRefresh, so the device list is not complete yet.
        private static bool _deferred;

        /// <summary>True once at least one device has been found and no new devices are waiting to be scanned. Used to decide when it is safe to remove offline points.</summary>
        public static bool ReadyForPrune => Devices.Count > 0 && !_deferred;
        /// <summary>When not empty, only devices whose name starts with this text are exposed (for example "PLC").</summary>
        public static string NamePrefix = "";

        private static bool HasPrefix(GameThing t)
        {
            try
            {
                string n = t.CustomName;
                return !string.IsNullOrEmpty(n) && n.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        public static HashSet<string> ExcludeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Runs every RefreshSeconds on the main thread:
        ///   1. copy the game's thing list into Scratch (ForEach holds a lock inside the game, so keep the callback tiny),
        ///   2. register new devices and mark known ones as seen,
        ///   3. publish each readable signal into PointStore,
        ///   4. retire devices that vanished (their addresses stay reserved in the I/O list).
        /// </summary>
        public static void Refresh()
        {
            if (_allTypes == null) _allTypes = (LogicType[])Enum.GetValues(typeof(LogicType));

            Scratch.Clear();
            var pool = OcclusionManager.AllThings;
            if (pool == null) return;
            pool.ForEach(t => { if (t != null) Scratch.Add(t); });

            foreach (var d in Devices.Values) d.Seen = false;

            int scans = 0;
            _deferred = false;
            foreach (var t in Scratch)
            {
                var logic = t as ILogicable;
                if (logic == null) continue;
                if (!IncludeItems && t is DynamicThing) continue;
                if (NamePrefix.Length > 0 && !HasPrefix(t)) continue;

                Dev d;
                if (!Devices.TryGetValue(t.ReferenceId, out d))
                {
                    if (scans >= ScansPerRefresh) { _deferred = true; continue; } // spread the work over several refreshes
                    if (ExcludeTypes.Contains(t.GetType().Name)) continue;
                    d = new Dev { Thing = t, Logic = logic, Id = t.ReferenceId, TypeName = t.GetType().Name };
                    Scan(d);
                    Devices[d.Id] = d;
                    scans++;
                }
                d.Seen = true;
                d.Thing = t;
                d.Logic = logic;
                CheckRename(d);
            }

            int readable = 0, writable = 0, active = 0;
            List<long> gone = null;
            foreach (var kv in Devices)
            {
                var d = kv.Value;
                if (!d.Seen)
                {
                    (gone ?? (gone = new List<long>())).Add(kv.Key);
                    continue;
                }
                active++;
                foreach (var s in d.Sigs)
                {
                    if (s.In != null) { readable++; Publish(d, s); }
                    if (s.Out != null) writable++;
                }
            }

            if (gone != null)
                foreach (var id in gone) Retire(Devices[id]);
            if (gone != null)
                foreach (var id in gone) Devices.Remove(id);

            Stats.DeviceCount = active;
            Stats.ReadableValues = readable;
            Stats.WritableValues = writable;
        }

        /// <summary>A device left the world (removed, or the world unloaded). Its addresses stay reserved and are marked inactive.</summary>
        private static void Retire(Dev d)
        {
            foreach (var s in d.Sigs)
            {
                if (s.In != null) s.In.Active = false;
                if (s.Out != null)
                {
                    s.Out.Active = false;
                    if (s.Out.Table == Table.Coil) CoilOut.Remove(s.Out.Address);
                    else HrOut.Remove(s.Out.Address);
                }
            }
            IoMap.Dirty = true;
        }

        /// <summary>
        /// Notices when a device is renamed in the game and updates its tag in the I/O list. The address stays the same,
        /// so programs keep working. The cheap string comparison runs for every device on every refresh; the tag is only
        /// rebuilt when the name actually changed.
        /// </summary>
        private static void CheckRename(Dev d)
        {
            string raw = null;
            try { raw = d.Thing.CustomName; } catch { }
            if (raw == d.RawName) return;
            d.RawName = raw;

            string tag = TagFor(d);
            if (tag == d.Tag) return;

            Plugin.Log.LogInfo($"Device renamed: {d.Tag} -> {tag} (addresses unchanged)");
            d.Tag = tag;
            foreach (var s in d.Sigs)
            {
                if (s.In != null) s.In.Tag = tag;
                if (s.Out != null) s.Out.Tag = tag;
            }
            IoMap.Dirty = true;
        }

        private static string TagFor(Dev d)
        {
            string name = "";
            try { name = SignalCatalog.Clean(d.Thing.CustomName); } catch { }
            if (name.Length == 0)
            {
                try { name = SignalCatalog.Clean(d.Thing.DisplayName); } catch { }
            }
            if (name.Length == 0) name = SignalCatalog.Clean(d.TypeName);
            return name;
        }

        /// <summary>Probes every LogicType once to learn which signals this device can read or write, and allocates addresses for them.</summary>
        private static void Scan(Dev d)
        {
            d.Tag = TagFor(d);
            foreach (var type in _allTypes)
            {
                string name = type.ToString();
                if (SignalCatalog.IsExcluded(name)) continue;

                bool canRead = false, canWrite = false;
                try { canRead = d.Logic.CanLogicRead(type); } catch { }
                try { canWrite = d.Logic.CanLogicWrite(type); } catch { }
                if (!canRead && !canWrite) continue;

                var s = new Sig { Type = type, Name = name, IsBool = SignalCatalog.IsBool(name) };
                double scale = SignalCatalog.DefaultScale(name);

                if (canRead)
                {
                    s.In = IoMap.GetOrAllocate(d.Id, name, s.IsBool ? Table.DiscreteInput : Table.InputRegister, scale, d.Tag, d.TypeName);
                    if (s.In != null) s.In.Active = true;
                }
                if (canWrite)
                {
                    s.Out = RegisterOutput(d, s, scale);
                }
                if (s.In != null || s.Out != null) d.Sigs.Add(s);
            }
            IoMap.Dirty = true;
        }

        private static Alloc RegisterOutput(Dev d, Sig s, double scale)
        {
            var table = s.IsBool ? Table.Coil : Table.HoldingRegister;
            var a = IoMap.GetOrAllocate(d.Id, s.Name, table, scale, d.Tag, d.TypeName);
            if (a == null) return null;
            a.Active = true;

            // Seed the command table from the game once, so a PLC connecting later does not slam the device.
            double cur = 0;
            try { cur = d.Logic.GetLogicValue(s.Type); } catch { }
            var map = s.IsBool ? CoilOut : HrOut;
            map[a.Address] = new OutRef { Dev = d, Sig = s };
            if (s.IsBool) PointStore.Coil[a.Address] = cur != 0;
            else PointStore.Hr[a.Address] = ToRaw(cur, a.Scale);
            return a;
        }

        private static double ReadValue(Dev d, Sig s)
        {
            return d.Logic.GetLogicValue(s.Type);
        }

        private static bool _clamped;
        private static readonly HashSet<string> ClampLogged = new HashSet<string>();

        private static short ToRaw(double v, double scale)
        {
            double r = Math.Round(v * scale);
            if (double.IsNaN(r)) { Interlocked.Increment(ref Stats.ValueErrors); return 0; }
            if (r > short.MaxValue) { Interlocked.Increment(ref Stats.Saturated); _clamped = true; return short.MaxValue; }
            if (r < short.MinValue) { Interlocked.Increment(ref Stats.Saturated); _clamped = true; return short.MinValue; }
            return (short)r;
        }

        /// <summary>Tells the player once which signal does not fit in 16 bits, so its Scale can be fixed in the I/O list.</summary>
        private static void ReportClamp(Dev d, Sig s, double v, double scale)
        {
            if (!_clamped) return;
            _clamped = false;
            if (ClampLogged.Count >= 30 || !ClampLogged.Add(d.Id + "|" + s.Name)) return;
            Plugin.Log.LogWarning($"Value does not fit in 16 bits: {d.Tag} ({d.TypeName}) {s.Name} = {v} at scale {scale}. It is clamped. Lower the Scale for this row in the I/O list CSV (for example 0.1) and restart.");
        }

        private static void Publish(Dev d, Sig s)
        {
            double v;
            try { v = ReadValue(d, s); }
            catch { Interlocked.Increment(ref Stats.ValueErrors); return; }

            if (s.IsBool) PointStore.Di[s.In.Address] = v != 0;
            else { PointStore.Ir[s.In.Address] = ToRaw(v, s.In.Scale); ReportClamp(d, s, v, s.In.Scale); }
        }

        /// <summary>Applies one PLC write on the main thread. Returns null on success, otherwise the reason.</summary>
        public static string Apply(WriteRequest w)
        {
            OutRef o;
            var map = w.Table == Table.Coil ? CoilOut : HrOut;
            if (!map.TryGetValue(w.Address, out o)) return "address is not an active output";
            if (o.Dev.Thing == null) return "device no longer exists";

            double value = w.Table == Table.Coil ? (w.Value != 0 ? 1.0 : 0.0) : (short)w.Value / o.Sig.Out.Scale;
            if (!o.Dev.Logic.CanLogicWrite(o.Sig.Type)) return "device refuses writes to " + o.Sig.Name + " right now";
            o.Dev.Logic.SetLogicValue(o.Sig.Type, value);
            return null;
        }

        /// <summary>Number of devices currently tracked (including ones not seen in the latest refresh).</summary>
        public static int DeviceTotal => Devices.Count;
    }
}
