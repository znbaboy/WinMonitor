using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinMonitor.Services
{
    /// <summary>
    /// Windows performance counters
    /// <para>System metrics read through System.Diagnostics.PerformanceCounter.</para>
    /// <para>Faster and closer to Task Manager than LibreHardwareMonitor (CPU clock, memory) and does not touch the hardware bus.</para>
    /// </summary>
    public class PerformanceCounterManager : IDisposable
    {
        // --- Counters ---
        private PerformanceCounter? _cpuLoadCounter; // CPU usage (incl. kernel time)
        private PerformanceCounter? _cpuActualFreqCounter; // CPU actual frequency (MHz)
        private PerformanceCounter? _cpuProcessorFreqCounter; // CPU processor frequency (MHz)
        private PerformanceCounter? _cpuFreqCounter; // CPU performance % (to work out the clock)
        private PerformanceCounter? _ramAvailableCounter; // available memory (MB)
        private PerformanceCounter? _diskReadCounter; // disk read speed (all disks)
        private PerformanceCounter? _diskWriteCounter; // disk write speed (all disks)
        private PerformanceCounter? _diskActiveCounter; // disk active time (%)
        private PerformanceCounter? _uptimeCounter; // system uptime

        // --- SMB counters (to leave out local network traffic) ---
        // SMB Client Shares is often missing; Redirector and Server are lower-level and reliable:
        // Redirector = client traffic (I access others)
        // Server = server traffic (others access me)
        // These are single-instance counters (instance name "")
        private PerformanceCounter? _smbClientReadCounter;
        private PerformanceCounter? _smbClientWriteCounter;
        private PerformanceCounter? _smbServerReadCounter;
        private PerformanceCounter? _smbServerWriteCounter;

        // --- Fixed values (read once) ---
        private float _cpuBaseFreq = 0; // CPU base clock (MHz)
        private float _totalMemoryMB = 0; // physical memory (MB)

        /// <summary>
        /// True once the counters are set up and warmed up.
        /// </summary>
        public bool IsInitialized { get; private set; } = false;

        /// <summary>
        /// Physical memory (GB)
        /// </summary>
        public float TotalMemoryGB => _totalMemoryMB / 1024f;

        /// <summary>
        /// Sets up all counters in the background.
        /// <para>Call at startup; does not block the UI.</para>
        /// </summary>
        public void InitializeAsync()
        {
            Task.Run(() =>
            {
                try
                {
                    // 1. Fixed hardware values (memory, base clock)
                    InitStaticHardwareInfo();

                    // 2. Create the counters
                    // English category names work on most localized systems too
                    
                    // CPU usage: Processor Information (Windows 8+)
                    // "% Processor Utility", not "% Processor Time":
                    // Task Manager shows Utility (includes boost) on Windows 8+
                    _cpuLoadCounter = CreateCounter("Processor Information", "% Processor Utility", "_Total");

                    if (_cpuLoadCounter == null)
                        _cpuLoadCounter = CreateCounter("Processor Information", "% Processor Time", "_Total"); // fallback 1

                    if (_cpuLoadCounter == null) 
                        _cpuLoadCounter = CreateCounter("Processor", "% Processor Time", "_Total"); // fallback for older Windows

                    // CPU clock: Windows' direct frequency first, else from the performance percentage
                    _cpuActualFreqCounter = CreateCounter("Processor Information", "Actual Frequency", "_Total");
                    _cpuProcessorFreqCounter = CreateCounter("Processor Information", "Processor Frequency", "_Total");
                    _cpuFreqCounter = CreateCounter("Processor Information", "% Processor Performance", "_Total");
                    if (_cpuFreqCounter == null) 
                        _cpuFreqCounter = CreateCounter("Processor", "% Processor Performance", "_Total");

                    // Memory: in use = total - available (more accurate than the load counter)
                    _ramAvailableCounter = CreateCounter("Memory", "Available MBytes");

                    // Disks: all physical disks (_Total)
                    _diskReadCounter = CreateCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                    _diskWriteCounter = CreateCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                    _diskActiveCounter = CreateCounter("PhysicalDisk", "% Disk Time", "_Total"); // Task Manager's disk %

                    // System: uptime
                    _uptimeCounter = CreateCounter("System", "System Up Time");

                    // SMB (local network traffic)
                    // Client: Read/Write Bytes/sec
                    _smbClientReadCounter = CreateCounter("SMB Client Shares", "Read Bytes/sec", "_Total");
                    _smbClientWriteCounter = CreateCounter("SMB Client Shares", "Write Bytes/sec", "_Total");

                    // Server: Received/Sent Bytes/sec
                    _smbServerReadCounter = CreateCounter("SMB Server Shares", "Received Bytes/sec", "_Total");
                    _smbServerWriteCounter = CreateCounter("SMB Server Shares", "Sent Bytes/sec", "_Total");

                    // 3. Warm up:
                    // counters compare two samples,
                    // so the first NextValue() is always 0.
                    // Read once here so the first values shown are real.
                    // SafeRead: one broken counter must not stop the rest
                    
                    SafeRead(_cpuLoadCounter);
                    SafeRead(_cpuActualFreqCounter);
                    SafeRead(_cpuProcessorFreqCounter);
                    SafeRead(_cpuFreqCounter);
                    SafeRead(_ramAvailableCounter);
                    SafeRead(_diskReadCounter);
                    SafeRead(_diskWriteCounter);
                    SafeRead(_diskActiveCounter);
                    SafeRead(_uptimeCounter);
                    
                    SafeRead(_smbClientReadCounter);
                    SafeRead(_smbClientWriteCounter);
                    SafeRead(_smbServerReadCounter);
                    SafeRead(_smbServerWriteCounter);

                    IsInitialized = true;
                }
                catch
                {
                    // Usually a damaged system component (e.g. stripped-down Windows):
                    // IsInitialized stays false and LibreHardwareMonitor is used instead
                }
            });
        }

        /// <summary>
        /// Creates a counter (null on failure)
        /// </summary>
        private PerformanceCounter? CreateCounter(string category, string counter, string instance = "")
        {
            try
            {
                if (!PerformanceCounterCategory.Exists(category)) return null;
                return string.IsNullOrEmpty(instance) 
                    ? new PerformanceCounter(category, counter) 
                    : new PerformanceCounter(category, counter, instance);
            }
            catch
            {
                return null;
            }
        }

        private void InitStaticHardwareInfo()
        {
            // A. CPU base clock (from the registry, faster than WMI)
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (key != null)
                    {
                        var val = key.GetValue("~MHz");
                        if (val is int freq) _cpuBaseFreq = freq;
                    }
                }
            }
            catch { }
            // Fallback 2.5 GHz (avoids dividing by zero)
            if (_cpuBaseFreq <= 0) _cpuBaseFreq = 2500; 

            // B. Physical memory (GlobalMemoryStatusEx)
            try
            {
                MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    _totalMemoryMB = memStatus.ullTotalPhys / 1024f / 1024f;
                }
            }
            catch { }
            // Fallback 16 GB
            if (_totalMemoryMB <= 0) _totalMemoryMB = 16 * 1024;
        }

        // ==========================================
        // Safe reads (null on failure)
        // ==========================================

        public float? GetCpuLoad()
        {
            var val = SafeRead(_cpuLoadCounter);
            // Utility can exceed 100% while boosting: cap it like Task Manager
            return val > 100f ? 100f : val;
        }

        public float? GetCpuFreq()
        {
            var actualFreq = SafeRead(_cpuActualFreqCounter);
            if (IsValidCpuFrequency(actualFreq)) return actualFreq;

            var processorFreq = SafeRead(_cpuProcessorFreqCounter);
            var percent = SafeRead(_cpuFreqCounter);
            return ResolveCpuFrequency(processorFreq, percent, _cpuBaseFreq);
        }

        private static float? ResolveCpuFrequency(float? processorFreq, float? percent, float registryFreq)
        {
            // Processor Frequency is the nominal clock: the base for the performance percentage, not a live clock.
            float baseFreq = IsValidCpuFrequency(processorFreq) ? processorFreq!.Value : registryFreq;
            if (percent.HasValue)
            {
                if (!float.IsFinite(percent.Value) || percent.Value <= 0 || !IsValidCpuFrequency(baseFreq)) return null;
                float calculated = baseFreq * (percent.Value / 100.0f);
                return IsValidCpuFrequency(calculated) ? calculated : null;
            }

            // Nominal value only when the performance counter is missing; a bad sample returns null so the sensors are tried.
            return IsValidCpuFrequency(processorFreq) ? processorFreq : null;
        }

        private static bool IsValidCpuFrequency(float? value)
        {
            return value.HasValue &&
                   value.Value > 0f &&
                   value.Value < 10000f &&
                   !float.IsNaN(value.Value) &&
                   !float.IsInfinity(value.Value);
        }

        /// <summary>
        /// Memory values.
        /// Returns (load %, used GB)
        /// </summary>
        public (float? Load, float? UsedGB) GetMemoryData()
        {
            var availableMB = SafeRead(_ramAvailableCounter);
            if (availableMB.HasValue && _totalMemoryMB > 0)
            {
                float usedMB = _totalMemoryMB - availableMB.Value;
                float load = (usedMB / _totalMemoryMB) * 100f;
                float usedGB = usedMB / 1024f;
                return (load, usedGB);
            }
            return (null, null);
        }

        public float? GetDiskRead() => SafeRead(_diskReadCounter);
        public float? GetDiskWrite() => SafeRead(_diskWriteCounter);
        public float? GetDiskActive() => SafeRead(_diskActiveCounter);
        public float? GetUptime() => SafeRead(_uptimeCounter);

        /// <summary>
        /// Estimated SMB traffic since the last sample (with protocol overhead)
        /// </summary>
        /// seconds since the last sample
        public (long UpBytes, long DownBytes) GetEstimatedSmbBytes(double seconds)
        {
            var rate = GetSmbTrafficRate();

            // A 1.2 factor covers TCP/IP and SMB headers and the counters' averaging lag;
            // without it a 600 MB transfer could still show about 30 MB of traffic.
            const double OverheadFactor = 1.2;

            long up = (long)(rate.UpRate * seconds * OverheadFactor);
            long down = (long)(rate.DownRate * seconds * OverheadFactor);

            return (up, down);
        }

        /// <summary>
        /// Current local network (SMB) rate (client + server)
        /// <para>Bytes/sec</para>
        /// </summary>
        private (float UpRate, float DownRate) GetSmbTrafficRate()
        {
            float up = 0;
            float down = 0;

            // 1. Client (Redirector)
            // Upload = Transmitted
            if (_smbClientWriteCounter != null) up += (SafeRead(_smbClientWriteCounter) ?? 0);
            // Download = Received
            if (_smbClientReadCounter != null) down += (SafeRead(_smbClientReadCounter) ?? 0);

            // 2. Server
            // Upload = sent (others download from me)
            if (_smbServerWriteCounter != null) up += (SafeRead(_smbServerWriteCounter) ?? 0);
            // Download = received (others send to me)
            if (_smbServerReadCounter != null) down += (SafeRead(_smbServerReadCounter) ?? 0);

            return (up, down);
        }

        /// <summary>
        /// Read with try/catch (a reset system component must not crash the app)
        /// </summary>
        private float? SafeRead(PerformanceCounter? pc)
        {
            if (pc == null) return null;
            try
            {
                var val = pc.NextValue();
                return val;
            }
            catch
            {
                return null; 
            }
        }

        public void Dispose()
        {
            // Dispose all counters
            _cpuLoadCounter?.Dispose();
            _cpuActualFreqCounter?.Dispose();
            _cpuProcessorFreqCounter?.Dispose();
            _cpuFreqCounter?.Dispose();
            _ramAvailableCounter?.Dispose();
            _diskReadCounter?.Dispose();
            _diskWriteCounter?.Dispose();
            _diskActiveCounter?.Dispose();
            _uptimeCounter?.Dispose();
            
            // Dispose the SMB counters
            _smbClientReadCounter?.Dispose();
            _smbClientWriteCounter?.Dispose();
            _smbServerReadCounter?.Dispose();
            _smbServerWriteCounter?.Dispose();
        }

        // --- Win32 memory structure ---
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
