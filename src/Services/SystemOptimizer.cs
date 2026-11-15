using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    /// <summary>
    /// Memory trimming and background maintenance
    /// </summary>
    public static class SystemOptimizer
    {
        #region Native Methods
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);
        #endregion

        /// <summary>
        /// Trims this process's working set
        /// </summary>
        /// <summary>Collects garbage and trims shortly after (e.g. when a window closed).</summary>
        public static void TrimSoon()
        {
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                TrimWorkingSet();
            });
        }

        public static void TrimWorkingSet()
        {
            try
            {
                using var proc = Process.GetCurrentProcess();
                EmptyWorkingSet(proc.Handle);
            }
            catch { }
        }

        /// <summary>
        /// Periodic maintenance (save traffic, collect garbage, trim memory)
        /// </summary>
        /// seconds since start
        public static void RunMaintenanceTasks(long secondsCounter)
        {
            // 1. Save traffic: every 60 s (at second 5)
            if (secondsCounter % 60 == 5)
            {
                TrafficLogger.Save();
            }

            // 2. Collect garbage: every 180 s (at second 30)
            if (secondsCounter % 180 == 30)
            {
                GC.Collect(2, GCCollectionMode.Optimized);
            }

            // 3. trim the working set every 60 s when above 24 MB
            if (secondsCounter % 60 == 45)
            {
                try
                {
                    using var proc = Process.GetCurrentProcess();
                    if (proc.WorkingSet64 > 24 * 1024 * 1024) 
                    {
                        EmptyWorkingSet(proc.Handle);
                    }
                }
                catch { }
            }
        }
    }
}
