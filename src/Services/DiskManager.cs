using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    public class DiskManager
    {
        // Last activity per disk (to let idle disks sleep)
        private Dictionary<IHardware, DateTime> _diskLastActiveTime = new Dictionary<IHardware, DateTime>();
        // Selected disk cache
        private IHardware? _cachedDiskHw;
        private DateTime _lastDiskScan = DateTime.MinValue;
        // DriveInfo cache (avoids re-creating them)
        private static readonly Dictionary<string, DriveInfo> _driveInfoCache = new();

        public void ClearCache()
        {
            _diskLastActiveTime.Clear();
            _cachedDiskHw = null;
            // The DriveInfo cache can stay (drives rarely change)
            _driveInfoCache.Clear();
        }

        // ===========================================================
        // Update
        // ===========================================================
        public void ProcessUpdate(IHardware hw, Settings cfg, bool isSlowScanTick, bool needDiskBgScan)
        {
            // 1. A preferred disk always wins
            if (!string.IsNullOrEmpty(cfg.PreferredDisk) && 
                !hw.Name.Equals(cfg.PreferredDisk, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Start the activity record
            if (!_diskLastActiveTime.ContainsKey(hw)) _diskLastActiveTime[hw] = DateTime.Now;

            // Is it the disk shown on the panel?
            bool isTarget = (_cachedDiskHw != null && hw == _cachedDiskHw) ||
                            (hw.Name == cfg.LastAutoDisk) ||
                            (hw.Name == cfg.PreferredDisk);
            
            bool shouldUpdate = false;
            double idleMinutes = (DateTime.Now - _diskLastActiveTime[hw]).TotalMinutes;

            // === Back off on idle disks ===
            if (isTarget)
            {
                shouldUpdate = true;
            }
            else
            {
                // B. Other disks:
                if (idleMinutes > 5) shouldUpdate = false; // [sleep: idle > 5 min]
                else if (idleMinutes > 1) { if (needDiskBgScan) shouldUpdate = true; } // [cooling: idle > 1 min]
                else { if (isSlowScanTick) shouldUpdate = true; } // 
            }

            // Update
            if (shouldUpdate)
            {
                hw.Update();
                // Traffic resets the idle timer
                bool hasTraffic = false;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Throughput && s.Value.HasValue && s.Value.Value > 1024) // > 1KB/s
                    {
                        hasTraffic = true;
                        break;
                    }
                }
                if (hasTraffic) _diskLastActiveTime[hw] = DateTime.Now;
            }
        }

        // ===========================================================
        // Best disk value (a preferred disk is handled by HardwareValueProvider)
        // ===========================================================
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // 1. Runtime cache (automatic mode)
            if (_cachedDiskHw != null)
            {
                // Drop the cached disk if it no longer exists
                if (!computer.Hardware.Contains(_cachedDiskHw))
                {
                    _cachedDiskHw = null;
                }
                else
                {
                    float? cachedVal = ReadDiskSensor(_cachedDiskHw, key, lastValidMap, syncLock);
                    // Active, or still in the cool-down: use it
                    // temperature too
                    if ((cachedVal.HasValue && cachedVal.Value > 0.1f) || key.Contains("Temp") || (DateTime.Now - _lastDiskScan).TotalSeconds < 10)
                        return cachedVal;
                }
            }
            // === Partitions, via the DriveInfo cache ===
            // key format: DISK.C.Used
            var parts = key.Split('.');
            if (parts.Length >= 3 && parts[1].Length == 1 && char.IsLetter(parts[1][0]))
            {
                string driveLetter = parts[1]; // "C"
                string type = parts[2];        // "Used"
                string root = driveLetter + ":\\";

                DriveInfo di;
                // Cache first
                if (!_driveInfoCache.TryGetValue(root, out di))
                {
                    try 
                    { 
                        di = new DriveInfo(root); 
                        _driveInfoCache[root] = di; // cache it
                    } 
                    catch { return null; }
                }

                if (type == "Used") 
                {
                    try 
                    { 
                        // IsReady does not allocate the volume string
                        if (di.IsReady) return (100f - (float)((double)di.TotalFreeSpace / di.TotalSize * 100.0));
                    } 
                    catch { return 0f; }
                }
            }
            // B. Disk remembered in the settings
            if (_cachedDiskHw == null && !string.IsNullOrEmpty(cfg.LastAutoDisk))
            {
                var savedHw = computer.Hardware.FirstOrDefault(h => h.Name == cfg.LastAutoDisk);
                if (savedHw != null)
                {
                    _cachedDiskHw = savedHw;
                    _lastDiskScan = DateTime.Now;
                    return ReadDiskSensor(savedHw, key, lastValidMap, syncLock);
                }
            }

            // C. Scan all disks
            string sysPrefix = "";
            try { sysPrefix = Path.GetPathRoot(Environment.SystemDirectory)?.Substring(0, 2) ?? ""; } catch { }

            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
            {
                bool isSystem = !string.IsNullOrEmpty(sysPrefix) && (SensorMap.Has(hw.Name, sysPrefix) || hw.Sensors.Any(s => SensorMap.Has(s.Name, sysPrefix)));
                ISensor? read = null, write = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (SensorMap.Has(s.Name, "read")) read ??= s;
                    if (SensorMap.Has(s.Name, "write")) write ??= s;
                }
                if (read == null && write == null) continue;

                double score = (read?.Value ?? 0) + (write?.Value ?? 0);
                if (isSystem) score += 1e9; // system disk first

                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "DISK.Read") ? read : write;
                }
            }

            // D. Update the cache
            if (bestHw != null)
            {
                _cachedDiskHw = bestHw;
                _lastDiskScan = DateTime.Now;
                
                // Remember the choice
                if (cfg.LastAutoDisk != bestHw.Name)
                {
                    cfg.LastAutoDisk = bestHw.Name;
                }
            }

            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            
            // Temperature fallback
            if (key == "DISK.Temp" && bestHw != null) return ReadDiskSensor(bestHw, key, lastValidMap, syncLock);

            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        public static ISensor? FindBestTempSensor(IHardware hw)
        {
            ISensor? best = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType == SensorType.Temperature)
                {
                    // Skip unrelated sensors
                    if (s.Name.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        s.Name.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (s.Name == "Temperature") return s;
                    if (best == null) best = s;
                }
            }
            return best;
        }

        private float? ReadDiskSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // Temperature
            if (key == "DISK.Temp")
            {
                var best = FindBestTempSensor(hw);
                if (best != null) return SafeRead(best, key, lastValidMap, syncLock);
                return null;
            }

            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "DISK.Read" && SensorMap.Has(s.Name, "read")) return SafeRead(s, key, lastValidMap, syncLock);
                if (key == "DISK.Write" && SensorMap.Has(s.Name, "write")) return SafeRead(s, key, lastValidMap, syncLock);
            }
            return SafeRead(null, key, lastValidMap, syncLock);
        }

        private float? SafeRead(ISensor? s, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            if (s?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }
    }
}