using System;
using LibreHardwareMonitor.Hardware;

namespace WinMonitor.Services
{
    /// <summary>
    /// Sensor matcher
    /// Turns raw sensor names into standard keys (e.g. "CPU.Temp")
    /// </summary>
    public static class SensorMatcher
    {
        // Shared string matching (HardwareRules)
        private static bool Has(string source, string sub) => HardwareRules.Has(source, sub);

        /// <summary>
        /// Matches a sensor to a standard key
        /// </summary>
        public static string? Match(IHardware hw, ISensor s)
        {
            string name = s.Name;
            var type = hw.HardwareType;

            // --- CPU ---
            if (type == HardwareType.Cpu)
            {
                // "package" too: some CPUs call the total load "CPU Package"
                if (s.SensorType == SensorType.Load)
                {
                    if (Has(name, "total") || Has(name, "package")) 
                        return "CPU.Load";
                }
                // CPU temperature:
                if (s.SensorType == SensorType.Temperature)
                {
                    // 1. Preferred names
                    if (Has(name, "package") || // Intel/AMD standard
                        Has(name, "average") || // LibreHardwareMonitor aggregate
                        Has(name, "tctl") || // AMD control temperature (most accurate)
                        Has(name, "tdie") || // AMD die temperature
                        Has(name, "ccd") || // AMD CCD
                        Has(name, "cores")) // generic core temperature
                    {
                        return "CPU.Temp";
                    }

                    // 2. Generic names as a fallback (e.g. mobile CPUs like the AMD 7840HS),
                    // excluding unrelated sensors (SoC, VRM, pump, ...)
                    if ((Has(name, "cpu") || Has(name, "core")) && 
                        !Has(name, "soc") && // not the SoC / integrated GPU
                        !Has(name, "vrm") && // not the VRM
                        !Has(name, "fan") && // not fans
                        !Has(name, "pump") && // not the pump
                        !Has(name, "liquid") && // not liquid
                        !Has(name, "coolant") && // not coolant
                        !Has(name, "distance")) // not "Distance to TjMax"
                    {
                        return "CPU.Temp";
                    }
                }
                if (s.SensorType == SensorType.Power && (Has(name, "package") || Has(name, "cores"))) return "CPU.Power";
                // CPU Voltage
                if (s.SensorType == SensorType.Voltage && (Has(name, "core") || Has(name, "cpu") || Has(name, "vcore") || Has(name, "vid"))) 
                {
                    // Exclude distractions
                    if (!Has(name, "soc") && !Has(name, "gt") && !Has(name, "sa") && !Has(name, "aux")) 
                        return "CPU.Voltage";
                }
            }

            // --- GPU ---
            if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                if (s.SensorType == SensorType.Load && (Has(name, "core") || Has(name, "d3d 3d"))) return "GPU.Load";
                if (s.SensorType == SensorType.Temperature && (Has(name, "core") || Has(name, "hot spot") || Has(name, "soc") || Has(name, "vr"))) return "GPU.Temp";
                
                // Video memory:
                // 1. Integrated GPUs prefer shared memory (HardwareRules)
                bool preferShared = HardwareRules.ShouldUseSharedMemory(hw);

                if (s.SensorType == SensorType.SmallData && (Has(name, "memory") || Has(name, "dedicated") || Has(name, "shared")))
                {
                    bool isShared = Has(name, "shared");
                    bool isDedicated = Has(name, "dedicated");
                    // Generic "Memory Used" (Vendor sensors often just say "Memory Used")
                    bool isGenericMem = !isShared && !isDedicated && Has(name, "memory");

                    if (preferShared)
                    {
                        // Integrated: Only accept Shared Memory
                        if (isShared && Has(name, "used")) return "GPU.VRAM.Used";
                        if (isShared && Has(name, "total")) return "GPU.VRAM.Total";
                        
                        // Fallback: no shared memory (some old drivers) but generic memory: use it,
                        // never dedicated (integrated GPUs have none, or a tiny fake one)
                        if (isGenericMem && Has(name, "used")) return "GPU.VRAM.Used";
                    }
                    else
                    {
                        // Discrete: Accept Dedicated or Generic Memory (Block Shared)
                        if (!isShared) 
                        {
                            if (Has(name, "used")) return "GPU.VRAM.Used";
                            if (Has(name, "total")) return "GPU.VRAM.Total";
                        }
                    }
                }
                
                if (s.SensorType == SensorType.Load && Has(name, "memory")) return "GPU.VRAM.Load";
            }

            // --- Memory ---
            if (type == HardwareType.Memory) 
            {
                // Never virtual memory
                if (Has(hw.Name, "virtual")) return null;
                
                // 1. Load
                // Also accepts a load sensor named just "Memory" (common in LibreHardwareMonitor)
                if (s.SensorType == SensorType.Load)
                {
                     if (Has(name, "memory")) return "MEM.Load";
                     // or one named "Load"
                     if (name.Equals("Load", StringComparison.OrdinalIgnoreCase)) return "MEM.Load";
                }
                
                // 2. Data and SmallData
                if (s.SensorType == SensorType.Data || s.SensorType == SensorType.SmallData)
                {
                    if (Has(name, "used")) return "MEM.Used";
                    // Free too (some systems lack Available or call it Free)
                    if (Has(name, "available") || Has(name, "free")) return "MEM.Available";
                }
            }

            // --- Battery ---
            if (type == HardwareType.Battery)
            {
                if (s.SensorType == SensorType.Level)
                {
                    // Skip battery wear / health values (also Level sensors)
                    if (Has(name, "Degradation") || Has(name, "Wear")) return null;
                    
                    // Prefer sensors with "Charge"
                    if (Has(name, "Charge")) return "BAT.Percent";
                    
                    // Others as a fallback
                    return "BAT.Percent";
                }
                if (s.SensorType == SensorType.Power)
                {
                    // Any battery Power sensor, charging or discharging.
                    return "BAT.Power";
                }
                if (s.SensorType == SensorType.Voltage) return "BAT.Voltage";
                if (s.SensorType == SensorType.Current) return "BAT.Current";
            }

            return null;
        }
    }
}
