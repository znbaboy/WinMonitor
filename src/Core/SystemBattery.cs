using System;
using System.Runtime.InteropServices;

namespace WinMonitor.Core
{
    /// <summary>
    /// Does this PC have a laptop battery? Asked once from Windows' power capabilities (the same answer
    /// Windows uses to show the battery icon). A UPS reports itself as a short-term battery and does not count.
    /// </summary>
    public static class SystemBattery
    {
        public static readonly bool Present = Detect();

        private static bool Detect()
        {
            try
            {
                if (GetPwrCapabilities(out var caps)) return caps.SystemBatteriesPresent != 0 && caps.BatteriesAreShortTerm == 0;
            }
            catch { }
            return true;   // unknown: keep the rows available
        }

        /// <summary>Laptop battery rows (BAT.*), as opposed to wireless device batteries.</summary>
        public static bool IsLaptopRow(string key) => key.StartsWith("BAT.", StringComparison.OrdinalIgnoreCase);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern bool GetPwrCapabilities(out SYSTEM_POWER_CAPABILITIES caps);

        [StructLayout(LayoutKind.Sequential)]
        private struct BATTERY_REPORTING_SCALE { public uint Granularity, Capacity; }

        // BOOLEAN fields are one byte; layout as in winnt.h
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_CAPABILITIES
        {
            public byte PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5,
                        HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent, ThermalControl, ProcessorThrottle;
            public byte ProcessorMinThrottle, ProcessorMaxThrottle;
            public byte FastSystemS4;
            public byte Hiberboot, WakeAlarmPresent, AoAc, DiskSpinDown;
            public byte HiberFileType, AoAcConnectivitySupported;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] spare3;
            public byte SystemBatteriesPresent, BatteriesAreShortTerm;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public BATTERY_REPORTING_SCALE[] BatteryScale;
            public int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
        }
    }
}
