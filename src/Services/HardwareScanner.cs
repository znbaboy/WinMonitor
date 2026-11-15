using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace WinMonitor.Services
{
    /// <summary>
    /// Lists hardware and names it (for the settings page)
    /// </summary>
    public static class HardwareScanner
    {
        private static List<string>? _cachedFanList = null;
        private static List<string>? _cachedNetworkList = null;
        private static List<string>? _cachedDiskList = null;
        private static List<GpuOption>? _cachedGpuOptions = null;
        private static List<string>? _cachedMoboTempList = null;

        public sealed class GpuOption
        {
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
            public string Name { get; set; } = "";
        }

        /// <summary>
        /// Clears the scan caches
        /// </summary>
        public static void ClearCache()
        {
            _cachedFanList = null;
            _cachedNetworkList = null;
            _cachedDiskList = null;
            _cachedGpuOptions = null;
            _cachedMoboTempList = null;
        }

        /// <summary>
        /// Names a sensor together with its hardware (SuperIO chips get the motherboard name)
        /// </summary>
        public static string GenerateSmartName(ISensor sensor, IHardware hardware, IComputer computer)
        {
            string hwName = hardware.Name;
            // SuperIO: use the motherboard name
            if (hardware.HardwareType == HardwareType.SuperIO)
            {
                var mobo = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
                if (mobo != null) hwName = mobo.Name;
            }
            return $"{sensor.Name} [{hwName}]";
        }

        /// <summary>
        /// All network adapters
        /// </summary>
        public static List<string> ListAllNetworks(IComputer computer)
        {
            if (_cachedNetworkList != null && _cachedNetworkList.Count > 0)
                return _cachedNetworkList.ToList();

            var list = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Network)
                .Select(h => h.Name).Distinct().ToList();

            if (list.Count > 0) _cachedNetworkList = list;
            return list.ToList();
        }

        /// <summary>
        /// All disks
        /// </summary>
        public static List<string> ListAllDisks(IComputer computer)
        {
            if (_cachedDiskList != null && _cachedDiskList.Count > 0)
                return _cachedDiskList.ToList();

            var list = computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Storage)
                .Select(h => h.Name).Distinct().ToList();

            if (list.Count > 0) _cachedDiskList = list;
            return list.ToList();
        }

        /// <summary>
        /// All GPU names (older callers).
        /// </summary>
        public static List<string> ListAllGpus(IComputer computer)
        {
            return ListAllGpuOptions(computer).Select(x => x.Label).ToList();
        }

        /// <summary>
        /// All GPU options; Value is the hardware identifier, so identical cards stay separate.
        /// </summary>
        public static List<GpuOption> ListAllGpuOptions(IComputer computer)
        {
            if (_cachedGpuOptions != null && _cachedGpuOptions.Count > 0)
                return CloneGpuOptions(_cachedGpuOptions);

            var gpus = computer.Hardware.Where(IsGpuHardware).ToList();
            var nameCounts = gpus
                .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var nameIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var list = new List<GpuOption>();

            foreach (var gpu in gpus)
            {
                string name = gpu.Name ?? "";
                nameIndexes.TryGetValue(name, out int currentIndex);
                currentIndex++;
                nameIndexes[name] = currentIndex;

                bool duplicatedName = nameCounts.TryGetValue(name, out int count) && count > 1;
                string label = duplicatedName ? $"{name} #{currentIndex}" : name;

                list.Add(new GpuOption
                {
                    Label = label,
                    Value = GetHardwareValue(gpu),
                    Name = name
                });
            }

            if (list.Count > 0) _cachedGpuOptions = CloneGpuOptions(list);
            return CloneGpuOptions(list);
        }

        public static IHardware? FindGpuByConfig(IComputer computer, string configValue)
        {
            if (string.IsNullOrWhiteSpace(configValue)) return null;

            var gpus = computer.Hardware.Where(IsGpuHardware).ToList();
            var byIdentifier = gpus.FirstOrDefault(h =>
                string.Equals(GetHardwareValue(h), configValue, StringComparison.OrdinalIgnoreCase));
            if (byIdentifier != null) return byIdentifier;

            // Older settings stored the GPU name. With several identical cards it cannot pick one, so automatic is used.
            var byName = gpus.Where(h => string.Equals(h.Name, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        public static GpuOption? FindGpuOptionByConfig(IComputer computer, string configValue)
        {
            if (string.IsNullOrWhiteSpace(configValue)) return null;

            var options = ListAllGpuOptions(computer);
            var byValue = options.FirstOrDefault(o =>
                string.Equals(o.Value, configValue, StringComparison.OrdinalIgnoreCase));
            if (byValue != null) return byValue;

            var byName = options.Where(o => string.Equals(o.Name, configValue, StringComparison.OrdinalIgnoreCase)).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        public static bool IsGpuHardware(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia ||
                   hw.HardwareType == HardwareType.GpuAmd ||
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        private static string GetHardwareValue(IHardware hardware)
        {
            string identifier = hardware.Identifier?.ToString() ?? "";
            return string.IsNullOrWhiteSpace(identifier) ? (hardware.Name ?? "") : identifier;
        }

        private static List<GpuOption> CloneGpuOptions(List<GpuOption> source)
        {
            return source
                .Select(x => new GpuOption { Label = x.Label, Value = x.Value, Name = x.Name })
                .ToList();
        }

        /// <summary>
        /// All fan sensors (motherboard / case fans; CPU and GPU fans excluded)
        /// </summary>
        public static List<string> ListAllFans(IComputer computer, object syncLock)
        {
            if (_cachedFanList != null && _cachedFanList.Count > 0)
                return _cachedFanList.ToList();

            var list = new List<string>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd ||
                                      hw.HardwareType == HardwareType.GpuIntel || hw.HardwareType == HardwareType.Cpu ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Fan) 
                                list.Add(GenerateSmartName(s, hw, computer));
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in computer.Hardware) Scan(hw);
            }

            list.Sort();
            var final = list.Distinct().ToList();
            if (final.Count > 0) _cachedFanList = final;
            return final.ToList();
        }

        /// <summary>
        /// All sensors that can stand for the motherboard / system temperature
        /// </summary>
        public static List<string> ListAllMoboTemps(IComputer computer, object syncLock)
        {
            if (_cachedMoboTempList != null && _cachedMoboTempList.Count > 0)
                return _cachedMoboTempList.ToList();

            var list = new List<string>();
            lock (syncLock)
            {
                void Scan(IHardware hw)
                {
                    bool isExcluded = hw.HardwareType == HardwareType.Cpu || hw.HardwareType == HardwareType.GpuNvidia ||
                                      hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuIntel ||
                                      hw.HardwareType == HardwareType.Storage || hw.HardwareType == HardwareType.Memory ||
                                      hw.HardwareType == HardwareType.Network;

                    if (!isExcluded)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature) 
                                list.Add(GenerateSmartName(s, hw, computer));
                        }
                    }
                    foreach (var sub in hw.SubHardware) Scan(sub);
                }
                foreach (var hw in computer.Hardware) Scan(hw);
            }

            list.Sort();
            var final = list.Distinct().ToList();
            if (final.Count > 0) _cachedMoboTempList = final;
            return final.ToList();
        }
    }
}
