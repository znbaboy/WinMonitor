using System;
using Windows.UI;
using System.Linq;
using WinMonitor.Core;
using WinMonitor.Services; // InfoService (dashboard values and colours)

namespace WinMonitor
{
    public enum MetricRenderStyle
    {
        StandardBar, 
        TwoColumn,   
        TextOnly     
    }

    public class MetricItem
    {
        // The item's settings entry (for its live label and unit)
        public MonitorItemConfig BoundConfig { get; set; }

        private string _key = "";
        
        // Cached InfoService lookup key
        private string? _dashColorKey;
        private const string PluginPrefix = "DASH.";

        public string Key 
        { 
            get => _key;
            set 
            {
                _key = UIUtils.Intern(value);
                // Pre-compute the lookup key
                if (_key.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    // Strip the "DASH." prefix (5 chars) and append ".Color"
                    // Span avoids an intermediate substring
                    _dashColorKey = UIUtils.Intern(string.Concat(_key.AsSpan(PluginPrefix.Length), ".Color".AsSpan()));
                }
                else
                {
                    _dashColorKey = null;
                }
            } 
        }

        private string _label = "";
        public string Label 
        {
            get 
            {
                // Shared label rules (MetricLabelResolver)
                string labelResolved = MetricLabelResolver.ResolveLabel(BoundConfig);
                if (!string.IsNullOrEmpty(labelResolved)) return labelResolved;

                return _label;
            }
            set => _label = UIUtils.Intern(value);
        }
        
        private string _shortLabel = "";
        public string ShortLabel 
        {
            get 
            {
                // Shared label rules (MetricLabelResolver)
                string shortResolved = MetricLabelResolver.ResolveShortLabel(BoundConfig);
                if (!string.IsNullOrEmpty(shortResolved)) return shortResolved;

                return _shortLabel;
            }
            set => _shortLabel = UIUtils.Intern(value);
        }
        
        public float? Value { get; set; } = null;
        public float DisplayValue { get; set; } = 0f;
        public string TextValue { get; set; } = null;

        // =============================
        // Text caches
        // =============================
        private float _cachedDisplayValue = -99999f; 
        private (bool Ac, bool Charging) _cachedPowerState = (false, false); // Last power state (battery items refresh when it changes)
        private string _cachedNormalText = ""; // Full text (value + unit)
        private string _cachedHorizontalText = ""; // Compact text (value + short unit)
        
        public string CachedValueText { get; private set; } = "";
        public string CachedUnitText { get; private set; } = "";
        public bool HasCustomUnit { get; private set; } = false; // True when the user set a custom unit


        public int CachedColorState { get; private set; } = 0;
        public double CachedPercent { get; private set; } = 0.0;

        // Item fed directly by code (no config, no InfoService): label, text, colour and bar
        public bool IsDirect { get; private set; } = false;

        public void SetDirect(string label, string text, int colorState, double percent)
        {
            IsDirect = true;
            Label = label;
            TextValue = text;
            CachedColorState = colorState;
            CachedPercent = Math.Clamp(percent, 0.0, 1.0);
        }

        public Color GetTextColor(Theme t)
        {
            return UIUtils.GetStateColor(CachedColorState, t, true);
        }

        public string GetFormattedText(bool isHorizontal)
        {
            if (IsDirect) return TextValue ?? ""; // 

            // 1. Dashboard items: take their colour state first
            if (_dashColorKey != null) // Use cached check
            {
                string colorVal = InfoService.Instance.GetValue(_dashColorKey);
                
                if (!string.IsNullOrEmpty(colorVal))
                {
                    if (int.TryParse(colorVal, out int state)) 
                    {
                        CachedColorState = state;
                    }
                }
                else
                {
                    CachedColorState = 0; // Default Safe if no color override
                }
            }

            // 2. Load Config (Optimized)
            var cfg = BoundConfig;
            if (cfg == null)
            {
                 // Fallback: This should rarely happen if initialized correctly
                 cfg = Settings.Load().MonitorItems.FirstOrDefault(x => x.Key == Key);
            }
            
            string? userFormat = null;   // units are always the reported ones
            HasCustomUnit = !string.IsNullOrEmpty(userFormat);

            // 3. Dashboard items: return their text
           if (TextValue != null) 
            {
                // Units come from MetricUtils:
                // 1. Default unit for this key
                var ctx = isHorizontal ? MetricUtils.UnitContext.Taskbar : MetricUtils.UnitContext.Panel;
                string defUnit = MetricUtils.GetUnitStr(Key, null, ctx);

                // 2. The user's unit, or the default when none is set
                string finalUnit = MetricUtils.GetDisplayUnit(Key, defUnit, userFormat);

                // 3. Append the unit unless the text already ends with it
                if (!string.IsNullOrEmpty(finalUnit) && !TextValue.EndsWith(finalUnit))
                {
                    return TextValue + finalUnit;
                }

                return TextValue;
            }

            // 4. Numeric Value Processing (Hardware items)
            // Refresh when the value changed, or when a battery item's power state changed
            bool isBat = Key.StartsWith("BAT", StringComparison.OrdinalIgnoreCase);
            var currentPower = MetricUtils.GetPowerStatus();
            bool powerChanged = isBat && (_cachedPowerState != currentPower);

            // Refresh threshold 0.005: values show with 2 decimals, so anything larger
            // could leave a stale value on screen (e.g. 0.04 -> 0.00 would not refresh at 0.05).
            if (Math.Abs(DisplayValue - _cachedDisplayValue) > 0.005f || powerChanged)
            {
                _cachedDisplayValue = DisplayValue;
                if (isBat) _cachedPowerState = currentPower;

                // Build the normal and the compact text
                
                // 1. Panel text
                string? userFmtPanel = null;
                string valNormal = MetricUtils.GetValueStr(Key, DisplayValue, false, userFmtPanel);
                string unitNormal = MetricUtils.GetUnitStr(Key, DisplayValue, MetricUtils.UnitContext.Panel);
                
                string finalUnitPanel = MetricUtils.GetDisplayUnit(Key, unitNormal, userFmtPanel);
                _cachedNormalText = valNormal + finalUnitPanel;

                // 2. Compact text (short units)
                // Compact text uses the compact unit context (e.g. no /s)
                string? userFmtTaskbar = null;
                bool hasCustomTaskbar = !string.IsNullOrEmpty(userFmtTaskbar);
                
                // Automatic unit: compact number and short unit
                // Custom unit: full number, unit based on the compact context
                bool compact = !hasCustomTaskbar;
                
                string valTaskbar = MetricUtils.GetValueStr(Key, DisplayValue, compact, userFmtTaskbar);
                string unitTaskbar = MetricUtils.GetUnitStr(Key, DisplayValue, MetricUtils.UnitContext.Taskbar);
                
                string finalUnitTaskbar = MetricUtils.GetDisplayUnit(Key, unitTaskbar, userFmtTaskbar);
                _cachedHorizontalText = valTaskbar + finalUnitTaskbar;

                // Keep the public text in sync (useful when debugging)
                if (isHorizontal)
                {
                    CachedValueText = valTaskbar;
                    CachedUnitText = finalUnitTaskbar;
                    HasCustomUnit = hasCustomTaskbar;
                }
                else
                {
                    CachedValueText = valNormal;
                    CachedUnitText = finalUnitPanel;
                    HasCustomUnit = !string.IsNullOrEmpty(userFmtPanel);
                }

                // Colour state for hardware items (dashboard items were handled above)
                if (!Key.StartsWith("DASH."))
                {
                    CachedColorState = MetricUtils.GetState(Key, DisplayValue);
                }
                
                CachedPercent = MetricUtils.GetProgressValue(Key, DisplayValue);
            }
            return isHorizontal ? _cachedHorizontalText : _cachedNormalText;
        }

        public MetricRenderStyle Style { get; set; } = MetricRenderStyle.StandardBar;

        public void TickSmooth(double speed)
        {
            if (!Value.HasValue) return;
            float target = Value.Value;
            float diff = Math.Abs(target - DisplayValue);
            
            // Snap tiny differences straight to the target instead of skipping them
            if (diff < 0.05f) 
            {
                DisplayValue = target;
                return;
            }

            // Big jumps, or fast animation: update straight away
            if (diff > 15f || speed >= 0.9) DisplayValue = target;
            else DisplayValue += (float)((target - DisplayValue) * speed);
        }
    }
}
