using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;
using Debug = System.Diagnostics.Debug;

namespace WinMonitor.Services
{
    public class HardwareValueProvider : IDisposable
    {
        private readonly Computer _computer;
        private readonly Settings _cfg;
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly object _lock;
        private readonly Dictionary<string, float> _lastValidMap; 
        
        // Helpers
        private readonly PerformanceCounterManager _perfManager;
        private readonly ComponentProcessor _componentProcessor;

        // Per-tick cache (no repeat work within one update)
        private readonly Dictionary<string, float> _tickCache = new();

        // Sensor object cache
        private Dictionary<string, ISensor> _manualSensorCache = new();

        private const float AutoMoboTempHardMax = 95f;
        private const float ManualMoboTempHardMax = 125f;

        // Settings version (re-warms the caches when it changes)
        private string _lastPrefCpuFan = "";
        private string _lastPrefCpuPump = "";
        private string _lastPrefCaseFan = "";
        private string _lastPrefMoboTemp = "";
        private string _lastPrefDisk = "";
        private string _lastPrefNet = "";
        private string _lastPrefGpu = "";
        
        public HardwareValueProvider(Computer c, Settings s, SensorMap map, NetworkManager net, DiskManager disk, PerformanceCounterManager perfManager, object syncLock, Dictionary<string, float> lastValid)
        {
            _computer = c;
            _cfg = s;
            _sensorMap = map;
            _networkManager = net;
            _diskManager = disk;
            _perfManager = perfManager;
            _lock = syncLock;
            _lastValidMap = lastValid;

            // Helpers
            _componentProcessor = new ComponentProcessor(c, s, map);
        }

        // Clear and re-warm the caches (after a hardware reload or settings change)
        public void PreCacheAllSensors(SensorMap map)
        {
            var newCache = map.GetInternalMap();

            // Remember which settings were warmed
            _lastPrefCpuFan = _cfg.PreferredCpuFan;
            _lastPrefCpuPump = _cfg.PreferredCpuPump;
            _lastPrefCaseFan = _cfg.PreferredCaseFan;
            _lastPrefMoboTemp = _cfg.PreferredMoboTemp;
            _lastPrefDisk = _cfg.PreferredDisk;
            _lastPrefNet = _cfg.PreferredNetwork;
            _lastPrefGpu = _cfg.PreferredGpu ?? "";

            // 1. The user's preferred sensors (fans, pump, motherboard temperature)
            string[] preferredKeys = { "CPU.Fan", "CPU.Pump", "CASE.Fan", "MOBO.Temp" };
            foreach (var key in preferredKeys)
            {
                string pref = (key == "CPU.Fan") ? _lastPrefCpuFan :
                             (key == "CPU.Pump") ? _lastPrefCpuPump :
                             (key == "CASE.Fan") ? _lastPrefCaseFan : _lastPrefMoboTemp;

                SensorType type = (key == "MOBO.Temp") ? SensorType.Temperature : SensorType.Fan;

                // "自动" is "Auto" in settings saved by the Chinese LiteMonitor
                if (!string.IsNullOrEmpty(pref) && !pref.Contains("自动") && !pref.Contains("Auto"))
                {
                    var s = FindSensorReverse(pref, type);
                    if (s != null) newCache[key] = s;
                }
            }

            // 2. Preferred disk (DISK.*)
            if (!string.IsNullOrEmpty(_lastPrefDisk))
            {
                var disk = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Storage && h.Name.Equals(_lastPrefDisk, StringComparison.OrdinalIgnoreCase));
                if (disk != null)
                {
                    foreach (var s in disk.Sensors)
                    {
                        if (s.SensorType == SensorType.Throughput)
                        {
                            if (SensorMap.Has(s.Name, "read")) newCache["DISK.Read"] = s;
                            else if (SensorMap.Has(s.Name, "write")) newCache["DISK.Write"] = s;
                        }
                    }
                    
                    // Temperature selection (same rules as DiskManager)
                    var bestTemp = DiskManager.FindBestTempSensor(disk);
                    if (bestTemp != null) newCache["DISK.Temp"] = bestTemp;
                }
            }

            // 3. Preferred network adapter (NET.*)
            if (!string.IsNullOrEmpty(_lastPrefNet))
            {
                var net = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Network && h.Name.Equals(_lastPrefNet, StringComparison.OrdinalIgnoreCase));
                if (net != null)
                {
                    foreach (var s in net.Sensors)
                    {
                        if (s.SensorType == SensorType.Throughput)
                        {
                            if (SensorMap.Has(s.Name, "upload") || SensorMap.Has(s.Name, "sent") || SensorMap.Has(s.Name, "up")) newCache["NET.Up"] = s;
                            else if (SensorMap.Has(s.Name, "download") || SensorMap.Has(s.Name, "received") || SensorMap.Has(s.Name, "down")) newCache["NET.Down"] = s;
                        }
                    }
                }
            }

            // 4. Swap the cache in
            lock (_lock)
            {
                _manualSensorCache = newCache;
                _tickCache.Clear();
                _componentProcessor.ClearCache();
            }
        }

        /// <summary>
        /// Finds the sensor for a saved "SensorName [HardwareName]" string
        /// </summary>
        private ISensor? FindSensorReverse(string savedString, SensorType type)
        {
            if (string.IsNullOrEmpty(savedString) || savedString.Contains("Auto") || savedString.Contains("自动")) 
                return null;

            int idx = savedString.LastIndexOf('[');
            if (idx < 0) return null; 

            string targetSensorName = savedString.Substring(0, idx).Trim();
            string targetHardwareName = savedString.Substring(idx + 1).TrimEnd(']');

            ISensor? SearchBranch(IHardware h)
            {
                foreach (var s in h.Sensors)
                {
                    if (s.SensorType == type && s.Name == targetSensorName)
                        return s; 
                }
                foreach (var sub in h.SubHardware)
                {
                    var s = SearchBranch(sub);
                    if (s != null) return s;
                }
                return null;
            }

            foreach (var hw in _computer.Hardware)
            {
                if (hw.Name == targetHardwareName)
                {
                    return SearchBranch(hw);
                }
            }
            return null;
        }

        public void ClearCache()
        {
            lock (_lock)
            {
                _manualSensorCache.Clear();
                _tickCache.Clear();
                _componentProcessor.ClearCache();
            }
        }

        /// <summary>
        /// Start of each update: clear the tick cache, check settings changes, update the power state
        /// </summary>
        public void OnUpdateTickStarted()
        {
            lock (_lock)
            {
                _tickCache.Clear();

                bool gpuChanged = _lastPrefGpu != (_cfg.PreferredGpu ?? "");
                if (gpuChanged)
                {
                    _sensorMap.Rebuild(_computer, _cfg);
                }

                // A changed preferred fan / disk / GPU re-warms the caches straight away
                if (gpuChanged ||
                    _lastPrefCpuFan != _cfg.PreferredCpuFan ||
                    _lastPrefCpuPump != _cfg.PreferredCpuPump ||
                    _lastPrefCaseFan != _cfg.PreferredCaseFan ||
                    _lastPrefMoboTemp != _cfg.PreferredMoboTemp ||
                    _lastPrefDisk != _cfg.PreferredDisk ||
                    _lastPrefNet != _cfg.PreferredNetwork)
                {
                    PreCacheAllSensors(_sensorMap);
                }
            }
        }


        // ===========================================================
        // ===================== Reading values =========================
        // ===========================================================
        public float? GetStartupValue(string key)
        {
            if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;

            bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;
            if (!useCounter) return null;

            if (key == "CPU.Load")
            {
                var cpuLoad = _perfManager.GetCpuLoad();
                return cpuLoad.HasValue ? Math.Clamp(cpuLoad.Value, 0f, 100f) : null;
            }

            if (key == "CPU.Clock")
            {
                return _perfManager.GetCpuFreq();
            }

            if (key == "MEM.Load")
            {
                var memData = _perfManager.GetMemoryData();
                if (memData.Load.HasValue && Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                {
                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                }
                return memData.Load;
            }

            if (key == "DISK.Read") return _perfManager.GetDiskRead();
            if (key == "DISK.Write") return _perfManager.GetDiskWrite();
            if (key == "DISK.Activity")
            {
                var diskActive = _perfManager.GetDiskActive();
                return diskActive.HasValue ? Math.Clamp(diskActive.Value, 0f, 100f) : null;
            }

            return null;
        }

        public float? GetValue(string key)
        {
            // TryEnter, so the UI never waits on a background reload:
            // no lock within 10 ms (hardware reloading) -> no fresh value
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_lock, 10, ref lockTaken);
                if (!lockTaken) 
                {
                    // Lock busy (reloading): return the last good value so the panel does not flicker.
                    // _lastValidMap is only written here (UI thread) and never by the background thread,
                    // so reading it without the lock is safe.
                    if (_lastValidMap.TryGetValue(key, out float lastVal)) return lastVal;
                    return null;
                }

                // Already worked out this tick: return it
                if (_tickCache.TryGetValue(key, out float cachedVal)) return cachedVal;



                // Result
                float? result = null;
                bool skipGenericFallback = false;
                
                // Use performance counters only when switched on and ready
                bool useCounter = _cfg.UseWinPerCounters && _perfManager.IsInitialized;

                // switch for a direct jump
                switch (key)
                {
                    // 1. CPU.Load
                    case "CPU.Load":
                        if (useCounter)
                        {
                            result = _perfManager.GetCpuLoad();
                            if (result.HasValue && result.Value > 100f) result = 100f;
                        }

                        if (result == null)
                        {
                            // Prefer LibreHardwareMonitor's "Total" sensor (matches Task Manager)
                            // before averaging the cores
                            if (_manualSensorCache.TryGetValue("CPU.Load", out var totalSensor) && totalSensor.Value.HasValue)
                            {
                                result = totalSensor.Value.Value;
                            }

                            // No Total sensor: average the cores
                            if (result == null)
                            {
                                result = _componentProcessor.GetCpuLoad();
                            }

                            if (result == null) result = 0f;
                        }
                        break;

                    // 2. CPU.Temp
                    case "CPU.Temp":
                        result = _componentProcessor.GetCpuTemp();
                        if (result == null && _manualSensorCache.TryGetValue("CPU.Temp", out var fallbackT))
                        {
                            result = fallbackT.Value;
                        }
                        if (result == null) result = 0f;
                        break;

                    // 4. Daily traffic
                    case "DATA.DayUp":
                        result = TrafficLogger.GetTodayStats().up;
                        break;
                    case "DATA.DayDown":
                        result = TrafficLogger.GetTodayStats().down;
                        break;

                    // 6. Memory
                    case "MEM.Load":
                        if (useCounter)
                        {
                            var memData = _perfManager.GetMemoryData();
                            if (memData.Load.HasValue)
                            {
                                result = memData.Load.Value;
                                if (Settings.DetectedRamTotalGB <= 0 && _perfManager.TotalMemoryGB > 0.1f)
                                {
                                    Settings.DetectedRamTotalGB = _perfManager.TotalMemoryGB;
                                }
                            }
                        }

                        if (result == null)
                        {
                            // 1. Used and Available
                            // (the most reliable: gives the load and the total size)
                            float? usedVal = null;
                            float? availVal = null;
                            if (_manualSensorCache.TryGetValue("MEM.Used", out var u) && u.Value.HasValue) usedVal = u.Value.Value;
                            if (_manualSensorCache.TryGetValue("MEM.Available", out var a) && a.Value.HasValue) availVal = a.Value.Value;

                            // 2. Load = Used / (Used + Available)
                            // (sets both the percentage and the total, never one without the other)
                            if (usedVal.HasValue && availVal.HasValue)
                            {
                                float memTotal = usedVal.Value + availVal.Value;
                                if (memTotal > 0)
                                {
                                    // Total size (for size display mode)
                                    if (Settings.DetectedRamTotalGB <= 0)
                                    {
                                        Settings.DetectedRamTotalGB = memTotal > 512.0f ? memTotal / 1024.0f : memTotal;
                                    }

                                    // Load percentage
                                    result = (usedVal.Value / memTotal) * 100.0f;
                                }
                            }

                            // 3. Fallback: the native Load sensor
                            if (result == null && _manualSensorCache.TryGetValue("MEM.Load", out var sLoad) && sLoad.Value.HasValue)
                            {
                                result = sLoad.Value.Value;
                            }
                        }
                        else
                        {
                            break; 
                        }
                        break;

                    // 7. Video memory
                    case "GPU.VRAM":
                        float? used = GetValue("GPU.VRAM.Used");
                        float? total = GetValue("GPU.VRAM.Total");
                        if (used.HasValue && total.HasValue && total > 0)
                        {
                            if (Settings.DetectedGpuVramTotalGB <= 0) Settings.DetectedGpuVramTotalGB = total.Value / 1024f;
                            if (total > 10485760) { used /= 1048576f; total /= 1048576f; }
                            result = used / total * 100f;
                        }
                        else
                        {
                            if (_manualSensorCache.TryGetValue("GPU.VRAM.Load", out var s) && s.Value.HasValue) result = s.Value;
                        }
                        break;

                    // 8. Fans / pump (records maximums)
                    case "CPU.Fan":
                    case "CPU.Pump":
                    case "CASE.Fan":
                    case "GPU.Fan":
                        if (_manualSensorCache.TryGetValue(key, out var sFan))
                        {
                            result = sFan.Value;
                        }
                        else 
                        {
                            if (_manualSensorCache.TryGetValue(key, out var autoS) && autoS.Value.HasValue)
                                result = autoS.Value.Value;
                        }
                        if (result.HasValue && result.Value < 10000f) 
                            _cfg.UpdateMaxRecord(key, result.Value);
                        break;

                    // Motherboard temperature
                    case "MOBO.Temp":
                        skipGenericFallback = true;
                        if (_manualSensorCache.TryGetValue(key, out var sMobo))
                        {
                            result = ReadMoboTemperature(sMobo);
                        }
                        break;

                    // Battery
                    case "BAT.Percent":
                    case "BAT.Power":
                    case "BAT.Voltage":
                    case "BAT.Current":
                        result = BatteryService.GetBatteryValue(key, _manualSensorCache);
                        break;

                    // Default: prefix / contains matching
                    default:
                        if (key.StartsWith("NET"))
                        {
                            if (_manualSensorCache.TryGetValue(key, out var sNet))
                            {
                                result = sNet.Value;
                            }
                            
                            if (result == null)
                            {
                                result = _networkManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        else if (key.StartsWith("DISK"))
                        {
                            bool isSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
                            if (useCounter && !isSpecificDisk)
                            {
                                if (key == "DISK.Read") result = _perfManager.GetDiskRead();
                                else if (key == "DISK.Write") result = _perfManager.GetDiskWrite();
                                else if (key == "DISK.Activity") 
                                {
                                    result = _perfManager.GetDiskActive();
                                    if (result.HasValue) result = Math.Clamp(result.Value, 0f, 100f);
                                }
                            }

                            if (result == null && _manualSensorCache.TryGetValue(key, out var sDisk))
                            {
                                result = sDisk.Value;
                            }

                            if (result == null)
                            {
                                result = _diskManager.GetBestValue(key, _computer, _cfg, _lastValidMap, _lock);
                            }
                        }
                        else if (key.Contains("Clock") || key.Contains("Power"))
                        {
                            if (useCounter && key == "CPU.Clock")
                            {
                                result = _perfManager.GetCpuFreq();
                            }
                            
                            if (result == null)
                            {
                                result = _componentProcessor.GetCompositeValue(key, _manualSensorCache);
                            }
                        }
                        break;
                }

                // 10. Generic sensor lookup (fallback)
                // straight from the cache (no lock)
                if (!skipGenericFallback && result == null && _manualSensorCache.TryGetValue(key, out var sGen))
                {
                    var val = sGen.Value;
                    if (val.HasValue && !float.IsNaN(val.Value)) 
                    { 
                        _lastValidMap[key] = val.Value; 
                        result = val.Value; 
                    }
                    else if (_lastValidMap.TryGetValue(key, out var last))
                    {
                        result = last;
                    }
                }

                // Cache and return
                if (result.HasValue)
                {
                    _tickCache[key] = result.Value;
                    return result.Value;
                }

                return null;
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_lock);
            }
        }

        private float? ReadMoboTemperature(ISensor sensor)
        {
            bool manualSensor = IsManualMoboTemperatureSelected();
            var raw = sensor.Value;
            if (!raw.HasValue || float.IsNaN(raw.Value) || float.IsInfinity(raw.Value) || raw.Value <= 0f)
            {
                return GetLastValidMoboTemperature(manualSensor);
            }

            float value = raw.Value;
            bool overHardMax = manualSensor ? value > ManualMoboTempHardMax : value >= AutoMoboTempHardMax;
            if (overHardMax)
            {
                return GetLastValidMoboTemperature(manualSensor);
            }

            _lastValidMap["MOBO.Temp"] = value;
            return value;
        }

        private bool IsManualMoboTemperatureSelected()
        {
            string pref = _cfg.PreferredMoboTemp ?? "";
            return !string.IsNullOrWhiteSpace(pref) &&
                   !pref.Contains("自动", StringComparison.OrdinalIgnoreCase) &&
                   !pref.Contains("Auto", StringComparison.OrdinalIgnoreCase);
        }

        private float? GetLastValidMoboTemperature(bool manualSensor)
        {
            float max = manualSensor ? ManualMoboTempHardMax : AutoMoboTempHardMax;
            return _lastValidMap.TryGetValue("MOBO.Temp", out float last) &&
                   last > 0f &&
                   last <= max
                ? last
                : null;
        }

        public void Dispose()
        {
        }
    }
}
