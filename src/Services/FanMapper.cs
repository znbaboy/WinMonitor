using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    /// <summary>
    /// Finds CPU fan, pump and case fan sensors
    /// </summary>
    public class FanMapper
    {
        
        public void ScanAndMapFans(Computer computer, Settings cfg, Dictionary<string, ISensor> targetMap)
        {
            ISensor? cpuFan = null;
            ISensor? cpuPump = null; 
            ISensor? caseFan = null;
            
            // 1. Collect every fan that is spinning (with a block list)
            var activeFans = new List<(IHardware Hw, ISensor S, float Rpm)>();
            
            void CollectFans(IHardware hw)
            {
                // Never use these fans:
                bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia ||
                                  hw.HardwareType == HardwareType.GpuAmd ||
                                  hw.HardwareType == HardwareType.GpuIntel ||
                                  hw.HardwareType == HardwareType.Cpu ||
                                  hw.HardwareType == HardwareType.Storage ||
                                  hw.HardwareType == HardwareType.Memory ||
                                  hw.HardwareType == HardwareType.Network;

                if (!isExcluded)
                {
                    hw.Update();
                    foreach (var s in hw.Sensors)
                    {
                        if (s.SensorType == SensorType.Fan && s.Value.HasValue)
                        {
                            // ignore readings below 200 RPM
                            if (s.Value.Value > 200)
                            {
                                activeFans.Add((hw, s, s.Value.Value));
                            }
                        }
                    }
                }
                foreach (var sub in hw.SubHardware) CollectFans(sub);
            }
            foreach (var hw in computer.Hardware) CollectFans(hw);

            // Already assigned?
            bool IsTaken(ISensor s) => s == cpuFan || s == cpuPump || s == caseFan;
            
            // -----------------------------------------------------------
            // Step 2: the user's own choice wins
            // -----------------------------------------------------------
            if (!string.IsNullOrEmpty(cfg.PreferredCpuFan)) cpuFan = FindFanByConfig(activeFans, cfg.PreferredCpuFan);
            if (!string.IsNullOrEmpty(cfg.PreferredCaseFan)) caseFan = FindFanByConfig(activeFans, cfg.PreferredCaseFan);
            if (!string.IsNullOrEmpty(cfg.PreferredCpuPump)) cpuPump = FindFanByConfig(activeFans, cfg.PreferredCpuPump);

            // -----------------------------------------------------------
            // Step 3: CPU fan
            // -----------------------------------------------------------
            if (cpuFan == null && activeFans.Count > 0)
            {
                var coolerFan = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                    IsCoolerHardware(x.Hw) && !SensorMap.Has(x.S.Name, "Pump"));
                
                if (coolerFan.S != null) cpuFan = coolerFan.S;
                else
                {
                    var namedCpu = activeFans.FirstOrDefault(x => !IsTaken(x.S) && SensorMap.Has(x.S.Name, "CPU"));
                    if (namedCpu.S != null) cpuFan = namedCpu.S;
                    else
                    {
                        var first = activeFans.FirstOrDefault(x => !IsTaken(x.S));
                        if (first.S != null) cpuFan = first.S;
                    }
                }
            }

            // -----------------------------------------------------------
            // Step 4: pump
            // -----------------------------------------------------------
            if (cpuPump == null && activeFans.Count > 0)
            {
                var coolerPump = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                    IsCoolerHardware(x.Hw) && 
                    (SensorMap.Has(x.S.Name, "Pump") || SensorMap.Has(x.S.Name, "Speed"))); 
                
                if (coolerPump.S == null)
                    coolerPump = activeFans.FirstOrDefault(x => !IsTaken(x.S) && IsCoolerHardware(x.Hw));

                if (coolerPump.S != null) cpuPump = coolerPump.S;
                else
                {
                    var namedPump = activeFans.FirstOrDefault(x => !IsTaken(x.S) && 
                        (SensorMap.Has(x.S.Name, "Pump") || SensorMap.Has(x.S.Name, "Water") || SensorMap.Has(x.S.Name, "AIO")));
                    if (namedPump.S != null) cpuPump = namedPump.S;
                }
            }

            // Guess a pump (above 3000 RPM)
            if (cpuPump == null)
            {
                var highSpeedCandidate = activeFans.Where(x => !IsTaken(x.S) && x.Rpm > 3000)
                                                   .OrderByDescending(x => x.Rpm)
                                                   .FirstOrDefault();
                if (highSpeedCandidate.S != null) cpuPump = highSpeedCandidate.S;
            }

            // -----------------------------------------------------------
            // Step 5: case fan
            // -----------------------------------------------------------
            if (caseFan == null && activeFans.Count > 0)
            {
                var leftovers = activeFans.Where(x => !IsTaken(x.S)).ToList();

                var best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Rear"));
                if (best.S == null) best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Chassis"));
                if (best.S == null) best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Sys"));
                if (best.S == null) best = leftovers.FirstOrDefault(x => SensorMap.Has(x.S.Name, "Case"));

                if (best.S != null) caseFan = best.S;
                else if (leftovers.Count > 0)
                {
                    var sorted = leftovers.OrderBy(x => x.Rpm).ToList();
                    caseFan = sorted.First().S; 
                    if (cpuPump == null && sorted.Count > 1) cpuPump = sorted.Last().S;
                }
            }

            // 3. Store the mapping
            if (cpuFan != null) targetMap["CPU.Fan"] = cpuFan;
            if (caseFan != null) targetMap["CASE.Fan"] = caseFan;
            if (cpuPump != null) targetMap["CPU.Pump"] = cpuPump;
        }

        private ISensor? FindFanByConfig(List<(IHardware Hw, ISensor S, float Rpm)> fans, string configStr)
        {
            foreach (var item in fans)
            {
                string uid = $"[{item.Hw.Name}] {item.S.Name}";
                if (uid == configStr || item.S.Name == configStr) return item.S;
            }
            return null;
        }

        private bool IsCoolerHardware(IHardware h) 
        {
            if (h.HardwareType == HardwareType.Cooler) return true;
            string n = h.Name;
            return SensorMap.Has(n, "Kraken") || SensorMap.Has(n, "Corsair") || SensorMap.Has(n, "Liquid") || SensorMap.Has(n, "AIO") || SensorMap.Has(n, "Cooler");
        }
    }
}