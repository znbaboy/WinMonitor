using System;
using System.Collections.Generic;
using Windows.UI;

namespace WinMonitor.Core
{
    /// <summary>String interning and theme colour helpers (no drawing code: the UI draws).</summary>
    public static class UIUtils
    {
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new();

        public static string Intern(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            lock (_poolLock)
            {
                if (_stringPool.TryGetValue(str, out var pooled)) return pooled;
                _stringPool[str] = str;
                return str;
            }
        }

        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        /// <summary>Colour for a metric state: value text (ValueSafe/Warn/Crit) or bar (BarLow/Mid/High).</summary>
        public static Color GetStateColor(int state, Theme t, bool isValueText = true)
        {
            if (state == MetricUtils.STATE_CRIT) return ThemeManager.ParseColor(isValueText ? t.Color.ValueCrit : t.Color.BarHigh);
            if (state == MetricUtils.STATE_WARN) return ThemeManager.ParseColor(isValueText ? t.Color.ValueWarn : t.Color.BarMid);
            return ThemeManager.ParseColor(t.Color.ValueSafe);   // LiteMonitor uses ValueSafe for safe bars too
        }
    }
}
