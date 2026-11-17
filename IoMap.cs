using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StationeersPlc
{
    /// <summary>One I/O point: a game signal pinned to a fixed address in one of the four tables.</summary>
    internal sealed class Alloc
    {
        public Table Table;
        public int Address;
        public long RefId;
        public string Signal = "";
        public double Scale = 1.0;
        public string Tag = "";
        public string DeviceType = "";
        public bool Active;
    }

    /// <summary>
    /// Assigns each (device, signal, table) a permanent address and keeps them in a CSV I/O list.
    /// The CSV is both the saved address map and the document students read. Addresses are never reused,
    /// so a PLC program keeps working across saves and restarts. The Scale column can be edited by hand.
    /// </summary>
    internal static class IoMap
    {
        private const string Header = "Table,Address,Address1Based,Tag,DeviceType,ReferenceId,Signal,DataType,Scale,Access,Status";

        private static readonly Dictionary<string, Alloc> ByKey = new Dictionary<string, Alloc>();
        private static readonly int[] Next = new int[4];
        private static readonly bool[] FullLogged = new bool[4];
        private static string _path;

        public static bool Dirty;
        public static string FilePath => _path;
        public static int Reserved(Table t) => Next[(int)t];
        public static int Rows => ByKey.Count;

        private static string Key(long id, string signal, Table t) => id.ToString(CultureInfo.InvariantCulture) + "|" + signal + "|" + (int)t;

        public static string TableName(Table t)
        {
            switch (t)
            {
                case Table.DiscreteInput: return "DI";
                case Table.Coil: return "COIL";
                case Table.InputRegister: return "IR";
                default: return "HR";
            }
        }

        private static bool TryParseTable(string s, out Table t)
        {
            switch ((s ?? "").Trim().ToUpperInvariant())
            {
                case "DI": t = Table.DiscreteInput; return true;
                case "COIL": t = Table.Coil; return true;
                case "IR": t = Table.InputRegister; return true;
                case "HR": t = Table.HoldingRegister; return true;
            }
            t = Table.DiscreteInput;
            return false;
        }

        /// <summary>Conventional 6-digit Modbus reference: 0xxxxx coil, 1xxxxx discrete input, 3xxxxx input reg, 4xxxxx holding reg.</summary>
        public static int OneBased(Table t, int addr)
        {
            switch (t)
            {
                case Table.Coil: return 1 + addr;
                case Table.DiscreteInput: return 100001 + addr;
                case Table.InputRegister: return 300001 + addr;
                default: return 400001 + addr;
            }
        }

        public static void Load(string path)
        {
            _path = path;
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"No existing I/O list at {path}. Starting a new one.");
                return;
            }

            int loaded = 0, bad = 0, badScale = 0;
            var lines = File.ReadAllLines(path);

            // If the file already uses addresses beyond the configured TableSize, grow the tables to fit. Otherwise those
            // rows would be dropped and then lost for good the next time the list is saved.
            int maxAddr = -1;
            for (int k = 1; k < lines.Length; k++)
            {
                var g = lines[k].Split(',');
                int ad;
                if (g.Length > 1 && int.TryParse(g[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ad) && ad > maxAddr && ad < PointStore.MaxSize)
                    maxAddr = ad;
            }
            if (maxAddr >= PointStore.Size)
            {
                Plugin.Log.LogWarning($"The I/O list uses address {maxAddr}, which is beyond TableSize ({PointStore.Size}). Tables enlarged to {maxAddr + 1} so no saved point is lost.");
                PointStore.Init(maxAddr + 1);
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                if (lines[i].StartsWith("#", StringComparison.Ordinal)) { ParseNextFree(lines[i]); continue; }
                var f = lines[i].Split(',');
                Table t; int addr; long id; double scale;
                if (f.Length < 11 ||
                    !TryParseTable(f[0], out t) ||
                    !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out addr) ||
                    !long.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ||
                    !double.TryParse(f[8], NumberStyles.Float, CultureInfo.InvariantCulture, out scale) ||
                    addr < 0 || addr >= PointStore.Size)
                {
                    bad++;
                    continue;
                }

                // A Scale of 0, a negative number or NaN would divide by zero when a PLC write is converted back. Use 1 instead.
                if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) { scale = 1.0; badScale++; }

                var a = new Alloc
                {
                    Table = t, Address = addr, RefId = id, Signal = f[6].Trim(), Scale = scale,
                    Tag = f[3].Trim(), DeviceType = f[4].Trim(), Active = false
                };
                ByKey[Key(id, a.Signal, t)] = a;
                if (addr + 1 > Next[(int)t]) Next[(int)t] = addr + 1;
                if (t == Table.Coil) PointStore.CoilAssigned[addr] = true;
                if (t == Table.HoldingRegister) PointStore.HrAssigned[addr] = true;
                loaded++;
            }
            Plugin.Log.LogInfo($"Loaded I/O list: {loaded} points from {path}" + (bad > 0 ? $" ({bad} unreadable rows skipped)" : "") +
                (badScale > 0 ? $" ({badScale} rows had an invalid Scale and were set to 1)" : ""));
        }

        /// <summary>
        /// Reads the "#NextFree,DI=..,COIL=..,IR=..,HR=.." line written by Save. It remembers the highest address ever handed out
        /// in each table, so addresses of removed points are still never reused after a restart.
        /// </summary>
        private static void ParseNextFree(string line)
        {
            var parts = line.Split(',');
            for (int k = 1; k < parts.Length; k++)
            {
                var kv = parts[k].Split('=');
                Table t; int n;
                if (kv.Length != 2 || !TryParseTable(kv[0], out t) ||
                    !int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                if (n > PointStore.Size) n = PointStore.Size;
                if (n > Next[(int)t]) Next[(int)t] = n;
            }
        }

        private static volatile bool _prunePending;

        /// <summary>Any thread. Asks the main thread to remove all offline points from the I/O list (see ServicePrune).</summary>
        public static void RequestPrune() { _prunePending = true; }

        /// <summary>
        /// Main thread, every frame. Removes points that are offline (their device is not in the world). The addresses are NOT
        /// handed out again. It refuses to run while the program is running, before the first devices have been found, or while
        /// new devices are still being scanned, because in those moments a device that does exist would look offline.
        /// </summary>
        public static void ServicePrune()
        {
            if (!_prunePending) return;
            _prunePending = false;

            if (StRuntime.Running) { Plugin.Log.LogWarning("Offline points were not removed: stop the PLC program first."); return; }
            if (!GameAdapter.ReadyForPrune) { Plugin.Log.LogWarning("Offline points were not removed: load a save and wait until all devices are listed."); return; }

            var dead = ByKey.Where(kv => !kv.Value.Active).ToList();
            foreach (var kv in dead)
            {
                var a = kv.Value;
                ByKey.Remove(kv.Key);
                if (a.Table == Table.Coil) PointStore.CoilAssigned[a.Address] = false;
                if (a.Table == Table.HoldingRegister) PointStore.HrAssigned[a.Address] = false;
            }
            if (dead.Count > 0)
            {
                Dirty = true;
                Plugin.Log.LogInfo($"Removed {dead.Count} offline points from the I/O list. Their addresses will not be reused.");
            }
        }

        /// <summary>Returns the permanent address for this signal, creating one the first time. Null if the table is full.</summary>
        public static Alloc GetOrAllocate(long refId, string signal, Table t, double defaultScale, string tag, string deviceType)
        {
            string key = Key(refId, signal, t);
            Alloc a;
            if (ByKey.TryGetValue(key, out a))
            {
                if (a.Tag != tag || a.DeviceType != deviceType)
                {
                    a.Tag = tag;
                    a.DeviceType = deviceType;
                    Dirty = true;
                }
                return a;
            }

            int addr = Next[(int)t];
            if (addr >= PointStore.Size)
            {
                if (!FullLogged[(int)t])
                {
                    FullLogged[(int)t] = true;
                    Plugin.Log.LogError($"The {TableName(t)} table is full ({PointStore.Size} addresses). New signals will not be exposed.");
                }
                return null;
            }

            Next[(int)t] = addr + 1;
            a = new Alloc
            {
                Table = t, Address = addr, RefId = refId, Signal = signal, Scale = defaultScale,
                Tag = tag, DeviceType = deviceType
            };
            ByKey[key] = a;
            if (t == Table.Coil) PointStore.CoilAssigned[addr] = true;
            if (t == Table.HoldingRegister) PointStore.HrAssigned[addr] = true;
            Dirty = true;
            return a;
        }

        /// <summary>Main thread only. Sorted copy of all points for the web page.</summary>
        public static List<Alloc> Snapshot() => ByKey.Values.OrderBy(x => (int)x.Table).ThenBy(x => x.Address).ToList();

        /// <summary>Writes the CSV through a temporary file so a crash mid-write cannot leave a half-written list.</summary>
        public static void Save()
        {
            if (string.IsNullOrEmpty(_path)) return;

            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(Header).Append(Environment.NewLine);
            foreach (var a in ByKey.Values.OrderBy(x => (int)x.Table).ThenBy(x => x.Address))
            {
                bool isBool = a.Table == Table.DiscreteInput || a.Table == Table.Coil;
                bool output = a.Table == Table.Coil || a.Table == Table.HoldingRegister;
                sb.Append(TableName(a.Table)).Append(',')
                  .Append(a.Address.ToString(inv)).Append(',')
                  .Append(OneBased(a.Table, a.Address).ToString(inv)).Append(',')
                  .Append(a.Tag).Append(',')
                  .Append(a.DeviceType).Append(',')
                  .Append(a.RefId.ToString(inv)).Append(',')
                  .Append(a.Signal).Append(',')
                  .Append(isBool ? "BOOL" : "INT16").Append(',')
                  .Append(isBool ? "1" : a.Scale.ToString("R", inv)).Append(',')
                  .Append(output ? "Write" : "Read").Append(',')
                  .Append(a.Active ? "active" : "inactive")
                  .Append(Environment.NewLine);
            }

            // Remember the highest address ever used in each table (see ParseNextFree).
            sb.Append("#NextFree,")
              .Append("DI=").Append(Next[(int)Table.DiscreteInput].ToString(inv)).Append(',')
              .Append("COIL=").Append(Next[(int)Table.Coil].ToString(inv)).Append(',')
              .Append("IR=").Append(Next[(int)Table.InputRegister].ToString(inv)).Append(',')
              .Append("HR=").Append(Next[(int)Table.HoldingRegister].ToString(inv))
              .Append(Environment.NewLine);

            string dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
            Dirty = false;
        }
    }
}
