using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using WinMonitor.Core;
namespace WinMonitor
{
    public class Settings
    {
        // ====== 基础设置 ======
        public string Skin { get; set; } = "DarkFlat_Classic";
        public bool TopMost { get; set; } = true;
        public bool AutoStart { get; set; } = false;
        public int RefreshMs { get; set; } = 1000;
        public double AnimationSpeed { get; set; } = 0.35;
        public ScreenPoint Position { get; set; } = new ScreenPoint(-1, -1);

        // ====== 界面与行为 ======
        public bool HorizontalMode { get; set; } = false;
        public double Opacity { get; set; } = 0.85;
        public bool ClickThrough { get; set; } = false;
        public bool AutoHide { get; set; } = true;
        public bool ClampToScreen { get; set; } = true;
        public int PanelWidth { get; set; } = 240;
        public double UIScale { get; set; } = 1.0;

        // ====== 硬件相关 ======
        public string PreferredNetwork { get; set; } = "";
        public string LastAutoNetwork { get; set; } = "";
        public string PreferredDisk { get; set; } = "";
        public string LastAutoDisk { get; set; } = "";
        public string PreferredGpu { get; set; } = "";
        
        // ★★★ [新增] 首选风扇 ★★★
        public string PreferredCpuFan { get; set; } = "";
        public string PreferredCpuPump { get; set; } = ""; // 保存用户选的水冷接口
        public string PreferredCaseFan { get; set; } = "";
        public string PreferredMoboTemp { get; set; } = "";
        
        // 主窗体所在的屏幕设备名 (用于记忆上次位置)
        public string ScreenDevice { get; set; } = "";

        // [Cleanup] taskbar mode removed
        public bool HideMainForm { get; set; } = false;
        public bool HideTrayIcon { get; set; } = false;

        // ★★★ [新增] 横条/桌面模式 间距配置 ★★★
        public int HorizontalItemSpacing { get; set; } = 12;  // 组间距 (默认 12)
        public int HorizontalInnerSpacing { get; set; } = 8;  // 标签与值间距 (默认 8)
        public bool HorizontalSingleLine { get; set; } = false; // [新增] 横条模式单行显示

        // 双击动作配置
        public int MainFormDoubleClickAction { get; set; } = 0;

        // 内存/显存显示模式
        public int MemoryDisplayMode { get; set; } = 1;

        // ★ 2. 运行时缓存：存储探测到的总容量 (GB)
        [JsonIgnore] public static float DetectedRamTotalGB { get; set; } = 0;
        [JsonIgnore] public static float DetectedGpuVramTotalGB { get; set; } = 0;

        // 开启后：CPU使用率、CPU频率、内存占用、磁盘读写 将优先从 Windows 计数器读取
        public bool UseWinPerCounters { get; set; } = true;

        // ★★★ [新增] 忽略 SMB (局域网文件传输) 流量 ★★★
        public bool IgnoreSmbTraffic { get; set; } = true;
        
        // ====== 记录与报警 ======
        public float RecordedMaxCpuPower { get; set; } = 65.0f;
        public float RecordedMaxCpuClock { get; set; } = 4200.0f;
        public float RecordedMaxGpuPower { get; set; } = 100.0f;
        public float RecordedMaxGpuClock { get; set; } = 1800.0f;
        
        // ★★★ [新增] FPS 固定最大值 (用于进度条上限，推荐 144) ★★★
        public float RecordedMaxFps { get; set; } = 144.0f;

        // ★★★ [新增] 风扇最大值记录 ★★★
        public float RecordedMaxCpuFan { get; set; } = 4000;
        public float RecordedMaxCpuPump { get; set; } = 5000; // 水冷最大转速 (用于百分比计算)
        public float RecordedMaxGpuFan { get; set; } = 3500;
        public float RecordedMaxChassisFan { get; set; } = 3000;

        public bool MaxLimitTipShown { get; set; } = false;
        
        public bool AlertTempEnabled { get; set; } = true;
        public int AlertTempThreshold { get; set; } = 80;

        
        public ThresholdsSet Thresholds { get; set; } = new ThresholdsSet();

        [JsonIgnore] public DateTime LastAlertTime { get; set; } = DateTime.MinValue;
        [JsonIgnore] public long SessionUploadBytes { get; set; } = 0;
        [JsonIgnore] public long SessionDownloadBytes { get; set; } = 0;
        [JsonIgnore] public DateTime LastAutoSaveTime { get; set; } = DateTime.MinValue;

        public Dictionary<string, string> GroupAliases { get; set; } = new Dictionary<string, string>();

        // show the "WinMonitor" title at the top of the panel
        public bool ShowTitle { get; set; } = true;

        // [Halo] seconds between wireless device battery checks
        public int HaloPollSeconds { get; set; } = 20;
        public bool HaloPsBluetoothFullMode { get; set; } = false;   // Halo's "PlayStation over Bluetooth: full mode"

        // [Halo] low battery alert for wireless devices (like the temperature alert)
        public bool AlertBatteryEnabled { get; set; } = true;
        public int AlertBatteryThreshold { get; set; } = 20;
        public List<MonitorItemConfig> MonitorItems { get; set; } = new List<MonitorItemConfig>();

        // ★★★ 核心修复：添加全局保存锁，防止重置时被自动保存覆盖 ★★★
        [JsonIgnore]
        public static bool GlobalBlockSave 
        { 
            get => SettingsHelper.GlobalBlockSave; 
            set => SettingsHelper.GlobalBlockSave = value; 
        }

        // ★★★ 优化 2：全局单例引用 ★★★
        private static Settings _instance;

        // ★★★ 优化 3：改造 Load 方法为单例模式 ★★★
        public static Settings Load(bool forceReload = false)
        {
            // 如果单例已存在且不强制刷新，直接返回内存对象 (0 IO, 0 GC)
            if (_instance != null && !forceReload) return _instance;

            // 委托给 SettingsHelper 加载
            _instance = SettingsHelper.Load(forceReload);

            return _instance;
        }

        public Settings DeepClone()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(this);
            return System.Text.Json.JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
        }
    }

    public class MonitorItemConfig
    {
        // ★★★ 核心优化：使用字符串驻留池解决内存浪费 ★★★
        private string _key = "";

        // 极简缓存：复用首个实例作为原型 (0 GC)
        private static readonly ConcurrentDictionary<string, MonitorItemConfig> _protoCache = new ConcurrentDictionary<string, MonitorItemConfig>();

        // [Optimization] Cache keys to avoid string concatenation in hot paths
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

                // 极简模式：直接从缓存的原型对象拷贝所有属性，无需额外对象分配
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

                // 首次计算
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
                
                // 存入缓存
                _protoCache[k] = this;
            }
        }
        // ★★★ [优化] 分离用户配置与系统动态值 ★★★
        // UserLabel: 用户手动设置的名称 (持久化)。为空表示跟随系统。
        public string UserLabel { get; set; } = ""; 
        // Short label in the horizontal mode (" " = no label). The JSON name is kept from the
        // removed taskbar mode, so existing settings keep their short labels.
        public string TaskbarLabel { get; set; } = "";

        // DynamicLabel: 插件运行时计算的名称 (不持久化)。
        [JsonIgnore]
        public string DynamicLabel { get; set; } = "";

        // DisplayLabel: 最终显示名称 (优先显示用户设置，否则显示动态值)
        [JsonIgnore]
        public string DisplayLabel => !string.IsNullOrEmpty(UserLabel) ? UserLabel : DynamicLabel;

        // ★★★ [新增] 自定义单位配置 ★★★
        // null/"Auto" = 自动(默认), "" = 不显示, "{u}/s" = 自定义格式
        public string UnitPanel { get; set; } = null; 
        public bool VisibleInPanel { get; set; } = true;

        // [Bars] draw the bar under this item (only matters when HasBar)
        public bool ShowBar { get; set; } = true;

        /// <summary>[Bars] Whether the panel draws a bar for this item: the Dashboard, network,
        /// disk and traffic rows are text only.</summary>
        [JsonIgnore]
        public bool HasBar => UIGroup is not ("DASH" or "NET" or "DISK" or "DATA");

        public int SortIndex { get; set; } = 0;

        // ★★★ 新增：统一的分组属性 ★★★
        // 所有界面（主界面、设置页、菜单）都统一调用这个属性来决定它属于哪个组
        // 从而避免了在 UI 代码里到处写 if else
        [JsonIgnore]
        public string UIGroup 
        {
            get 
            {
                // [Optimization] Return cached value if available
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
