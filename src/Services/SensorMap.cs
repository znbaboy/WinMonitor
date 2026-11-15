using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    /// <summary>
    /// Maps hardware sensors to standard keys (e.g. CPU.Temp)
    /// </summary>
    public class SensorMap
    {
        // Key -> sensor
        private readonly Dictionary<string, ISensor> _map = new();
        
        // Fan matching
        private readonly FanMapper _fanMapper = new FanMapper();

        // Fast caches
        // CPU core load/clock pairs (for the weighted average)
        public class CpuCoreSensors
        {
            public ISensor? Clock;
            public ISensor? Load;
        }
        public List<CpuCoreSensors> CpuCoreCache { get; private set; } = new();
        
        // GPU hardware cache
        public IHardware? CachedGpu { get; private set; }
        // Strict single-GPU refresh only when the settings match exactly one GPU.
        public bool HasExplicitGpuSelection { get; private set; }
        // Bus speed sensor (for the Zen 5 clock correction)
        public ISensor? CpuBusSpeedSensor { get; private set; }

        private DateTime _lastMapBuild = DateTime.MinValue;
        
        private readonly object _lock = new object();
        
        // Settings
        private Settings _cfg;

        // Simple rebuild rule:
        // build once at start, then only when asked or by the 10-minute safety rebuild.
        // Returns whether it rebuilt, so the value cache can follow.
        public bool EnsureFresh(Computer computer, Settings cfg)
        {
            if ((DateTime.Now - _lastMapBuild).TotalMinutes > 10)
            {
                Rebuild(computer, cfg);
                return true;
            }
            return false;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                CpuCoreCache.Clear();
                CachedGpu = null;
                HasExplicitGpuSelection = false;
                CpuBusSpeedSensor = null;
            }
        }



        public bool TryGetSensor(string key, out ISensor? sensor)
        {
            lock (_lock)
            {
                return _map.TryGetValue(key, out sensor);
            }
        }

        /// <summary>
        /// A copy of the map (for HardwareValueProvider's cache)
        /// </summary>
        public Dictionary<string, ISensor> GetInternalMap()
        {
            lock (_lock)
            {
                return new Dictionary<string, ISensor>(_map);
            }
        }

        public bool IsMappedGpuSensorHardware(IHardware hw)
        {
            lock (_lock)
            {
                foreach (var kv in _map)
                {
                    if (kv.Key.StartsWith("GPU.", StringComparison.OrdinalIgnoreCase) &&
                        ReferenceEquals(kv.Value.Hardware, hw))
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        // =======================================================================
        // Builds the sensor map and caches
        // =======================================================================
        public void Rebuild(Computer computer, Settings cfg)
        {
            _cfg = cfg;
            // 1. New containers (swapped in at the end)
            var newMap = new Dictionary<string, ISensor>(StringComparer.OrdinalIgnoreCase); // case-insensitive keys
            var newCpuCache = new List<CpuCoreSensors>();
            IHardware? newGpu = null;
            ISensor? newBusSensor = null; // temporary
            
            // Candidates for smart matching
            // (fans are matched later by FanMapper;
            // motherboard temperatures are collected here)
            var candidatesMoboTemps = new List<ISensor>(capacity: 10); // pre-sized

            string preferredGpu = cfg.PreferredGpu ?? "";
            IHardware? selectedGpu = HardwareScanner.FindGpuByConfig(computer, preferredGpu);
            bool shouldWarmGpuForMapping = cfg.IsAnyEnabled("GPU");

            // Recursive walk
            void RegisterTo(IHardware hw)
            {
                bool isGpuHardware = HardwareScanner.IsGpuHardware(hw);

                // A chosen GPU that still exists: map only that one; otherwise automatic priority.
                if (isGpuHardware &&
                    selectedGpu != null &&
                    !ReferenceEquals(hw, selectedGpu))
                {
                    return;
                }

                if (isGpuHardware && shouldWarmGpuForMapping)
                {
                    TryUpdateGpuForMapping(hw);
                }

                // --- CPU caches (weighted average) ---
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    // Bus speed sensor:
                    // prefer the clock sensor named "Bus Speed"
                    if (newBusSensor == null)
                    {
                        newBusSensor = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && s.Name.Contains("Bus Speed"));
                    }

                    // All core clocks (not the bus)
                    var clocks = hw.Sensors.Where(s => s.SensorType == SensorType.Clock && Has(s.Name, "core") && !Has(s.Name, "bus"));
                    
                    foreach (var clock in clocks)
                    {
                        // AMD names the load "CPU Core #1" and the clock "Core #1":
                        // EndsWith matches both Intel (same name) and AMD (prefix)
                        var load = hw.Sensors.FirstOrDefault(s => 
                            s.SensorType == SensorType.Load && 
                            s.Name.EndsWith(clock.Name, StringComparison.OrdinalIgnoreCase)); 
                            
                        newCpuCache.Add(new CpuCoreSensors { Clock = clock, Load = load });
                    }
                }

                // --- GPU cache (by priority) ---
                if (isGpuHardware)
                {
                    // Hardware is visited strongest GPU first (GetHwPriority),
                    // so the first GPU seen is the one to use.
                    if (newGpu == null)
                    {
                        newGpu = hw;
                    }
                    
                    // GPU fan
                    var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan);
                    if (fan == null) fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);
                    if (fan != null) newMap.TryAdd("GPU.Fan", fan);
                }
                
                // Motherboard / SuperIO sensors
                if (hw.HardwareType == HardwareType.Motherboard || hw.HardwareType == HardwareType.SuperIO)
                {
                    foreach (var s in hw.Sensors)
                    {
                        // (fans come from FanMapper; only temperatures here)
                        if (s.SensorType == SensorType.Temperature) candidatesMoboTemps.Add(s);
                    }
                }

                // --- Other sensors ---
                foreach (var s in hw.Sensors)
                {
                    string? key = SensorMatcher.Match(hw, s); // SensorMatcher works out the key
                    if (!string.IsNullOrEmpty(key))
                    {
                        if (!newMap.ContainsKey(key))
                        {
                            newMap[key] = s;
                        }
                        else
                        {
                            // Conflicts: prefer vendor sensors over D3D ones,
                            // and never let a weaker GPU overwrite a stronger one.
                            var existing = newMap[key];
                            
                            // 1. Existing value from a stronger GPU (lower priority number): keep it.
                            // (Visited in priority order, so existing is never weaker than this one.)
                            if (HardwareRules.GetHwPriority(existing.Hardware) < HardwareRules.GetHwPriority(hw))
                            {
                                continue;
                            }

                            // 2. Same priority (same card, or two identical cards):
                            // prefer vendor over D3D.
                            bool existingIsD3D = existing.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            bool newIsD3D = s.Name.Contains("D3D", StringComparison.OrdinalIgnoreCase);
                            
                            // Existing is D3D and the new one is not: replace
                            if (existingIsD3D && !newIsD3D)
                            {
                                newMap[key] = s;
                            }
                            // Otherwise keep the existing one (first / vendor wins)
                        }
                    }
                }

                foreach (var sub in hw.SubHardware) RegisterTo(sub);
            }

            // Register in priority order
            var ordered = computer.Hardware.OrderBy(h => HardwareRules.GetHwPriority(h));
            foreach (var hw in ordered) RegisterTo(hw);
            
            // ============================================
            // Smart matching
            // ============================================
            
            // A. Motherboard temperature (System > Motherboard > Chipset > highest)
            if (!newMap.ContainsKey("MOBO.Temp") && candidatesMoboTemps.Count > 0)
            {
                // 1. Standard names (System, Motherboard)
                var best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "System"));
                if (best == null) best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "Motherboard"));
                
                // 2. Chipset
                if (best == null) best = candidatesMoboTemps.FirstOrDefault(x => Has(x.Name, "Chipset") || Has(x.Name, "PCH"));
                
                // 3. Fallback: the highest valid value (no 0 or 200+ readings)
                if (best == null)
                {
                    // Prefer the highest value in a plausible range (15-68 °C), usually the system temperature;
                    // only without one, the highest in 0-95 °C (maybe VRM or chipset).
                    // (Some boards, e.g. Asus Z790, report a Temperature #5 that jumps above 100.)
                    
                    ISensor? bestSafe = null;
                    float maxSafe = -999f;
                    
                    ISensor? bestFallback = null;
                    float maxFallback = -999f;

                    foreach (var t in candidatesMoboTemps)
                    {
                        if (!t.Value.HasValue) continue;
                        float v = t.Value.Value;
                        
                        // 1. Wide range (0 - 95), anything above 95 is ignored
                        if (v > 0 && v < 95 && v > maxFallback)
                        {
                            maxFallback = v;
                            bestFallback = t;
                        }
                        
                        // 2. Plausible range (15 - 68)
                        // 68 °C is about the limit for board / system sensors; above it is likely chipset or VRM
                        if (v >= 15 && v <= 68 && v > maxSafe)
                        {
                            maxSafe = v;
                            bestSafe = t;
                        }
                    }
                    
                    // Plausible range first
                    best = bestSafe ?? bestFallback;
                }
                
                if (best != null) newMap["MOBO.Temp"] = best;
            }

            // B. Fans: matched only when fan / pump items are shown (no needless controller scans at startup).
            if (cfg.IsAnyEnabled("CPU.Fan") ||
                cfg.IsAnyEnabled("CPU.Pump") ||
                cfg.IsAnyEnabled("CASE.Fan"))
            {
                _fanMapper.ScanAndMapFans(computer, cfg, newMap);
            }

            // 2. Swap the data in (locked)
            lock (_lock)
            {
                _map.Clear();
                foreach (var kv in newMap) _map[kv.Key] = kv.Value;
                
                CpuCoreCache = newCpuCache;
                CachedGpu = newGpu;
                HasExplicitGpuSelection = selectedGpu != null;
                CpuBusSpeedSensor = newBusSensor; // bus sensor
                
                _lastMapBuild = DateTime.Now;
            }
        }

        // Shared string matching (HardwareRules)
        public static bool Has(string source, string sub) => HardwareRules.Has(source, sub);

        private static void TryUpdateGpuForMapping(IHardware hw)
        {
            try
            {
                hw.Update();
            }
            catch
            {
                // A failed GPU update keeps the old mapping, so the other hardware is not affected.
            }
        }
    }
}
