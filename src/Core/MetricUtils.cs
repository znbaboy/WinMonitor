using System;
using System.Linq;
using WinMonitor.Services;

namespace WinMonitor.Core
{
    public enum MetricType
    {
        Percent,    // Load, BatPercent
        Temperature,// Temp
        Memory,     // Mem, Vram
        DataSize,   // Data Total
        DataSpeed,  // Net, Disk
        Frequency,  // Clock
        Power,      // Power
        Voltage,    // Voltage
        Current,    // Current
        RPM,        // Fan, Pump
        FPS,        // FPS
        Unknown
    }

    /// <summary>
    /// Metric formatting, units, states and bar values
    /// Type lookup, formatting, thresholds and state colours
    /// </summary>
    public static class MetricUtils
    {
        // =========================================================
        // 1. Power state (cached, refreshed every 3 s)
        // =========================================================
        private static long _lastPowerCheck = 0; // Last power-state check (ms)
        private static bool _lastAc = false; // On AC power
        private static bool _lastCharging = false; // Charging (not just full / battery protection)

        /// <summary>
        /// Current power state.
        /// AcOnline: on AC power
        /// IsCharging: charging (not just full / battery protection)
        /// </summary>
        public static (bool AcOnline, bool IsCharging) GetPowerStatus()
        {
            long now = Environment.TickCount64;
            if (now - _lastPowerCheck > 3000) // refresh at most every 3 s
            {
                _lastPowerCheck = now;
                try
                {
                    if (GetSystemPowerStatus(out var ps))
                    {
                        _lastAc = ps.ACLineStatus == 1;
                        _lastCharging = (ps.BatteryFlag & 8) != 0 && ps.BatteryFlag != 255;   // 8 = charging, 255 = unknown
                    }
                }
                catch { }
            }
            return (_lastAc, _lastCharging);
        }
        
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        // Cache data size units
        private static readonly string[] _dataSizes = { "KB", "MB", "GB", "TB", "PB" };

        public const int STATE_SAFE = 0;
        public const int STATE_WARN = 1;
        public const int STATE_CRIT = 2;

        // =========================================================
        // 2. Types
        // =========================================================
        public static MetricType GetType(string key)
        {
            if (string.IsNullOrEmpty(key)) return MetricType.Unknown;
            
            if (key.Equals("FPS", StringComparison.OrdinalIgnoreCase)) return MetricType.FPS;
            if (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase))
            {
                if (key.IndexOf("Percent", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Percent;
                if (key.IndexOf("Voltage", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Voltage;
                if (key.IndexOf("Current", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Current;
                if (key.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Power;
                return MetricType.Percent;
            }
            if (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("VRAM", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Memory;

            if (key.IndexOf("LOAD", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Percent;
            if (key.IndexOf("TEMP", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Temperature;
            if (key.IndexOf("CLOCK", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Frequency;
            if (key.IndexOf("POWER", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Power;
            if (key.IndexOf("VOLTAGE", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.Voltage;
            if (key.IndexOf("FAN", StringComparison.OrdinalIgnoreCase) >= 0 || 
                key.IndexOf("PUMP", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.RPM;
            if (key.StartsWith("NET", StringComparison.OrdinalIgnoreCase) || 
                key.StartsWith("DISK", StringComparison.OrdinalIgnoreCase)) return MetricType.DataSpeed;
            if (key.IndexOf("DATA", StringComparison.OrdinalIgnoreCase) >= 0) return MetricType.DataSize;
            
            return MetricType.Unknown;
        }

        // =========================================================
        // 3. Formatting
        // =========================================================

        /// <summary>
        /// The value as text (scaled, rounded, compact when asked)
        /// </summary>
        public static string GetValueStr(string key, float? value, bool compact = false, string? userFormat = null)
        {
            float v = value ?? 0.0f;
            var type = GetType(key);

            // 1. Memory: size or percent
            if (type == MetricType.Memory)
            {
                 var cfg = Settings.Load();
                 if (cfg.MemoryDisplayMode == 1 && !IsPercentUnitOverride(userFormat)) // size mode
                 {
                     double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) 
                        ? Settings.DetectedRamTotalGB 
                        : Settings.DetectedGpuVramTotalGB;

                     if (totalGB > 0)
                     {
                         // bytes -> formatted -> number part
                         // adaptive decimals (-1) so 100 GB shows without .0
                         return FormatDataSizeParts((v / 100.0) * totalGB * 1073741824.0, -1).val;
                     }
                 }
            }

            // 2. Data sizes / speeds (automatic unit)
            if (type == MetricType.DataSpeed || type == MetricType.DataSize)
            {
                //return FormatDataSizeParts(v, compact ? -1 : 1).val;
                return FormatDataSizeParts(v,-1 ).val;
            }

            // 4. Panel
            if (type == MetricType.FPS || type == MetricType.RPM) return $"{v:0}";
            if (type == MetricType.Frequency) return $"{v/1000f:F1}"; // clocks in GHz with one decimal
            
            // memory %, load, temperature, power, ...

            return FormatValueAdaptive(v, -1);
        }

        private static bool IsPercentUnitOverride(string? userFormat)
        {
            if (string.IsNullOrWhiteSpace(userFormat)) return false;
            return !userFormat.Contains("{u}", StringComparison.OrdinalIgnoreCase) &&
                   userFormat.Contains("%", StringComparison.Ordinal);
        }

        /// <summary>
        /// Number formatting (adaptive decimals)
        /// </summary>
        private static string FormatValueAdaptive(double val, int decimals)
        {
            if (decimals >= 0)
            {
                // Fixed decimals
                string format = "0." + new string('0', decimals);
                if (decimals == 0) format = "0";
                
                // Even with fixed decimals, values >= 99.95 show as whole numbers
                // unless decimals > 1 (voltage, current, ...)
                if (val >= 99.95 && decimals <= 1) return val.ToString("0");
                
                return val.ToString(format);
            }

            // Adaptive (decimals < 0)
            if (val < 9.995) return val.ToString("0.00");   // 0.00 - 9.99
            if (val < 99.95) return val.ToString("0.0");   // 10.0 - 99.9
            return val.ToString("0");                      // 100+
        }

        public enum UnitContext
        {
            Panel, // Panel (full, with space)
            Taskbar, // Compact (no space)
            SettingsPanel, // Settings preview (with placeholder)
        }

        /// <summary>
        /// The unit text (by context; some units depend on the value)
        /// </summary>
        public static string GetUnitStr(string key, float? value, UnitContext context)
        {
            var type = GetType(key);
            
            // 1. Memory (GB or %)
            if (type == MetricType.Memory) 
            {
                if (Settings.Load().MemoryDisplayMode != 1) return "%";
                if (context == UnitContext.SettingsPanel) return "{u}";

                double totalGB = (key.IndexOf("MEM", StringComparison.OrdinalIgnoreCase) >= 0) ? Settings.DetectedRamTotalGB : Settings.DetectedGpuVramTotalGB;
                
                return (totalGB > 0 && value.HasValue) 
                    ? FormatDataSizeParts((value.Value / 100.0) * totalGB * 1073741824.0, -1).unit 
                    : "GB";
            }
            

            // 2. Data (unit depends on the value)
            if (type == MetricType.DataSpeed || type == MetricType.DataSize)
            {
                // Settings show the {u} placeholder for automatic data units
                if (context == UnitContext.SettingsPanel)
                {
                    if (type == MetricType.DataSpeed)
                        return "{u}/s";
                    return "{u}";
                }

                // Work out the unit from the value (cheap)
                // Compact context -> whole numbers (0)
                // Panel -> adaptive decimals (-1)
                int decimals = (context == UnitContext.Taskbar) ? 0 : -1;
                string u = FormatDataSizeParts(value ?? 0, decimals).unit;
                
                if (type == MetricType.DataSpeed)
                    return context == UnitContext.Taskbar ? u : u + "/s";
                
                return u;
            }
            // Dashboard items: unit from InfoService
            if (key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
            {
                 // Unit injected at runtime, if any
                 string u = InfoService.Instance.GetValue(key + ".Unit");
                 if (!string.IsNullOrEmpty(u)) return u;
            }

            // 3. Battery: charging suffix
            // Lightning only while actually charging (AC and charging),
            // not when plugged in and full
            var pStatus = GetPowerStatus();
            string suffix = (
                key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase) 
                && pStatus.AcOnline 
                && pStatus.IsCharging
                && context != UnitContext.SettingsPanel 
                ) ? "⚡" : "";

            // 4. Standard units
            return type switch
            {
                MetricType.Percent     => "%" + suffix,
                MetricType.Temperature => "°C",
                MetricType.Frequency   => "GHz",
                MetricType.Power       => "W" + suffix,
                MetricType.Voltage     => "V" + suffix,
                MetricType.Current     => "A" + suffix,
                // Compact context uses the short forms
                MetricType.FPS         => context == UnitContext.Taskbar ? "F" : " FPS",
                MetricType.RPM         => context == UnitContext.Taskbar ? "R" : " RPM",
                _ => ""
            };
        }


        public static string GetDisplayUnit(string key, string calculatedUnit, string? userFormat)
        {
           if (string.IsNullOrEmpty(userFormat))
            {
                if (string.IsNullOrEmpty(userFormat) && userFormat != null) return ""; // Empty = Hide
                // GetUnitStr already returns the correct unit (e.g., "MB" or "MB/s") based on context.
                // We don't need to append "/s" here anymore.
                return calculatedUnit;
            }
            return userFormat.Contains("{u}") ? userFormat.Replace("{u}", calculatedUnit) : userFormat;
        }
        // Data sizes
        
        /// <summary>
        /// Bytes to a friendly size: returns the number and the unit separately.
        /// </summary>
        public static (string val, string unit) FormatDataSizeParts(double bytes, int decimals = -1)
        {
            // Use cached array
            // string[] sizes = { "KB", "MB", "GB", "TB", "PB" };

            // Start from KB
            double len = bytes / 1024.0;

            // Step up until below 1000 or at the largest unit
            // 1000 (not 1024) avoids long texts like "1023 KB" (shows "0.99 MB" instead)
            int order = 0;
            while (len >= 1000 && order < _dataSizes.Length - 1)
            {
                order++;
                len /= 1024.0;
            }

            // Number and unit
            return (FormatValueAdaptive(len, decimals), _dataSizes[order]);
        }

        public static string FormatDataSize(double bytes, string suffix = "", int decimals = -1)
        {
            var (val, unit) = FormatDataSizeParts(bytes, decimals);
            return $"{val}{unit}{suffix}";
        }



        // =========================================================
        // 4. Thresholds and states
        // =========================================================
        public static int GetState(string key, double value)
        {
            if (double.IsNaN(value)) return STATE_SAFE;
            if (key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase) && value < 0) return STATE_SAFE;

            var type = GetType(key);
            double checkValue = value;

            // Values without a fixed maximum (clock, power, fan speed)
            // are judged against their recorded maximum, not an absolute value:
            // e.g. a CPU clock at 90% of its maximum counts as high (red).
            // Otherwise 4.0 GHz might never reach a threshold.
            if (type is MetricType.Frequency or MetricType.Power or MetricType.RPM or MetricType.FPS or MetricType.Voltage or MetricType.Current)
            {
                 // Use GetAdaptivePercentage directly since we are already inside the adaptive type check block
                 checkValue = GetAdaptivePercentage(key, value) * 100.0;

                 if (checkValue >= 90) return STATE_CRIT;
                 if (checkValue >= 80) return STATE_WARN;
                 return STATE_SAFE;
            }
            else if (type is MetricType.DataSpeed or MetricType.DataSize)
            {
                checkValue = value / 1048576.0; // To MB
            }

            var (warn, crit) = GetThresholds(key);

            // Battery: low is bad (reversed)
            if (key.Equals("BAT.Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (checkValue <= crit) return STATE_CRIT;
                if (checkValue <= warn) return STATE_WARN;
                return STATE_SAFE;
            }

            if (checkValue >= crit) return STATE_CRIT;
            if (checkValue >= warn) return STATE_WARN;

            return STATE_SAFE;
        }

        public static (double warn, double crit) GetThresholds(string key)
        {
            var cfg = Settings.Load();
            var th = cfg.Thresholds;
            var type = GetType(key);

            return type switch
            {
                MetricType.Temperature => (th.Temp.Warn, th.Temp.Crit),
                MetricType.DataSpeed => key.StartsWith("NET") 
                    ? (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 ? (th.NetUpMB.Warn, th.NetUpMB.Crit) : (th.NetDownMB.Warn, th.NetDownMB.Crit))
                    : (th.DiskIOMB.Warn, th.DiskIOMB.Crit),
                MetricType.DataSize => (key.IndexOf("UP", StringComparison.OrdinalIgnoreCase) >= 0 ? (th.DataUpMB.Warn, th.DataUpMB.Crit) : (th.DataDownMB.Warn, th.DataDownMB.Crit)),
                MetricType.Percent => key.Equals("BAT.Percent", StringComparison.OrdinalIgnoreCase) ? (60, 20) : (th.Load.Warn, th.Load.Crit),
                _ => (th.Load.Warn, th.Load.Crit)
            };
        }

        /// <summary>
        /// The bar's fill (0.0 - 1.0)
        /// Uses the recorded maximum where needed, and shows at least 5%
        /// </summary>
        public static double GetProgressValue(string key, double value)
        {
            // 1. Base percentage
            // Inlined GetUnifiedPercent logic
            var type = GetType(key);
            double rawPct;
            
            if (type is MetricType.Frequency or MetricType.Power or MetricType.RPM or MetricType.FPS or MetricType.Voltage or MetricType.Current)
                rawPct = GetAdaptivePercentage(key, value);
            else
                rawPct = value / 100.0;

            // 2. Negative (charging): full bar, colour from the state
            if (rawPct < 0) rawPct = Math.Abs(rawPct);

            // 3. Keep within [0.05, 1.0]
            // so a small value still shows a thin bar
            return Math.Max(0.05, Math.Min(1.0, rawPct));
        }

        public static double GetAdaptivePercentage(string key, double val)
        {
            var cfg = Settings.Load();
            float max = 1.0f;

            if (key == "CPU.Clock") max = cfg.RecordedMaxCpuClock;
            else if (key == "CPU.Power") max = cfg.RecordedMaxCpuPower;
            else if (key == "GPU.Clock") max = cfg.RecordedMaxGpuClock;
            else if (key == "GPU.Power") max = cfg.RecordedMaxGpuPower;
            else if (key == "CPU.Fan") max = cfg.RecordedMaxCpuFan;
            else if (key == "CPU.Pump") max = cfg.RecordedMaxCpuPump;
            else if (key == "CASE.Fan") max = cfg.RecordedMaxChassisFan;
            else if (key == "GPU.Fan") max = cfg.RecordedMaxGpuFan;
            else if (key == "CPU.Voltage") max = 1.6f;
            else if (key == "BAT.Power") max = 100f;
            else if (key == "BAT.Voltage") max = 20f;
            else if (key == "BAT.Current") max = 5f;

            if (max < 1) max = 1;
            double pct = val / max;
            return pct > 1.0 ? 1.0 : pct;
        }
        
        // =========================================================
        // 5. Conversions
        // =========================================================
        public static int ParseInt(string s) => int.TryParse(new string(s?.Where(c => char.IsDigit(c) || c == '-').ToArray()), out int v) ? v : 0;
        public static double ParseDouble(string s) => double.TryParse(new string(s?.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray()), out double v) ? v : 0;
        public static string ToStr(double v, string format = "F1") => v.ToString(format);
    }
}
