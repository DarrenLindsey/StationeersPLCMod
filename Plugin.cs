using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace StationeersPlc
{
    /// <summary>
    /// BepInEx entry point. Awake runs once when the game starts. It reads the settings, loads the I/O list,
    /// starts the Modbus server and the web workstation, and creates the hidden object that drives everything
    /// from Unity's Update loop (see PlcRuntime).
    ///
    /// Threading rules for the whole mod (details in ARCHITECTURE.md):
    ///   * Unity main thread  - the ONLY thread allowed to touch game objects (GameAdapter) and the ST program.
    ///   * Modbus / web threads - read and write PointStore, and hand work to the main thread through queues
    ///     (Plugin.PendingWrites, StRuntime.Submit/RequestMode/Force) or cached strings.
    /// Keep PluginVersion in step with the README and the CHANGELOG section of README.md.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.stationeersplc.iolist";
        public const string PluginName = "Stationeers PLC I/O List";
        public const string PluginVersion = "0.7.0";

        /// <summary>BepInEx logger. Set in Awake, so do not use it from static initialisers.</summary>
        internal static ManualLogSource Log;
        /// <summary>Outputs waiting to be applied to game devices. Filled by Modbus threads and the ST program, drained by PlcRuntime.Tick on the main thread.</summary>
        internal static readonly ConcurrentQueue<WriteRequest> PendingWrites = new ConcurrentQueue<WriteRequest>();
        /// <summary>Mirrors the Modbus AllowWrites setting so the web page's manual switches obey it too.</summary>
        internal static volatile bool WritesAllowed = true;

        private void Awake()
        {
            Log = Logger;

            var port = Config.Bind("Modbus", "Port", 5020, "TCP port of the Modbus TCP server.");
            var bind = Config.Bind("Modbus", "BindAddress", "127.0.0.1",
                "Address to listen on. 127.0.0.1 = this computer only. Use 0.0.0.0 to allow other computers (no authentication!).");
            var refresh = Config.Bind("Modbus", "RefreshSeconds", 0.25f, "How often game values are copied into the Modbus tables.");
            var allowWrites = Config.Bind("Modbus", "AllowWrites", true, "When false, the PLC can read everything but change nothing.");
            WritesAllowed = allowWrites.Value;
            var tableSize = Config.Bind("Modbus", "TableSize", PointStore.MaxSize,
                "Number of addresses in EACH of the four tables (64 to 65536). A smaller table uses less memory and makes a shorter I/O list. " +
                "If your saved I/O list already uses higher addresses, the tables are enlarged automatically so nothing is lost.");

            var booleans = Config.Bind("Signals", "BooleanSignals", SignalCatalog.DefaultBooleans,
                "Comma-separated signal names exposed as on/off bits instead of 16-bit numbers.");
            var ioPath = Config.Bind("Signals", "IoListPath", "",
                "Where the I/O list CSV is stored. Empty = BepInEx/config/StationeersPlc_iolist.csv.");
            var save = Config.Bind("Signals", "SaveSeconds", 10f, "How often a changed I/O list is written to disk.");
            var excl = Config.Bind("Signals", "ExcludeSignals", SignalCatalog.DefaultExcludedSignals,
                "Signals never exposed (identifiers and hashes do not fit in 16 bits). Names ending in Hash are always excluded.");

            var items = Config.Bind("Devices", "IncludeItems", false, "Also expose handheld items (tablets, tools...).");
            var prefix = Config.Bind("Devices", "OnlyNamePrefix", "",
                "Only expose devices whose name starts with this text, for example PLC (so PLC-Light, PLC-Pump). Empty = expose every device. Rename devices in the game with a labeller or the console to add them.");
            var exTypes = Config.Bind("Devices", "ExcludeDeviceTypes", "ElevatorShaft",
                "Comma-separated device class names that are skipped.");

            var debug = Config.Bind("Debug", "DebugLogging", true, "Log a status line and extra detail.");
            var status = Config.Bind("Debug", "StatusIntervalSeconds", 10f, "Seconds between status lines.");

            var stOn = Config.Bind("Plc", "EnableStProgram", true, "Enable the in-game Structured Text program and the web workstation.");
            var stScan = Config.Bind("Plc", "ScanSeconds", 0.1f, "Time between ST program scans (0.02 to 1).");
            var stBlock = Config.Bind("Plc", "BlockModbusWritesWhileRunning", true,
                "While the in-game ST program is in RUN, refuse Modbus writes (so two controllers never fight over an output).");
            var webPort = Config.Bind("Plc", "WebPort", 15882, "Port of the web workstation (http://127.0.0.1:port/). 0 disables it.");
            var stFile = Config.Bind("Plc", "ProgramPath", "", "ST program file. Empty = BepInEx/config/StationeersPlc_program.st.");

            // ---- Apply settings. Values are clamped here so a bad config file cannot stall the game. ----
            Stats.Debug = debug.Value;
            PlcRuntime.RefreshSeconds = Mathf.Max(0.05f, refresh.Value);
            PlcRuntime.StatusSeconds = Mathf.Max(1f, status.Value);
            PlcRuntime.SaveSeconds = Mathf.Max(1f, save.Value);

            // Size the tables before anything reads or writes them (the I/O list load below may enlarge them if needed).
            PointStore.Init(Mathf.Clamp(tableSize.Value, 64, PointStore.MaxSize));
            Log.LogInfo($"Table size: {PointStore.Size} addresses per table.");

            SignalCatalog.Configure(booleans.Value);
            SignalCatalog.ConfigureExclusions(excl.Value);

            GameAdapter.IncludeItems = items.Value;
            GameAdapter.NamePrefix = (prefix.Value ?? "").Trim();
            foreach (var part in (exTypes.Value ?? "").Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0) GameAdapter.ExcludeTypes.Add(s);
            }

            string path = string.IsNullOrWhiteSpace(ioPath.Value)
                ? Path.Combine(Paths.ConfigPath, "StationeersPlc_iolist.csv")
                : ioPath.Value;
            try { IoMap.Load(path); }
            catch (Exception e) { Log.LogError("Could not read the I/O list: " + e.Message); }

            IPAddress ip;
            if (!IPAddress.TryParse(bind.Value, out ip)) ip = IPAddress.Loopback;

            try
            {
                PlcRuntime.Server = new ModbusServer();
                PlcRuntime.Server.Start(ip, port.Value, allowWrites.Value);
                Log.LogInfo($"{PluginName} {PluginVersion}: Modbus TCP listening on {ip}:{port.Value}" +
                            (allowWrites.Value ? "" : " (read-only)"));
            }
            catch (Exception e)
            {
                Log.LogError($"Could not start Modbus on {ip}:{port.Value}: {e.Message}");
                PlcRuntime.Server = null;
            }

            // The plugin component can be destroyed by the game, so work runs on our own hidden persistent object.
            var go = new GameObject("StationeersPlcDriver");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<PlcDriver>();
            PlcRuntime.DriverObject = go;
            PlcRuntime.Started = true;

            if (stOn.Value)
            {
                StRuntime.BlockExternalWrites = stBlock.Value;
                StRuntime.ScanSeconds = Mathf.Clamp(stScan.Value, 0.02f, 1f);
                StRuntime.Load(string.IsNullOrWhiteSpace(stFile.Value)
                    ? Path.Combine(Paths.ConfigPath, "StationeersPlc_program.st")
                    : stFile.Value);
                PlcRuntime.StEnabled = true;
                if (webPort.Value > 0)
                {
                    try
                    {
                        Web.Start(webPort.Value);
                        Log.LogInfo($"Workstation: http://127.0.0.1:{webPort.Value}/ (open it in a browser, e.g. on a second monitor)");
                    }
                    catch (Exception e) { Log.LogError($"Could not start the web workstation on port {webPort.Value}: {e.Message}"); }
                }
            }
        }
    }
}
