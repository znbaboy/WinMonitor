using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    /// <summary>
    /// 系统优化器：负责内存清理、性能修剪及后台维护任务
    /// </summary>
    public static class SystemOptimizer
    {
        #region Native Methods
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);
        #endregion

        /// <summary>
        /// 修剪当前进程的工作集内存
        /// </summary>
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
        /// 执行定时维护任务 (流量保存、GC 优化、内存修剪)
        /// </summary>
        /// <param name="secondsCounter">系统运行秒数计数器</param>
        public static void RunMaintenanceTasks(long secondsCounter)
        {
            // 1. 流量保存: 每 60 秒 (Offset 5s: 避开整点)
            if (secondsCounter % 60 == 5)
            {
                TrafficLogger.Save();
            }

            // 2. 内存软清理: 每 180 秒 (Offset 30s)
            if (secondsCounter % 180 == 30)
            {
                GC.Collect(2, GCCollectionMode.Optimized);
            }

            // 3. 内存硬清理: 每 300 秒 (5分钟) (Offset 45s)
            if (secondsCounter % 300 == 45)
            {
                try
                {
                    using var proc = Process.GetCurrentProcess();
                    // 阈值 30MB，确保极致轻量
                    if (proc.WorkingSet64 > 30 * 1024 * 1024) 
                    {
                        EmptyWorkingSet(proc.Handle);
                    }
                }
                catch { }
            }
        }
    }
}
