using System;
using System.Threading;
using UnityEngine;

namespace StationeersPlc
{
    /// <summary>
    /// Everything that must keep running lives here, driven by our own hidden GameObject,
    /// so the mod does not depend on the BepInEx plugin component staying alive.
    /// </summary>
    internal static class PlcRuntime
    {
        public static bool Started;
        public static bool Quitting;
        public static bool StEnabled;
        public static ModbusServer Server;
        public static GameObject DriverObject;
        public static float RefreshSeconds = 0.25f;
        public static float StatusSeconds = 10f;
        public static float SaveSeconds = 10f;

        private static float _nextRefresh, _nextStatus, _nextErrorLog, _nextSave;
        private static int _suppressedErrors;
        private static bool _firstRefreshLogged, _firstTickLogged, _saveFailedLogged;

        /// <summary>
        /// One frame of work, called from PlcDriver.Update on the Unity main thread. Order matters:
        ///   1. apply queued writes (so reads below see them),
        ///   2. refresh game values into PointStore every RefreshSeconds,
        ///   3. run the ST program and update the web page cache,
        ///   4. save the I/O list when it changed,
        ///   5. print the status line.
        /// Unscaled time is used so the PLC keeps its rhythm even if the game is paused or sped up.
        /// </summary>
        public static void Tick()
        {
            float now = Time.unscaledTime;

            if (!_firstTickLogged)
            {
                _firstTickLogged = true;
                Plugin.Log.LogInfo($"Update loop running (scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}', t={now:F1}s)");
            }

            // Apply queued PLC writes first so reads in the same tick see them.
            while (Plugin.PendingWrites.TryDequeue(out var w))
            {
                string reason;
                try { reason = GameAdapter.Apply(w); }
                catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; }

                if (reason == null) Interlocked.Increment(ref Stats.WritesApplied);
                else
                {
                    long n = Interlocked.Increment(ref Stats.WritesRejected);
                    if (n <= 20) Plugin.Log.LogWarning($"Write rejected ({IoMap.TableName(w.Table)} {w.Address} = {w.Value}): {reason}");
                    else if (n == 21) Plugin.Log.LogWarning("Further rejected writes are counted but not logged individually.");
                }
            }

            if (now >= _nextRefresh)
            {
                _nextRefresh = now + RefreshSeconds;
                TryRefresh(now);
            }

            if (StEnabled)
            {
                StRuntime.Tick(now);
                Web.Tick(now);
            }

            IoMap.ServicePrune();

            if (IoMap.Dirty && now >= _nextSave)
            {
                _nextSave = now + SaveSeconds;
                SaveIoList();
            }

            if (Stats.Debug && now >= _nextStatus)
            {
                _nextStatus = now + StatusSeconds;
                Plugin.Log.LogInfo(Stats.StatusLine());
            }
        }

        private static void SaveIoList()
        {
            try
            {
                IoMap.Save();
                Stats.Dbg($"I/O list saved: {IoMap.Rows} points -> {IoMap.FilePath}");
            }
            catch (Exception e)
            {
                if (!_saveFailedLogged)
                {
                    _saveFailedLogged = true;
                    Plugin.Log.LogWarning($"Could not save the I/O list to {IoMap.FilePath}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static void TryRefresh(float now)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                GameAdapter.Refresh();
                sw.Stop();
                Stats.LastRefreshMs = sw.Elapsed.TotalMilliseconds;
                if (Stats.LastRefreshMs > Stats.MaxRefreshMs) Stats.MaxRefreshMs = Stats.LastRefreshMs;
                Stats.RefreshCount++;

                if (!_firstRefreshLogged && Stats.DeviceCount > 0)
                {
                    _firstRefreshLogged = true;
                    Plugin.Log.LogInfo($"First snapshot: {Stats.DeviceCount} devices, {Stats.ReadableValues} input points and " +
                                       $"{Stats.WritableValues} output points, took {Stats.LastRefreshMs:F1} ms");
                }
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref Stats.RefreshErrors);
                if (now < _nextErrorLog) { _suppressedErrors++; return; }
                _nextErrorLog = now + 30f;
                string extra = _suppressedErrors > 0 ? $" ({_suppressedErrors} similar errors suppressed)" : "";
                _suppressedErrors = 0;
                Plugin.Log.LogWarning($"Snapshot refresh failed: {e.GetType().Name}: {e.Message}{extra}\n{e.StackTrace}");
            }
        }

        public static void Shutdown(string why)
        {
            if (!Started) return;
            Started = false;
            Plugin.Log.LogInfo($"Shutting down ({why}). Final {Stats.StatusLine()}");
            SaveIoList();
            try { Server?.Stop(); } catch { }
            try { Web.Stop(); } catch { }
            Server = null;
        }
    }

    /// <summary>Hidden, persistent object that gives the runtime its Update calls.</summary>
    internal sealed class PlcDriver : MonoBehaviour
    {
        private void Update() => PlcRuntime.Tick();

        private void OnApplicationQuit() => PlcRuntime.Quitting = true;

        private void OnDestroy()
        {
            if (PlcRuntime.Quitting) { PlcRuntime.Shutdown("game exiting"); return; }
            Plugin.Log.LogWarning("Driver object was destroyed while the game is running. Something removed it. Stack:\n" + Environment.StackTrace);
            PlcRuntime.Shutdown("driver destroyed");
        }
    }
}
