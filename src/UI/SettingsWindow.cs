using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinMonitor.Core;
using WinMonitor.Services;
using Windows.ApplicationModel.DataTransfer;

namespace WinMonitor.UI
{
    /// <summary>Settings: changes apply (and are saved) straight away.</summary>
    internal sealed class SettingsWindow : ToolWindow
    {
        private readonly AppHost _app;
        private readonly Settings _cfg;
        private readonly NavigationView _nav;
        private readonly Dictionary<string, Func<UIElement>> _pages = new();

        private static string T(string key) => LanguageManager.T(key);

        public SettingsWindow(AppHost app) : base(T("Menu.SettingsPanel") + " - WinMonitor", 900, 720)
        {
            _app = app;
            _cfg = app.Cfg;

            _nav = new NavigationView
            {
                PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
                IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                IsSettingsVisible = false,
                IsPaneToggleButtonVisible = false,
                OpenPaneLength = 220,
            };
            void Add(string key, string text, string glyph, Func<UIElement> page)
            {
                _pages[key] = page;
                _nav.MenuItems.Add(new NavigationViewItem { Content = text, Tag = key, Icon = new FontIcon { Glyph = glyph } });
            }
            Add("Panel", T("Menu.MainFormSettings"), "\uE771", PanelPage);   // Personalize
            Add("Items", T("Menu.MonitorItemDisplay"), "\uE8FD", ItemsPage);  // BulletedList
            Add("Alerts", T("Menu.Thresholds"), "\uEA8F", AlertsPage);        // Ringer
            Add("Hardware", T("Menu.SystemHardwar") is var h && h != "SystemHardwar" ? h : "Hardware", "\uE950", HardwarePage);  // Component
            Add("About", T("Menu.About"), "\uE946", AboutPage);               // Info
            _nav.SelectionChanged += (_, e) =>
            {
                if (e.SelectedItem is NavigationViewItem item && item.Tag is string key) _nav.Content = _pages[key]();
            };
            SetBody(_nav);
            ShowPage("Panel");
        }

        public void ShowPage(string key)
        {
            var item = _nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key);
            if (item == null) return;
            if (ReferenceEquals(_nav.SelectedItem, item)) _nav.Content = _pages[key]();
            else _nav.SelectedItem = item;
        }

        private void Save() => _cfg.Save();

        // ================================================================== Panel
        private UIElement PanelPage()
        {
            var c = _cfg;
            double[] opacities = { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 };
            int[] actionIds = { 2, 1, 5 };

            return Ui.Page(
                Ui.PageTitle(T("Menu.MainFormSettings")),

                Ui.Section(T("Menu.Appearance")),
                Ui.Row(T("Menu.Theme"), ThemeRow(c), "Dark, Light, or follow Windows", "\uE790"),
                Ui.LinkRow(T("Menu.ThemeEditor"), _app.OpenThemeEditor, "Make your own colours, fonts and spacing", "\uE70F"),
                Ui.Row(T("Menu.Width"), Ui.NumberUnit(c.PanelWidth, 100, 10000, "px",
                    v => { c.PanelWidth = (int)v; Save(); _app.ApplyThemeAndLayout(retainData: true); }, 10), null, "\uE740"),
                Ui.Row(T("Menu.Scale"), Ui.NumberUnit(Math.Round(c.UIScale * 100), 10, 1000, "%",
                    v => { c.UIScale = v / 100.0; Save(); _app.ApplyThemeAndLayout(retainData: true); }, 5), null, "\uE8A3"),
                Ui.Row(T("Menu.Opacity"), Ui.Combo(opacities.Select(o => $"{o * 100:0}%"), Array.FindIndex(opacities, o => Math.Abs(o - c.Opacity) < 0.01),
                    i => { c.Opacity = opacities[i]; Save(); _app.ApplyWindowAttributes(); }, 120), null, "\uE706"),
                Ui.Row(T("Menu.ShowTitle"), Ui.Toggle(c.ShowTitle, v => { c.ShowTitle = v; Save(); _app.ApplyThemeAndLayout(retainData: true); }),
                    "Category names above each group", "\uE8D2"),

                Ui.Section("Behaviour"),
                Ui.Row(T("Menu.TopMost"), Ui.Toggle(c.TopMost, v => { c.TopMost = v; Save(); _app.ApplyWindowAttributes(); }), null, "\uE718"),
                Ui.Row(T("Menu.ClampToScreen"), Ui.Toggle(c.ClampToScreen, v => { c.ClampToScreen = v; Save(); }),
                    "Stop the panel from being dragged off the edge", "\uE7F4"),
                Ui.Row(T("Menu.ClickThrough"), Ui.Toggle(c.ClickThrough, v => { c.ClickThrough = v; Save(); _app.ApplyWindowAttributes(); }),
                    "Clicks go to the window underneath. Use the tray icon to reach the menu.", "\uE962"),
                Ui.Row(T("Menu.DoubleClickAction"), Ui.Combo(
                    new[] { T("Menu.ActionSettings"), T("Menu.ActionTaskMgr"), "Nothing" },
                    Math.Max(0, Array.IndexOf(actionIds, c.MainFormDoubleClickAction)),
                    i => { c.MainFormDoubleClickAction = actionIds[i]; Save(); }, 200), null, "\uE8B0"),
                Ui.Row(T("Menu.HideMainForm"), Ui.Toggle(c.HideMainForm, v => { c.HideMainForm = v; Save(); _app.ApplyVisibility(); }),
                    "Keep only the tray icon", "\uED1A"),

                Ui.Section(T("Menu.Devices")),
                Ui.Group(T("Menu.Devices"), null, "Wireless mouse, keyboard, headset and controller batteries", "\uE772",
                    Ui.Row(T("Menu.DevicesInterval"), Ui.NumberUnit(c.HaloPollSeconds, 1, 86400, "s",
                        v => { c.HaloPollSeconds = (int)v; Save(); }), T("Menu.DevicesHint")),
                    Ui.Row(T("Menu.DevicesPsFullMode"), Ui.Toggle(c.HaloPsBluetoothFullMode, v => { c.HaloPsBluetoothFullMode = v; Save(); }),
                        T("Menu.DevicesPsFullModeHint")),
                    Ui.Row(T("Menu.DevicesReport"), Ui.Button(T("Menu.DevicesReportCopy"), CopyDeviceReport),
                        "What each device reader saw, for troubleshooting a device that does not show."))
            );
        }

        private FrameworkElement ThemeRow(Settings c)
        {
            var keys = new List<string> { ThemeManager.SystemMode };
            keys.AddRange(ThemeManager.GetAvailableThemes());
            var labels = keys.Select(k => k == ThemeManager.SystemMode ? AppHost.SystemThemeLabel : k);
            return Ui.Combo(labels, Math.Max(0, keys.FindIndex(k => k.Equals(c.Skin, StringComparison.OrdinalIgnoreCase))),
                i => _app.SetTheme(keys[i]), 200);
        }

        private void CopyDeviceReport()
        {
            try
            {
                var dp = new DataPackage();
                dp.SetText(Services.Halo.HaloEngine.Report());
                Clipboard.SetContent(dp);
                Clipboard.Flush();
                _app.Notify("WinMonitor", T("Menu.DevicesReportCopied"), false);
            }
            catch (Exception ex) { Dialogs.Error(ex.Message); }
        }

        // ================================================================== Monitor items
        private UIElement ItemsPage() => new MonitorItemsPage(_app).Root;

        // ================================================================== Alerts & thresholds
        private UIElement AlertsPage()
        {
            var c = _cfg;
            FrameworkElement Threshold(ValueRange r, string unit)
            {
                var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                p.Children.Add(new TextBlock { Text = T("Menu.ValueWarnColor"), VerticalAlignment = VerticalAlignment.Center, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0xA0, 0x20)) });
                p.Children.Add(Ui.Number(r.Warn, 0, 1_000_000, v => { r.Warn = v; Save(); }));
                p.Children.Add(new TextBlock { Text = T("Menu.ValueCritColor"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x50, 0x50)) });
                p.Children.Add(Ui.Number(r.Crit, 0, 1_000_000, v => { r.Crit = v; Save(); }));
                p.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, Foreground = Ui.Res("TextFillColorSecondaryBrush") });
                return p;
            }

            return Ui.Page(
                Ui.PageTitle(T("Menu.Thresholds")),
                Ui.Section("Notifications"),
                Ui.Group(T("Menu.AlertTemp"), Ui.Toggle(c.AlertTempEnabled, v => { c.AlertTempEnabled = v; Save(); }),
                    "When a temperature stays at or above its limit for 5 seconds (then at most every 3 minutes)", "\uE7BA",
                    Ui.Row("CPU, GPU and motherboard limit", Ui.NumberUnit(c.AlertTempThreshold, 30, 120, "°C", v => { c.AlertTempThreshold = (int)v; Save(); })),
                    Ui.Row("Disk limit", Ui.NumberUnit(c.AlertDiskTempThreshold, 30, 100, "°C", v => { c.AlertDiskTempThreshold = (int)v; Save(); }))),
                Ui.Group(T("Menu.AlertBattery"), Ui.Toggle(c.AlertBatteryEnabled, v => { c.AlertBatteryEnabled = v; Save(); }),
                    "Once when a wireless device drops to the limit, again after it charges", "\uE83F",
                    Ui.Row(T("Menu.AlertThreshold"), Ui.NumberUnit(c.AlertBatteryThreshold, 0, 100, "%", v => { c.AlertBatteryThreshold = (int)v; Save(); }),
                        "Rows turn red at the limit and yellow up to 10% above it")),

                Ui.Section("Colours"),
                Ui.Hint(T("Menu.ThresholdsTips")),
                Ui.Row(T("Menu.HardwareLoad"), Threshold(c.Thresholds.Load, "%"), null, "\uE9D9"),
                Ui.Row(T("Menu.HardwareTemp"), Threshold(c.Thresholds.Temp, "°C"), null, "\uE7BA"),
                Ui.Row(T("Menu.DiskIOSpeed"), Threshold(c.Thresholds.DiskIOMB, "MB/s"), null, "\uEDA2"),
                Ui.Row(T("Menu.UploadSpeed"), Threshold(c.Thresholds.NetUpMB, "MB/s"), null, "\uE898"),
                Ui.Row(T("Menu.DownloadSpeed"), Threshold(c.Thresholds.NetDownMB, "MB/s"), null, "\uE896"),
                Ui.Group(T("Menu.DailyTraffic"), null, null, "\uE9F9",
                    Ui.Row(T("Items.DATA.DayUp"), Threshold(c.Thresholds.DataUpMB, "MB")),
                    Ui.Row(T("Items.DATA.DayDown"), Threshold(c.Thresholds.DataDownMB, "MB")))
            );
        }

        // ================================================================== Hardware & system
        private UIElement HardwarePage()
        {
            var c = _cfg;
            string auto = T("Menu.Auto");
            int[] rates = { 100, 200, 300, 500, 600, 700, 800, 1000, 1500, 2000, 3000 };

            ComboBox Source(Func<string> get, Action<string> set)
            {
                var cb = new ComboBox { Width = 280, PlaceholderText = auto };
                cb.Items.Add(auto);
                string cur = get();
                if (!string.IsNullOrEmpty(cur)) cb.Items.Add(cur);
                cb.SelectedIndex = string.IsNullOrEmpty(cur) ? 0 : 1;
                cb.Tag = set;
                return cb;
            }
            var disk = Source(() => c.PreferredDisk, v => c.PreferredDisk = v);
            var net = Source(() => c.PreferredNetwork, v => c.PreferredNetwork = v);
            var gpu = new ComboBox { Width = 280 };
            gpu.Items.Add(new ComboBoxItem { Content = auto, Tag = "" });
            gpu.SelectedIndex = 0;
            var mobo = Source(() => c.PreferredMoboTemp, v => c.PreferredMoboTemp = v);
            var cpuFan = Source(() => c.PreferredCpuFan, v => c.PreferredCpuFan = v);
            var pump = Source(() => c.PreferredCpuPump, v => c.PreferredCpuPump = v);
            var caseFan = Source(() => c.PreferredCaseFan, v => c.PreferredCaseFan = v);
            _ = FillSourcesAsync(disk, net, gpu, mobo, cpuFan, pump, caseFan);

            FrameworkElement Calib(float value, string unit, Action<float> set) =>
                Ui.NumberUnit(value, 1, 100000, unit, v => { set((float)v); Save(); });
            string Max(string key) => T(key) + " (" + T("Menu.MaxLimits") + ")";

            return Ui.Page(
                Ui.PageTitle(T("Menu.SystemHardwar") is var sh && sh != "SystemHardwar" ? sh : "System"),
                Ui.Section(T("Menu.SystemSettings")),
                Ui.Row(T("Menu.AutoStart"), Ui.Toggle(c.AutoStart, v => { c.AutoStart = v; Save(); _app.ApplyAutoStart(); }),
                    "When you sign in, without an administrator prompt", "\uE7E8"),
                Ui.Row(T("Menu.HideTrayIcon"), Ui.Toggle(c.HideTrayIcon, v => { c.HideTrayIcon = v; Save(); _app.ApplyVisibility(); }), null, "\uED1A"),

                Ui.Section(T("Menu.HardwareSettings")),
                Ui.Row(T("Menu.Refresh"), Ui.Combo(rates.Select(r => r + " ms"), Array.IndexOf(rates, c.RefreshMs),
                    i => { c.RefreshMs = rates[i]; Save(); _app.ApplyThemeAndLayout(retainData: true); }, 120), "How often the panel updates", "\uE72C"),
                Ui.Row(T("Menu.MemoryDisplayMode"), Ui.Combo(new[] { T("Menu.Percent"), T("Menu.UsedSize") }, c.MemoryDisplayMode,
                    i => { c.MemoryDisplayMode = i; Save(); _app.ApplyMonitorLayout(); }, 160), null, "\uE964"),
                Ui.Row(T("Menu.UseWinPerCounters"), Ui.Toggle(c.UseWinPerCounters, v => { c.UseWinPerCounters = v; Save(); _app.ApplyMonitorLayout(); }),
                    "Read load, memory, disk and network from Windows", "\uE9D9"),
                Ui.Row(T("Menu.IgnoreSMBTraffic"), Ui.Toggle(c.IgnoreSmbTraffic, v => { c.IgnoreSmbTraffic = v; Save(); _app.ApplyMonitorLayout(); }),
                    "Leave out network file copies (SMB) from the network speed", "\uE839"),
                Ui.Group("Sensor sources", null, "Which disk, network, GPU and fans the panel shows", "\uE950",
                    Ui.Row(T("Menu.DiskSource"), disk),
                    Ui.Row(T("Menu.NetworkSource"), net),
                    Ui.Row(T("Menu.GpuSource"), gpu),
                    Ui.Row(T("Items.MOBO.Temp"), mobo),
                    Ui.Row(T("Items.CPU.Fan"), cpuFan),
                    Ui.Row(T("Items.CPU.Pump"), pump),
                    Ui.Row(T("Items.CASE.Fan"), caseFan)),
                Ui.Group(T("Menu.Calibration"), null, T("Menu.CalibrationTip"), "\uE8EF",
                    Ui.Row(Max("Items.CPU.Power"), Calib(c.RecordedMaxCpuPower, "W", v => c.RecordedMaxCpuPower = v)),
                    Ui.Row(Max("Items.CPU.Clock"), Calib(c.RecordedMaxCpuClock, "MHz", v => c.RecordedMaxCpuClock = v)),
                    Ui.Row(Max("Items.GPU.Power"), Calib(c.RecordedMaxGpuPower, "W", v => c.RecordedMaxGpuPower = v)),
                    Ui.Row(Max("Items.GPU.Clock"), Calib(c.RecordedMaxGpuClock, "MHz", v => c.RecordedMaxGpuClock = v)),
                    Ui.Row(Max("Items.CPU.Fan"), Calib(c.RecordedMaxCpuFan, "RPM", v => c.RecordedMaxCpuFan = v)),
                    Ui.Row(Max("Items.CPU.Pump"), Calib(c.RecordedMaxCpuPump, "RPM", v => c.RecordedMaxCpuPump = v)),
                    Ui.Row(Max("Items.GPU.Fan"), Calib(c.RecordedMaxGpuFan, "RPM", v => c.RecordedMaxGpuFan = v)),
                    Ui.Row(Max("Items.CASE.Fan"), Calib(c.RecordedMaxChassisFan, "RPM", v => c.RecordedMaxChassisFan = v)))
            );
        }

        private async Task FillSourcesAsync(ComboBox disk, ComboBox net, ComboBox gpu, ComboBox mobo, ComboBox cpuFan, ComboBox pump, ComboBox caseFan)
        {
            try
            {
                var mon = HardwareMonitor.Instance;
                if (mon == null) return;
                var tDisk = Task.Run(() => HardwareScanner.ListAllDisks(mon.ComputerInstance));
                var tNet = Task.Run(() => HardwareScanner.ListAllNetworks(mon.ComputerInstance));
                var tGpu = Task.Run(() => HardwareMonitor.ListAllGpuOptions());
                var tFans = Task.Run(() => HardwareScanner.ListAllFans(mon.ComputerInstance, mon.SyncLock));
                var tMobo = Task.Run(() => HardwareScanner.ListAllMoboTemps(mon.ComputerInstance, mon.SyncLock));
                await Task.WhenAll(tDisk, tNet, tGpu, tFans, tMobo);

                string auto = T("Menu.Auto");
                void Fill(ComboBox cb, List<string> data, string current)
                {
                    var set = (Action<string>)cb.Tag;
                    cb.Items.Clear();
                    cb.Items.Add(auto);
                    foreach (var d in data) cb.Items.Add(d);
                    cb.SelectedIndex = !string.IsNullOrEmpty(current) && data.Contains(current) ? data.IndexOf(current) + 1 : 0;
                    cb.SelectionChanged += (_, _) =>
                    {
                        if (cb.SelectedIndex < 0) return;
                        set(cb.SelectedIndex == 0 ? "" : (string)cb.SelectedItem);
                        Save();
                        _app.ApplyMonitorLayout();
                    };
                }
                Fill(disk, tDisk.Result, _cfg.PreferredDisk);
                Fill(net, tNet.Result, _cfg.PreferredNetwork);
                Fill(mobo, tMobo.Result, _cfg.PreferredMoboTemp);
                Fill(cpuFan, tFans.Result, _cfg.PreferredCpuFan);
                Fill(pump, tFans.Result, _cfg.PreferredCpuPump);
                Fill(caseFan, tFans.Result, _cfg.PreferredCaseFan);

                // GPUs: shown by label, stored by value (an old setting may hold the plain name)
                var gpus = tGpu.Result;
                gpu.Items.Clear();
                gpu.Items.Add(new ComboBoxItem { Content = auto, Tag = "" });
                foreach (var o in gpus) gpu.Items.Add(new ComboBoxItem { Content = o.Label, Tag = o.Value });
                var cur = gpus.FirstOrDefault(x => string.Equals(x.Value, _cfg.PreferredGpu, StringComparison.OrdinalIgnoreCase));
                if (cur == null)
                {
                    var byName = gpus.Where(x => string.Equals(x.Name, _cfg.PreferredGpu, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byName.Count == 1) cur = byName[0];
                }
                gpu.SelectedIndex = cur == null ? 0 : gpus.IndexOf(cur) + 1;
                gpu.SelectionChanged += (_, _) =>
                {
                    if (gpu.SelectedItem is not ComboBoxItem it) return;
                    _cfg.PreferredGpu = (string)it.Tag;
                    Save();
                    _app.ApplyMonitorLayout();
                };
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ================================================================== About
        private UIElement AboutPage()
        {
            var icon = new Microsoft.UI.Xaml.Controls.ImageIcon
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png"))),
            };
            var app = new CommunityToolkit.WinUI.Controls.SettingsExpander
            {
                Header = "WinMonitor",
                Description = "Hardware monitor with wireless device batteries. Free and open source (MIT).",
                HeaderIcon = icon,
                Content = new TextBlock { Text = AppInfo.Version, Foreground = Ui.Res("TextFillColorSecondaryBrush"), IsTextSelectionEnabled = true },
                IsExpanded = true,
            };
            app.Items.Add(Ui.Row("Source code and releases", Ui.Link("GitHub", AppInfo.RepoUrl)));
            app.Items.Add(Ui.Row("Report a problem", Ui.Link("Issues", AppInfo.IssuesUrl)));

            FrameworkElement Credit(string name, string what, string licence) => Ui.Row(name,
                new TextBlock { Text = licence, Foreground = Ui.Res("TextFillColorSecondaryBrush") }, what);

            return Ui.Page(
                Ui.PageTitle(T("Menu.About")),
                app,
                Ui.Section("Credits"),
                Ui.Group("Open-source components", null, "Full licence texts are in THIRD-PARTY-NOTICES.txt next to WinMonitor.exe", "\uE8F1",
                    Credit("LiteMonitor", "The monitor this app is based on (Diorser)", "MIT"),
                    Credit("Halo Battery", "Wireless device battery readers", "MIT"),
                    Credit("LibreHardwareMonitor", "Sensor readings", "MPL-2.0"),
                    Credit("Windows App SDK / WinUI 3", "User interface", "MIT"),
                    Credit("Windows Community Toolkit", "Settings cards", "MIT"),
                    Credit("WinUIEx", "See-through window technique (Morten Nielsen)", "MIT"),
                    Credit("PawnIO", "Optional driver for CPU temperature, clock and power (namazso)", "Official release")),
                Ui.Section(T("Menu.Reset")),
                Ui.Row(T("Menu.Reset"), Ui.Button(T("Menu.Reset") + "…", ResetSettings), "Deletes settings.json and restarts WinMonitor with the defaults.", "\uE777")
            );
        }

        private void ResetSettings()
        {
            if (!Dialogs.YesNo(T("Menu.ResetConfirm"), T("Menu.Reset"))) return;
            try
            {
                Settings.GlobalBlockSave = true;
                SettingsHelper.DeleteStoredSettings();
                _app.Restart();
            }
            catch (Exception ex) { Settings.GlobalBlockSave = false; Dialogs.Error(ex.Message); }
        }
    }
}
