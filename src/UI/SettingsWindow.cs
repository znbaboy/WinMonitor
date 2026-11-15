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
            void Add(string key, string text, Symbol icon, Func<UIElement> page)
            {
                _pages[key] = page;
                _nav.MenuItems.Add(new NavigationViewItem { Content = text, Tag = key, Icon = new SymbolIcon(icon) });
            }
            Add("Panel", T("Menu.MainFormSettings"), Symbol.ViewAll, PanelPage);
            Add("Items", T("Menu.MonitorItemDisplay"), Symbol.List, ItemsPage);
            Add("Alerts", T("Menu.Thresholds"), Symbol.Important, AlertsPage);
            Add("Hardware", T("Menu.SystemHardwar") is var h && h != "SystemHardwar" ? h : "Hardware", Symbol.Setting, HardwarePage);
            Add("About", T("Menu.About"), Symbol.Help, AboutPage);
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
                Ui.Row(T("Menu.Theme"), Ui.Combo(AppHost.ThemeModes.Select(m => m.Label), Array.FindIndex(AppHost.ThemeModes, m => m.Key == c.Skin),
                    i => { c.Skin = AppHost.ThemeModes[i].Key; Save(); _app.ApplyAppTheme(); }), "Windows 11 style, light or dark."),
                Ui.Row(T("Menu.Width"), Ui.NumberUnit(c.PanelWidth, 100, 10000, "px",
                    v => { c.PanelWidth = (int)v; Save(); _app.ApplyThemeAndLayout(retainData: true); }, 10)),
                Ui.Row(T("Menu.Opacity"), Ui.Combo(opacities.Select(o => $"{o * 100:0}%"), Array.FindIndex(opacities, o => Math.Abs(o - c.Opacity) < 0.01),
                    i => { c.Opacity = opacities[i]; Save(); _app.ApplyWindowAttributes(); })),
                Ui.Row(T("Menu.Scale"), Ui.NumberUnit(Math.Round(c.UIScale * 100), 10, 1000, "%",
                    v => { c.UIScale = v / 100.0; Save(); _app.ApplyThemeAndLayout(retainData: true); }, 5)),
                Ui.Row(T("Menu.ShowTitle"), Ui.Toggle(c.ShowTitle, v => { c.ShowTitle = v; Save(); _app.ApplyThemeAndLayout(retainData: true); })),

                Ui.Section(T("Menu.MainFormSettings")),
                Ui.Row(T("Menu.HideMainForm"), Ui.Toggle(c.HideMainForm, v => { c.HideMainForm = v; Save(); _app.ApplyVisibility(); })),
                Ui.Row(T("Menu.TopMost"), Ui.Toggle(c.TopMost, v => { c.TopMost = v; Save(); _app.ApplyWindowAttributes(); })),
                Ui.Row(T("Menu.ClampToScreen"), Ui.Toggle(c.ClampToScreen, v => { c.ClampToScreen = v; Save(); })),
                Ui.Row(T("Menu.AutoHide"), Ui.Toggle(c.AutoHide, v => { c.AutoHide = v; Save(); _app.ApplyWindowAttributes(); }),
                    "Slides the panel off the screen edge when it is docked there; point at the edge to bring it back."),
                Ui.Row(T("Menu.ClickThrough"), Ui.Toggle(c.ClickThrough, v => { c.ClickThrough = v; Save(); _app.ApplyWindowAttributes(); }),
                    "Clicks go to the window underneath. Use the tray icon to reach the menu."),
                Ui.Row(T("Menu.DoubleClickAction"), Ui.Combo(
                    new[] { T("Menu.ActionSettings"), T("Menu.ActionTaskMgr"), "Nothing" },
                    Math.Max(0, Array.IndexOf(actionIds, c.MainFormDoubleClickAction)),
                    i => { c.MainFormDoubleClickAction = actionIds[i]; Save(); })),

                Ui.Section(T("Menu.Devices")),
                Ui.Row(T("Menu.DevicesInterval"), Ui.NumberUnit(c.HaloPollSeconds, 1, 86400, "s",
                    v => { c.HaloPollSeconds = (int)v; Save(); }), T("Menu.DevicesHint")),
                Ui.Row(T("Menu.DevicesPsFullMode"), Ui.Toggle(c.HaloPsBluetoothFullMode, v => { c.HaloPsBluetoothFullMode = v; Save(); }),
                    T("Menu.DevicesPsFullModeHint")),
                Ui.Row(T("Menu.DevicesReport"), Ui.Button(T("Menu.DevicesReportCopy"), CopyDeviceReport),
                    "What each device reader saw, for troubleshooting a device that does not show.")
            );
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
        private ScrollViewer? _itemsScroll;

        private UIElement ItemsPage()
        {
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(24, 8, 24, 24), MaxWidth = 1000 };
            stack.Children.Add(Ui.PageTitle(T("Menu.MonitorItemDisplay")));
            stack.Children.Add(Ui.Hint(T("Menu.MonitorItemTip") + "  Names and units apply when you press Enter or leave the box."));

            var groups = _cfg.MonitorItems.OrderBy(x => x.SortIndex).GroupBy(x => x.UIGroup).Select(g => g.ToList()).ToList();
            for (int gi = 0; gi < groups.Count; gi++)
                stack.Children.Add(GroupCard(groups, gi));

            double offset = _itemsScroll?.VerticalOffset ?? 0;
            _itemsScroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            if (offset > 0) _itemsScroll.Loaded += (s, _) => ((ScrollViewer)s).ChangeView(null, offset, null, true);
            return _itemsScroll;
        }

        private Border GroupCard(List<List<MonitorItemConfig>> groups, int gi)
        {
            var items = groups[gi];
            string key = items[0].UIGroup;
            var panel = new StackPanel { Spacing = 2 };

            // header: group name, all on/off, order
            string defName = LanguageManager.GetOriginal("Groups." + key);
            if (defName.StartsWith("Groups.")) defName = key;
            var alias = new TextBox { Text = _cfg.GroupAliases.TryGetValue(key, out var a) ? a : defName, Width = 150, FontWeight = Ui.Bold };
            CommitOnEnterOrLeave(alias, () =>
            {
                string v = alias.Text.Trim();
                if (!string.IsNullOrEmpty(v) && v != defName) _cfg.GroupAliases[key] = v;
                else { _cfg.GroupAliases.Remove(key); alias.Text = defName; }
                ApplyItems(rebuildPage: false);
            });
            var all = new CheckBox { Content = T("Menu.MainForm"), IsChecked = items.All(i => i.VisibleInPanel), MinWidth = 0 };
            all.Click += (_, _) => { foreach (var i in items) i.VisibleInPanel = all.IsChecked == true; ApplyItems(rebuildPage: true); };
            panel.Children.Add(RowGrid(
                new TextBlock { Text = key, Foreground = Ui.Res("TextFillColorSecondaryBrush"), FontWeight = Ui.Bold, VerticalAlignment = VerticalAlignment.Center },
                alias, null, all, null, Arrows(() => MoveGroup(groups, gi, -1), () => MoveGroup(groups, gi, 1))));
            panel.Children.Add(new Border { Height = 1, Background = Ui.Res("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 4, 0, 4) });

            for (int ii = 0; ii < items.Count; ii++)
                panel.Children.Add(ItemRow(groups, gi, ii));
            return Ui.Card(panel, new Thickness(16, 8, 16, 8));
        }

        private FrameworkElement ItemRow(List<List<MonitorItemConfig>> groups, int gi, int ii)
        {
            var item = groups[gi][ii];

            string resolved = MetricLabelResolver.ResolveLabel(item);
            string defName = T(UIUtils.Intern("Items." + item.Key));
            var name = new TextBox { Text = !string.IsNullOrEmpty(resolved) ? resolved : defName, Width = 150 };
            CommitOnEnterOrLeave(name, () =>
            {
                // a single space hides the name; the built-in or live name means "automatic"
                string val = name.Text;
                if (val.Length > 0 && string.IsNullOrWhiteSpace(val)) val = " ";
                else val = val.Trim();
                string original = LanguageManager.GetOriginal(UIUtils.Intern("Items." + item.Key));
                string dyn = InfoService.Instance.GetValue("PROP.Label." + item.Key);
                if (string.IsNullOrEmpty(dyn)) dyn = item.DynamicLabel;
                bool isDefault = string.Equals(val, original, StringComparison.Ordinal) || (!string.IsNullOrEmpty(dyn) && val == dyn) || val == "";
                item.UserLabel = val != " " && isDefault ? "" : val;
                if (val == "") name.Text = original;
                ApplyItems(rebuildPage: false);
            });

            // unit: empty = automatic, spaces = none, anything else = custom format ({u} = the automatic unit)
            string defUnit = MetricUtils.GetUnitStr(item.Key, 0, MetricUtils.UnitContext.SettingsPanel);
            var unit = new TextBox { Text = item.UnitPanel == null ? defUnit : (item.UnitPanel == "" ? " " : item.UnitPanel), Width = 70 };
            CommitOnEnterOrLeave(unit, () =>
            {
                string raw = unit.Text;
                if (string.IsNullOrEmpty(raw)) { item.UnitPanel = null; unit.Text = defUnit; }
                else if (string.IsNullOrWhiteSpace(raw)) item.UnitPanel = "";
                else item.UnitPanel = string.Equals(raw, defUnit, StringComparison.Ordinal) ? null : raw;
                ApplyItems(rebuildPage: false);
            });

            var show = new CheckBox { Content = T("Menu.MainForm"), IsChecked = item.VisibleInPanel, MinWidth = 0 };
            show.Click += (_, _) => { item.VisibleInPanel = show.IsChecked == true; ApplyItems(rebuildPage: false); };
            CheckBox? bar = null;
            if (item.HasBar)
            {
                bar = new CheckBox { Content = T("Menu.ShowBar"), IsChecked = item.ShowBar, MinWidth = 0 };
                bar.Click += (_, _) => { item.ShowBar = bar.IsChecked == true; ApplyItems(rebuildPage: false); };
            }

            return RowGrid(
                new TextBlock { Text = item.Key, Foreground = Ui.Res("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                name, unit, show, bar, Arrows(() => MoveItem(groups, gi, ii, -1), () => MoveItem(groups, gi, ii, 1)));
        }

        private static Grid RowGrid(FrameworkElement key, FrameworkElement name, FrameworkElement? unit, FrameworkElement show, FrameworkElement? bar, FrameworkElement arrows)
        {
            var g = new Grid { ColumnSpacing = 8, MinHeight = 40 };
            foreach (var w in new[] { 104.0, 150, 70, 80, 64 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cells = new[] { key, name, unit, show, bar, arrows };
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null) continue;
                cells[i]!.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(cells[i]!, i);
                g.Children.Add(cells[i]!);
            }
            arrows.HorizontalAlignment = HorizontalAlignment.Right;
            return g;
        }

        private static StackPanel Arrows(Action up, Action down)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            Button B(string glyph, Action a, string tip)
            {
                var b = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 12 }, Padding = new Thickness(8, 6, 8, 6) };
                ToolTipService.SetToolTip(b, tip);
                b.Click += (_, _) => a();
                return b;
            }
            p.Children.Add(B("", up, "Move up"));
            p.Children.Add(B("", down, "Move down"));
            return p;
        }

        private static void CommitOnEnterOrLeave(TextBox box, Action commit)
        {
            string last = box.Text;
            void Do() { if (box.Text == last) return; commit(); last = box.Text; }
            box.LostFocus += (_, _) => Do();
            box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Do(); e.Handled = true; } };
        }

        private void MoveItem(List<List<MonitorItemConfig>> groups, int gi, int ii, int dir)
        {
            var list = groups[gi];
            int j = ii + dir;
            if (j < 0 || j >= list.Count) return;
            (list[ii], list[j]) = (list[j], list[ii]);
            Renumber(groups);
        }

        private void MoveGroup(List<List<MonitorItemConfig>> groups, int gi, int dir)
        {
            int j = gi + dir;
            if (j < 0 || j >= groups.Count) return;
            (groups[gi], groups[j]) = (groups[j], groups[gi]);
            Renumber(groups);
        }

        private void Renumber(List<List<MonitorItemConfig>> groups)
        {
            int idx = 0;
            foreach (var g in groups) foreach (var i in g) i.SortIndex = idx++;
            _cfg.MonitorItems = _cfg.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            ApplyItems(rebuildPage: true);
        }

        private void ApplyItems(bool rebuildPage)
        {
            _cfg.SyncToLanguage();   // custom names win over the built-in texts
            Save();
            _app.ApplyMonitorLayout();
            if (rebuildPage) _nav.Content = ItemsPage();
        }

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
                Ui.Section(T("Menu.AlertTemp")),
                Ui.Row(T("Menu.AlertTemp"), Ui.Toggle(c.AlertTempEnabled, v => { c.AlertTempEnabled = v; Save(); }),
                    "A notification when CPU, GPU or motherboard stay at or above the limit for 5 seconds (disks: 20 °C lower, at most 60 °C)."),
                Ui.Row(T("Menu.AlertThreshold"), Ui.NumberUnit(c.AlertTempThreshold, 30, 120, "°C", v => { c.AlertTempThreshold = (int)v; Save(); })),

                Ui.Section(T("Menu.AlertBattery")),
                Ui.Row(T("Menu.AlertBattery"), Ui.Toggle(c.AlertBatteryEnabled, v => { c.AlertBatteryEnabled = v; Save(); }),
                    "One notification when a wireless device drops to the limit; again after it charges. Rows turn red at the limit and yellow up to 10% above it."),
                Ui.Row(T("Menu.AlertThreshold"), Ui.NumberUnit(c.AlertBatteryThreshold, 0, 100, "%", v => { c.AlertBatteryThreshold = (int)v; Save(); })),

                Ui.Section(T("Menu.GeneralHardware")),
                Ui.Hint(T("Menu.ThresholdsTips")),
                Ui.Row(T("Menu.HardwareLoad"), Threshold(c.Thresholds.Load, "%")),
                Ui.Row(T("Menu.HardwareTemp"), Threshold(c.Thresholds.Temp, "°C")),

                Ui.Section(T("Menu.NetworkDiskSpeed")),
                Ui.Row(T("Menu.DiskIOSpeed"), Threshold(c.Thresholds.DiskIOMB, "MB/s")),
                Ui.Row(T("Menu.UploadSpeed"), Threshold(c.Thresholds.NetUpMB, "MB/s")),
                Ui.Row(T("Menu.DownloadSpeed"), Threshold(c.Thresholds.NetDownMB, "MB/s")),

                Ui.Section(T("Menu.DailyTraffic")),
                Ui.Row(T("Items.DATA.DayUp"), Threshold(c.Thresholds.DataUpMB, "MB")),
                Ui.Row(T("Items.DATA.DayDown"), Threshold(c.Thresholds.DataDownMB, "MB"))
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
                var cb = new ComboBox { Width = 320, PlaceholderText = auto };
                cb.Items.Add(auto);
                string cur = get();
                if (!string.IsNullOrEmpty(cur)) cb.Items.Add(cur);
                cb.SelectedIndex = string.IsNullOrEmpty(cur) ? 0 : 1;
                cb.Tag = set;
                return cb;
            }
            var disk = Source(() => c.PreferredDisk, v => c.PreferredDisk = v);
            var net = Source(() => c.PreferredNetwork, v => c.PreferredNetwork = v);
            var gpu = new ComboBox { Width = 320 };
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
                Ui.Section(T("Menu.HardwareSettings")),
                Ui.Row(T("Menu.UseWinPerCounters"), Ui.Toggle(c.UseWinPerCounters, v => { c.UseWinPerCounters = v; Save(); _app.ApplyMonitorLayout(); })),
                Ui.Row(T("Menu.IgnoreSMBTraffic"), Ui.Toggle(c.IgnoreSmbTraffic, v => { c.IgnoreSmbTraffic = v; Save(); _app.ApplyMonitorLayout(); })),
                Ui.Row(T("Menu.MemoryDisplayMode"), Ui.Combo(new[] { T("Menu.Percent"), T("Menu.UsedSize") }, c.MemoryDisplayMode,
                    i => { c.MemoryDisplayMode = i; Save(); _app.ApplyMonitorLayout(); })),
                Ui.Row(T("Menu.Refresh"), Ui.Combo(rates.Select(r => r + " ms"), Array.IndexOf(rates, c.RefreshMs),
                    i => { c.RefreshMs = rates[i]; Save(); _app.ApplyThemeAndLayout(retainData: true); })),
                Ui.Row(T("Menu.DiskSource"), disk),
                Ui.Row(T("Menu.NetworkSource"), net),
                Ui.Row(T("Menu.GpuSource"), gpu),
                Ui.Row(T("Items.MOBO.Temp"), mobo),
                Ui.Row(T("Items.CPU.Fan"), cpuFan),
                Ui.Row(T("Items.CPU.Pump"), pump),
                Ui.Row(T("Items.CASE.Fan"), caseFan),

                Ui.Section(T("Menu.Calibration")),
                Ui.Hint(T("Menu.CalibrationTip")),
                Ui.Row(Max("Items.CPU.Power"), Calib(c.RecordedMaxCpuPower, "W", v => c.RecordedMaxCpuPower = v)),
                Ui.Row(Max("Items.CPU.Clock"), Calib(c.RecordedMaxCpuClock, "MHz", v => c.RecordedMaxCpuClock = v)),
                Ui.Row(Max("Items.GPU.Power"), Calib(c.RecordedMaxGpuPower, "W", v => c.RecordedMaxGpuPower = v)),
                Ui.Row(Max("Items.GPU.Clock"), Calib(c.RecordedMaxGpuClock, "MHz", v => c.RecordedMaxGpuClock = v)),
                Ui.Row(Max("Items.CPU.Fan"), Calib(c.RecordedMaxCpuFan, "RPM", v => c.RecordedMaxCpuFan = v)),
                Ui.Row(Max("Items.CPU.Pump"), Calib(c.RecordedMaxCpuPump, "RPM", v => c.RecordedMaxCpuPump = v)),
                Ui.Row(Max("Items.GPU.Fan"), Calib(c.RecordedMaxGpuFan, "RPM", v => c.RecordedMaxGpuFan = v)),
                Ui.Row(Max("Items.CASE.Fan"), Calib(c.RecordedMaxChassisFan, "RPM", v => c.RecordedMaxChassisFan = v)),

                Ui.Section(T("Menu.SystemSettings")),
                Ui.Row(T("Menu.AutoStart"), Ui.Toggle(c.AutoStart, v => { c.AutoStart = v; Save(); _app.ApplyAutoStart(); }),
                    "Starts WinMonitor when you sign in (a scheduled task, so it can run as administrator without a prompt)."),
                Ui.Row(T("Menu.HideTrayIcon"), Ui.Toggle(c.HideTrayIcon, v => { c.HideTrayIcon = v; Save(); _app.ApplyVisibility(); }))
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
            var links = new StackPanel { Spacing = 2 };
            links.Children.Add(Ui.Link("Source code and releases", AppInfo.RepoUrl));
            links.Children.Add(Ui.Link("Report a problem", AppInfo.IssuesUrl));

            var credits = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text =
                    "WinMonitor is free and open-source software (MIT licence).\n\n" +
                    "Built on:\n" +
                    "• LiteMonitor by Diorser (MIT): the monitor this app is based on\n" +
                    "• Halo Battery (MIT): wireless device battery readers\n" +
                    "• LibreHardwareMonitor (MPL-2.0): sensor readings\n" +
                    "• Windows App SDK / WinUI 3 (MIT)\n" +
                    "• WinUIEx by Morten Nielsen (MIT): see-through window technique\n\n" +
                    "Optional download, from its official release only:\n" +
                    "• PawnIO driver (namazso): CPU temperature, clock and power\n\n" +
                    "Full licence texts: THIRD-PARTY-NOTICES.txt next to WinMonitor.exe.",
            };

            return Ui.Page(
                Ui.PageTitle(T("Menu.About")),
                Ui.Card(new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "⚡ WinMonitor", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                        new TextBlock { Text = "Version " + AppInfo.Version, Foreground = Ui.Res("TextFillColorSecondaryBrush") },
                        new TextBlock { Text = "Hardware monitor with wireless device batteries. Portable: settings are kept next to the app.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) },
                        links,
                    }
                }, new Thickness(16)),
                Ui.Section("Credits"),
                Ui.Card(credits, new Thickness(16)),
                Ui.Section(T("Menu.Reset")),
                Ui.Row(T("Menu.Reset"), Ui.Button(T("Menu.Reset") + "…", ResetSettings), "Deletes settings.json and restarts WinMonitor with the defaults.")
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
