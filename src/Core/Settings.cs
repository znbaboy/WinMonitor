using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using WinMonitor.Core;
namespace WinMonitor
{
    public class Settings
    {
        // ====== Basics ======
        public string Skin { get; set; } = "System";   // System (follow Windows), Light or Dark
        public bool TopMost { get; set; } = false;
        public bool AutoStart { get; set; } = false;
        public int RefreshMs { get; set; } = 1000;
        public double AnimationSpeed { get; set; } = 0.35;
        public ScreenPoint Position { get; set; } = new ScreenPoint(-1, -1);

        // ====== Panel and behaviour ======
        public double Opacity { get; set; } = 0.85;
        public bool ClickThrough { get; set; } = false;
        public bool ClampToScreen { get; set; } = true;
        public int PanelWidth { get; set; } = 240;
        public double UIScale { get; set; } = 1.0;

        // ====== Hardware ======
        public string PreferredNetwork { get; set; } = "";
        public string LastAutoNetwork { get; set; } = "";
        public string PreferredDisk { get; set; } = "";
        public string LastAutoDisk { get; set; } = "";
        public string PreferredGpu { get; set; } = "";
        
        // Preferred fan sensors
        public string PreferredCpuFan { get; set; } = "";
        public string PreferredCpuPump { get; set; } = ""; // the user's pump sensor
        public string PreferredCaseFan { get; set; } = "";
        public string PreferredMoboTemp { get; set; } = "";
        
        // Monitor the panel was on (to restore its position)
        public string ScreenDevice { get; set; } = "";

        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;


        // Double-click action
        public int MainFormDoubleClickAction { get; set; } = 2;   // 1 Task Manager, 2 Settings, 5 nothing

        // Memory / video memory display mode
        public int MemoryDisplayMode { get; set; } = 1;

        // Runtime cache: detected total size (GB)
        [JsonIgnore] public static float DetectedRamTotalGB { get; set; } = 0;
        [JsonIgnore] public static float DetectedGpuVramTotalGB { get; set; } = 0;

        // On: CPU usage, CPU clock, memory and disk speed come from Windows performance counters first
        public bool UseWinPerCounters { get; set; } = true;

        // Leave out SMB (local network file sharing) traffic
        public bool IgnoreSmbTraffic { get; set; } = true;
        
        // ====== Records and alerts ======
        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        

        // Recorded fan maximums
        public float RecordedMaxCpuFan { get; set; } = 4000;
        public float RecordedMaxCpuPump { get; set; } = 5000; // pump maximum (for the percentage)
        public float RecordedMaxGpuFan { get; set; } = 3500;
        public float RecordedMaxChassisFan { get; set; } = 3000;

        public bool MaxLimitTipShown { get; set; } = false;
        
        public bool AlertTempEnabled { get; set; } = true;
        public int AlertTempThreshold { get; set; } = 80;
        public int AlertDiskTempThreshold { get; set; } = 70;   // disks have their own limit

        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] public DateTime LastAutoSaveTime { get; set; } = DateTime.MinValue;

        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();

        /// <summary>Categories switched off: their rows are hidden (and not read) but keep their own choices.</summary>
        public List<string> DisabledGroups { get; set; } = new List<string>();

        public bool IsGroupEnabled(string group) => !DisabledGroups.Contains(group);

        /// <summary>Is this row on the panel? Ticked, and its category is on.</summary>
        public bool IsShown(MonitorItemConfig item) => item.VisibleInPanel && IsGroupEnabled(item.UIGroup);

        // show the "WinMonitor" title at the top of the panel
        public bool ShowTitle { get; set; } = true;

        // seconds between wireless device battery checks
        public int HaloPollSeconds { get; set; } = 20;
        public bool HaloPsBluetoothFullMode { get; set; } = false;   // Halo's "PlayStation over Bluetooth: full mode"

        // low battery alert for wireless devices (like the temperature alert)
        public bool AlertBatteryEnabled { get; set; } = true;
        public int AlertBatteryThreshold { get; set; } = 20;
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // Blocks every save (used while resetting, so nothing writes the old settings back)
        [JsonIgnore]
        public static bool GlobalBlockSave 
        { 
            get => SettingsHelper.GlobalBlockSave; 
            set => SettingsHelper.GlobalBlockSave = value; 
        }

        // The single shared instance
        private static Settings _instance;

        // Loads once, then returns the same instance
        public static Settings Load(bool forceReload = false)
        {
            // Already loaded: no file access
            if (_instance != null && !forceReload) return _instance;

            // Load through SettingsHelper
            _instance = SettingsHelper.Load(forceReload);

            return _instance;
        }

    }

    public class MonitorItemConfig
    {
        // Shared string pool, so keys are not duplicated in memory
        private string _key = "";

        // Caches the first instance as a template
        private static readonly ConcurrentDictionary<string, MonitorItemConfig> _protoCache = new ConcurrentDictionary<string, MonitorItemConfig>();

        // Cache keys to avoid string concatenation in hot paths
        [JsonIgnore] public string CachedPropLabelKey { get; private set; } = "";
        [JsonIgnore] public string CachedPropShortLabelKey { get; private set; } = "";
        [JsonIgnore] public string CachedUIGroup { get; private set; } = "";
        [JsonIgnore] public string CachedItemsKey { get; private set; } = ""; // Items.Key
        [JsonIgnore] public string CachedGroupsKey { get; private set; } = ""; // Groups.UIGroup
        [JsonIgnore] public string CachedDashColorKey { get; private set; } = "";
        [JsonIgnore] public string CachedDashUnitKey { get; private set; } = "";

        public string Key 
        { 
            get => _key; 
            set 
            {
                string k = UIUtils.Intern(value ?? "");
                if (_key == k) return;
                _key = k;

                // Copy everything from the cached template (no extra allocations)
                if (_protoCache.TryGetValue(k, out var p))
                {
                    CachedPropLabelKey = p.CachedPropLabelKey;
                    CachedPropShortLabelKey = p.CachedPropShortLabelKey;
                    CachedItemsKey = p.CachedItemsKey;
                    CachedUIGroup = p.CachedUIGroup;
                    CachedGroupsKey = p.CachedGroupsKey;
                    CachedDashColorKey = p.CachedDashColorKey;
                    CachedDashUnitKey = p.CachedDashUnitKey;
                    return;
                }

                // First time: compute
                CachedPropLabelKey = UIUtils.Intern("PROP.Label." + _key);
                CachedPropShortLabelKey = UIUtils.Intern("PROP.ShortLabel." + _key);
                CachedItemsKey = UIUtils.Intern("Items." + _key);

                if (_key == "MEM.Load" || _key == "MOBO.Temp" || _key == "DISK.Temp" || _key == "CASE.Fan"|| _key == "FPS") 
                    CachedUIGroup = "HOST"; 
                else if (_key.StartsWith("DASH."))
                {
                    CachedUIGroup = "DASH";
                    string sub = _key.Substring(5);
                    CachedDashColorKey = UIUtils.Intern(sub + ".Color");
                    CachedDashUnitKey = UIUtils.Intern(sub + ".Unit");
                }
                else
                    CachedUIGroup = UIUtils.Intern(_key.Split('.')[0]); 
                
                CachedGroupsKey = UIUtils.Intern("Groups." + CachedUIGroup);
                
                // Cache it
                _protoCache[k] = this;
            }
        }
        // User settings vs live values:
        // UserLabel: name set by the user (saved). Empty = automatic.
        public string UserLabel { get; set; } = ""; 
        // Short label in the horizontal mode (" " = no label). The JSON name is kept from the
        // removed taskbar mode, so existing settings keep their short labels.
        public string TaskbarLabel { get; set; } = "";

        // DynamicLabel: name worked out at runtime (not saved).
        [JsonIgnore]
        public string DynamicLabel { get; set; } = "";

        // DisplayLabel: the user's name if set, otherwise the live one
        [JsonIgnore]
        public string DisplayLabel => !string.IsNullOrEmpty(UserLabel) ? UserLabel : DynamicLabel;

        // Custom unit
        // null/"Auto" = automatic (default), "" = none, "{u}/s" = custom format
        public string UnitPanel { get; set; } = null; 
        public bool VisibleInPanel { get; set; } = true;

        // draw the bar under this item (only matters when HasBar)
        public bool ShowBar { get; set; } = true;

        /// <summary>[Bars] Whether the panel draws a bar for this item: the Dashboard, network,
        /// disk and traffic rows are text only.</summary>
        [JsonIgnore]
        public bool HasBar => UIGroup is not ("DASH" or "NET" or "DISK" or "DATA");

        public int SortIndex { get; set; } = 0;

        // The group an item belongs to
        // Used everywhere (panel, settings, menus), so the rule lives in one place
        [JsonIgnore]
        public string UIGroup 
        {
            get 
            {
                // Return cached value if available
                if (!string.IsNullOrEmpty(CachedUIGroup)) return CachedUIGroup;

                // Fallback for uninitialized state (should be rare)
                if (Key == "MEM.Load" || 
                    Key == "MOBO.Temp" || 
                    Key == "DISK.Temp" || 
                    Key == "CASE.Fan"|| 
                    Key == "FPS") 
                { 
                    return "HOST"; 
                } 
                
                if (Key.StartsWith("DASH."))
                {
                    return "DASH";
                }

                return Key.Split('.')[0]; 
            }
        }
    }

    public class ThresholdsSet
    {
        public ValueRange Load { get; set; } = new ValueRange { Warn = 60, Crit = 85 };
        public ValueRange Temp { get; set; } = new ValueRange { Warn = 50, Crit = 70 };
        public ValueRange DiskIOMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange NetUpMB { get; set; } = new ValueRange { Warn = 1, Crit = 2 };
        public ValueRange NetDownMB { get; set; } = new ValueRange { Warn = 2, Crit = 8 };
        public ValueRange DataUpMB { get; set; } = new ValueRange { Warn = 512, Crit = 1024 };
        public ValueRange DataDownMB { get; set; } = new ValueRange { Warn = 2048, Crit = 5096 };
    }

    /// <summary>Panel position in screen pixels (same JSON shape as LiteMonitor's settings: {"X":..,"Y":..}).</summary>
    public struct ScreenPoint
    {
        public int X { get; set; }
        public int Y { get; set; }
        public ScreenPoint(int x, int y) { X = x; Y = y; }
    }

    public class ValueRange
    {
        public double Warn { get; set; } = 0;
        public double Crit { get; set; } = 0;
    }
}
