using System;
using System.Collections.Generic;
using Windows.UI;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinMonitor.Core
{
    /// <summary>
    /// 布局配置：仅保留当前代码实际用到的字段
    /// - width:            窗体宽度（最终由 Settings.PanelWidth 覆盖）
    /// - rowHeight:        行高（各监控项基准高度）
    /// - padding:          画布外边距
    /// - cornerRadius:     窗体圆角（MainForm 应用）
    /// - groupRadius:      组块圆角（UIRenderer 应用）
    /// - groupPadding:     组块内边距
    /// - groupSpacing:     组块之间的垂直间距
    /// - groupBottom:      组块额外底部留白
    /// - itemGap:          监控项之间的垂直间距
    /// - groupTitleOffset: 组标题与块体的垂直微调
    /// </summary>
    public class LayoutConfig
    {
        public int Width { get; set; } = 240;//不会被实际使用，运行时由 Settings.PanelWidth 覆盖
        public float LayoutScale { get; set; } = 1.0f;//不会被实际使用，运行时由 Settings.PanelWidth 覆盖
        public int RowHeight { get; set; } = 40;
        public int Padding { get; set; } = 12;

        public int CornerRadius { get; set; } = 12;
        public int GroupRadius { get; set; } = 10;

        public int GroupPadding { get; set; } = 8;
        public int GroupSpacing { get; set; } = 30;
        public int GroupBottom { get; set; } = 0;

        public int ItemGap { get; set; } = 6;
        public int GroupTitleOffset { get; set; } = 6;

        

        public void Scale(float s)
        {
            if (s <= 0f || Math.Abs(s - 1f) < 0.01f)
                return;

            Width = (int)(Width * s);
            RowHeight = (int)(RowHeight * s);
            Padding = (int)(Padding * s);
            CornerRadius = (int)(CornerRadius * s);
            GroupRadius = (int)(GroupRadius * s);
            GroupPadding = (int)(GroupPadding * s);
            GroupSpacing = (int)(GroupSpacing * s);
            GroupBottom = (int)(GroupBottom * s);
            GroupTitleOffset = (int)(GroupTitleOffset * s);
            ItemGap = (int)(ItemGap * s);
        }


    }

    /// <summary>
    /// 字体配置：
    /// - family       : 文本主字体
    /// - valueFamily  : 数值字段单独字体（等宽可读性更好）
    /// - title/group/item/value: 四类字号
    /// - bold         : 是否加粗（四类统一按该值生效）
    /// - scale        : DPI/喜好缩放系数（0.5~3.0）
    /// </summary>
    public class FontConfig
    {
        public string Family { get; set; } = "Microsoft YaHei UI";
        public string ValueFamily { get; set; } = "Consolas";

        public double Title { get; set; } = 11.5;
        public double Group { get; set; } = 10.5;
        public double Item { get; set; } = 10.0;
        public double Value { get; set; } = 10.5;

        public bool Bold { get; set; } = true;
        public int ItemSize { get; internal set; }
    }

    /// <summary>
    /// 阈值定义（warn/crit），渲染中用于切换颜色。
    /// </summary>
    public class ThresholdSet
    {
        public double Warn { get; set; } = 70;
        public double Crit { get; set; } = 90;
    }

    /// <summary>
    /// 各类指标的阈值配置（按当前 UIRenderer 的使用保留）。
    /// </summary>
    public class ThresholdConfig
    {
        public ThresholdSet Load { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet Temp { get; set; } = new() { Warn = 50, Crit = 70 };
        public ThresholdSet Vram { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet Mem { get; set; } = new() { Warn = 65, Crit = 85 };
        public ThresholdSet NetKBps { get; set; } = new() { Warn = 2048 * 1024, Crit = 8192 * 1024 };
    }

    /// <summary>
    /// 颜色配置：只保留实际使用的颜色键
    /// - Background / GroupBackground
    /// - TextTitle / TextGroup / TextPrimary
    /// - ValueSafe / ValueWarn / ValueCrit
    /// - BarBackground / BarLow / BarMid / BarHigh
    /// </summary>
    public class ColorConfig
    {
        public string Background { get; set; } = "#202225";

        public string TextTitle { get; set; } = "#FFFFFF";
        public string TextGroup { get; set; } = "#B0B0B0";
        public string TextPrimary { get; set; } = "#EAEAEA";

        public string ValueSafe { get; set; } = "#66FF99";
        public string ValueWarn { get; set; } = "#FFD666";
        public string ValueCrit { get; set; } = "#FF6666";

        public string BarBackground { get; set; } = "#1C1C1C";
        public string BarLow { get; set; } = "#00C853";
        public string BarMid { get; set; } = "#FFAB00";
        public string BarHigh { get; set; } = "#D50000";

        public string GroupBackground { get; set; } = "#2B2D31";
    }

    /// <summary>
    /// Theme 主对象：聚合 Layout / Font / Threshold / Color。
    /// 运行期还会构建 4 类 Font 对象供渲染使用。
    /// </summary>
    public class Theme
    {
        public string Name { get; set; } = "Default";
        public int Version { get; set; } = 3;

        public LayoutConfig Layout { get; set; } = new();
        public FontConfig Font { get; set; } = new();
        public ThresholdConfig Thresholds { get; set; } = new();
        public ColorConfig Color { get; set; } = new();

        /// <summary>Scales the layout for DPI x user scale. Font sizes are scaled when drawn
        /// (Font.* x UserScale), so the theme file values stay as written.</summary>
        [JsonIgnore] public float UserScale { get; private set; } = 1f;

        public void Scale(float dpiScale, float userScale)
        {
            UserScale = userScale <= 0 ? 1f : userScale;
            Layout.LayoutScale = dpiScale * UserScale;
            Layout.Scale(Layout.LayoutScale);
        }
    }


    /// <summary>
    /// 主题管理器：负责读取 JSON、反序列化、构建字体、暴露 Current。
    /// 注意：不在此处做清缓存；清缓存应在 UIController.ApplyTheme() 统一处理。
    /// </summary>
    public static class ThemeManager
    {
        public static Theme Current { get; private set; } = new Theme();
        
        // ★★★ 修改：移除 _stringPool，只保留 _colorCache ★★★
        // private static readonly Dictionary<string, string> _stringPool ... (删除)
        private static readonly Dictionary<string, Color> _colorCache = new(32);
        private static readonly object _lock = new object(); 

        public const string SystemMode = "System", DarkDefault = "Windows11_Dark", LightDefault = "Windows11_Light";

        public static string ThemeDir
        {
            get
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "resources", "themes");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Theme file names (without .json), the two Windows 11 defaults first.</summary>
        public static IEnumerable<string> GetAvailableThemes()
        {
            EnsureDefaults();
            try
            {
                var names = Directory.EnumerateFiles(ThemeDir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).ToList();
                return names.Where(n => n == DarkDefault || n == LightDefault).OrderBy(n => n == LightDefault)
                            .Concat(names.Where(n => n != DarkDefault && n != LightDefault).OrderBy(n => n)).ToArray();
            }
            catch { return new[] { DarkDefault, LightDefault }; }
        }

        /// <summary>Windows apps set to dark?</summary>
        public static bool WindowsIsDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int v ? v == 0 : true;
            }
            catch { return true; }
        }

        /// <summary>The theme file a setting means: "System" picks the Windows 11 light or dark one.</summary>
        public static string Resolve(string skin) =>
            string.IsNullOrEmpty(skin) || skin == SystemMode ? (WindowsIsDark() ? DarkDefault : LightDefault) : skin;

        /// <summary>Should menus and windows be dark for this setting? (custom themes: by background brightness)</summary>
        public static bool IsDark(string skin)
        {
            var name = Resolve(skin);
            if (name == DarkDefault) return true;
            if (name == LightDefault) return false;
            var c = ParseColor((name == Current.Name ? Current : TryRead(name) ?? Current).Color.Background);
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;
        }

        private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, IgnoreReadOnlyProperties = true, AllowTrailingCommas = true };

        private static Theme? TryRead(string name)
        {
            try
            {
                var path = Path.Combine(ThemeDir, name + ".json");
                return File.Exists(path) ? JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), ReadOptions) : null;
            }
            catch { return null; }
        }

        /// <summary>Loads a theme ("System" or a file name) and makes it current. Falls back to the Windows 11 defaults.</summary>
        public static Theme Load(string skin)
        {
            EnsureDefaults();
            string name = Resolve(skin);
            var theme = TryRead(name) ?? (WindowsIsDark() || name == DarkDefault ? Windows11Dark() : Windows11Light());
            if (string.IsNullOrEmpty(theme.Name)) theme.Name = name;
            theme.Font.Family = UIUtils.Intern(theme.Font.Family ?? "");
            theme.Font.ValueFamily = UIUtils.Intern(theme.Font.ValueFamily ?? "");
            Current = theme;
            return theme;
        }

        /// <summary>Puts the two Windows 11 theme files back if they were deleted.</summary>
        private static void EnsureDefaults()
        {
            try
            {
                foreach (var (name, make) in new (string, Func<Theme>)[] { (DarkDefault, Windows11Dark), (LightDefault, Windows11Light) })
                {
                    var path = Path.Combine(ThemeDir, name + ".json");
                    if (!File.Exists(path)) File.WriteAllText(path, JsonSerializer.Serialize(make(), new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        /// <summary>Makes a theme object the current one (the theme editor's live preview).</summary>
        public static void SetCurrent(Theme theme) => Current = theme;

        /// <summary>A deep copy (themes are scaled in place when applied).</summary>
        public static Theme Clone(Theme theme) =>
            JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(theme)) ?? new Theme();

        // The defaults, also written to resources/themes when missing.
        private static Theme Base() => new()
        {
            Version = 3,
            Layout = new LayoutConfig
            {
                Width = 240, LayoutScale = 1, RowHeight = 38, Padding = 10, CornerRadius = 8, GroupRadius = 4, GroupPadding = 10,
                GroupSpacing = 30, GroupBottom = 0, ItemGap = 4, GroupTitleOffset = 6,
            },
            Font = new FontConfig
            {
                Family = "Segoe UI Variable Text", ValueFamily = "Segoe UI Semibold",
                Title = 10.5, Group = 9.5, Item = 9.5, Value = 11, Bold = false,
            },
        };

        private static Theme Windows11Dark()
        {
            var t = Base();
            t.Name = DarkDefault;
            t.Color = new ColorConfig
            {
                Background = "#202020", GroupBackground = "#2B2B2B",
                TextTitle = "#FFFFFF", TextGroup = "#C5C5C5", TextPrimary = "#FFFFFF",
                ValueSafe = "#FFFFFF", ValueWarn = "#FCE100", ValueCrit = "#FF99A4",
                BarBackground = "#454545", BarLow = "#60CDFF", BarMid = "#FCE100", BarHigh = "#FF99A4",
            };
            return t;
        }

        private static Theme Windows11Light()
        {
            var t = Base();
            t.Name = LightDefault;
            t.Color = new ColorConfig
            {
                Background = "#F3F3F3", GroupBackground = "#FFFFFF",
                TextTitle = "#1A1A1A", TextGroup = "#5D5D5D", TextPrimary = "#1A1A1A",
                ValueSafe = "#1A1A1A", ValueWarn = "#9D5D00", ValueCrit = "#C42B1C",
                BarBackground = "#D9D9D9", BarLow = "#005FB8", BarMid = "#9D5D00", BarHigh = "#C42B1C",
            };
            return t;
        }

        private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

        public static Color ParseColor(string colorStr)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) 
                return Transparent;

            // ★★★ 修改：使用 UIUtils 的全局池 ★★★
            string key = UIUtils.Intern(colorStr);
            
            lock (_lock)
            {
                if (_colorCache.TryGetValue(key, out var cached))
                    return cached;
            }

            Color color;
            if (colorStr.StartsWith('#'))
            {
                ReadOnlySpan<char> hex = colorStr.AsSpan(1);
                try {
                    if (hex.Length == 6)
                    {
                        int r = Convert.ToInt32(hex.Slice(0, 2).ToString(), 16);
                        int g = Convert.ToInt32(hex.Slice(2, 2).ToString(), 16);
                        int b = Convert.ToInt32(hex.Slice(4, 2).ToString(), 16);
                        color = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
                    }
                    else if (hex.Length == 8)
                    {
                        int a = Convert.ToInt32(hex.Slice(0, 2).ToString(), 16);
                        int r = Convert.ToInt32(hex.Slice(2, 2).ToString(), 16);
                        int g = Convert.ToInt32(hex.Slice(4, 2).ToString(), 16);
                        int b = Convert.ToInt32(hex.Slice(6, 2).ToString(), 16);
                        color = Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
                    }
                    else
                    {
                        color = Transparent;
                    }
                } catch { color = Transparent; }
            }
            else
            {
                color = colorStr.Equals("white", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(255, 255, 255, 255)
                    : colorStr.Equals("black", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(255, 0, 0, 0) : Transparent;
            }

            // 🔒 写缓存加锁
            lock (_lock)
            {
                _colorCache[key] = color;
            }
            return color;
        }

        public static void ClearCaches()
        {
            lock (_lock) // 🔒 加锁
            {
                _colorCache.Clear();
                // _stringPool.Clear(); (删除，这里不需要清理全局字符串，因为主题切换不影响硬件Key)
            }

        }
    }
}