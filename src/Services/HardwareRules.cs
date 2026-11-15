using System;
using LibreHardwareMonitor.Hardware;

namespace WinMonitor.Services
{
    /// <summary>
    /// Hardware rules
    /// GPU priority, GPU type and which video memory to read
    /// </summary>
    public static class HardwareRules
    {
        // Fast string matching
        public static bool Has(string source, string sub)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(sub)) return false;
            return source.AsSpan().Contains(sub.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Hardware priority (lower = stronger)
        /// 0: top dedicated GPU / 1: top integrated / 2: mid-range / 3: low-end / 4: other / 100: unusable
        /// </summary>
        public static int GetHwPriority(IHardware hw)
        {
            string name = hw.Name;
            
            // 1. Microsoft Basic Render Driver always last
            if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) return 100;

            // 2. NVIDIA (treated as dedicated / strongest)
            if (hw.HardwareType == HardwareType.GpuNvidia) return 0;

            // 3. AMD
            if (hw.HardwareType == HardwareType.GpuAmd)
            {
                // The generic "AMD Radeon(TM) Graphics" is usually integrated -> 2
                if (name.Equals("AMD Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)) return 2;
                // Named models (e.g. RX 7900 XTX) are dedicated -> 0
                return 0;
            }

            // 4. Intel
            if (hw.HardwareType == HardwareType.GpuIntel)
            {
                // Arc
                if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                {
                    // Arc dedicated (A/B/Pro) -> 0
                    if (IsDiscreteArc(name)) return 0;
                    // Arc integrated (140V) -> 1
                    return 1;
                }

                // Iris -> 2
                if (name.Contains("Iris", StringComparison.OrdinalIgnoreCase)) return 2;

                // UHD -> 3
                if (name.Contains("UHD", StringComparison.OrdinalIgnoreCase)) return 3;
            }

            // Other -> 4
            return 4;
        }

        /// <summary>
        /// Dedicated Arc (A/B/Pro series)?
        /// </summary>
        public static bool IsDiscreteArc(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return false;
            
            // Dedicated cards carry a series (A770, B580, Pro A40);
            // integrated ones are "Intel Arc Graphics" or "Intel Arc 140V"
            // Simple check for " A", " B", " Pro" (with the space)
            return Has(name, " A") || Has(name, " B") || Has(name, " Pro");
        }

        /// <summary>
        /// Use shared memory?
        /// </summary>
        public static bool ShouldUseSharedMemory(IHardware hw)
        {
            // Only Intel integrated GPUs use shared memory;
            // dedicated Arc, NVIDIA and AMD use dedicated memory
            if (hw.HardwareType == HardwareType.GpuIntel)
            {
                // Dedicated Arc: dedicated memory
                if (IsDiscreteArc(hw.Name)) return false;
                
                // Integrated (UHD / Iris / Arc): shared memory
                return true;
            }
            
            return false;
        }
    }
}
