using System.Threading;

namespace StationeersPlc
{
    /// <summary>Counters and debug helpers. Written from several threads, so counters use Interlocked.</summary>
    internal static class Stats
    {
        public static bool Debug;

        public static long ClientsTotal, ClientsActive, Requests, Reads;
        public static long WritesQueued, WritesApplied, WritesRejected, UnassignedWrites;
        public static long ProtocolErrors, RefreshErrors, ValueErrors, Saturated;

        // main thread only
        public static int DeviceCount, ReadableValues, WritableValues, RefreshCount;
        public static double LastRefreshMs, MaxRefreshMs;

        public static void Dbg(string msg)
        {
            if (Debug) Plugin.Log.LogInfo("[debug] " + msg);
        }

        public static string StatusLine()
        {
            string s =
                $"status: devices={DeviceCount} inputs={ReadableValues} outputs={WritableValues} " +
                $"| reserved addresses DI/COIL/IR/HR={IoMap.Reserved(Table.DiscreteInput)}/{IoMap.Reserved(Table.Coil)}/{IoMap.Reserved(Table.InputRegister)}/{IoMap.Reserved(Table.HoldingRegister)} | " +
                $"clients={Interlocked.Read(ref ClientsActive)} (total {Interlocked.Read(ref ClientsTotal)}) " +
                $"requests={Interlocked.Read(ref Requests)} reads={Interlocked.Read(ref Reads)} | " +
                $"writes queued/applied/rejected={Interlocked.Read(ref WritesQueued)}/{Interlocked.Read(ref WritesApplied)}/{Interlocked.Read(ref WritesRejected)} " +
                $"(unassigned address {Interlocked.Read(ref UnassignedWrites)}) | " +
                $"refresh {LastRefreshMs:F1} ms (max {MaxRefreshMs:F1}, count {RefreshCount}) | " +
                $"errors: refresh={Interlocked.Read(ref RefreshErrors)} value={Interlocked.Read(ref ValueErrors)} protocol={Interlocked.Read(ref ProtocolErrors)} clamped={Interlocked.Read(ref Saturated)}";
            if (DeviceCount == 0) s += " | no devices found, is a world loaded?";
            return s;
        }
    }
}
