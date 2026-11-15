using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WinMonitor.Core;
using WinMonitor.Services;

namespace WinMonitor.UI
{
    /// <summary>The running app: panel, tray icon, menus, and applying setting changes.</summary>
    internal sealed class AppHost
    {
        public static AppHost Current { get; private set; } = null!;

        public Settings Cfg { get; }
        public PanelWindow Panel { get; }
        public PanelController Controller { get; }
        private readonly TrayIcon _tray;
        private bool _exiting;

        /// <summary>Broadcast by a second launch to bring the panel back.</summary>
        public static readonly uint WM_SHOWAPP = Native.RegisterWindowMessage("WinMonitor.ShowPanel");

        public AppHost(Settings cfg)
        {
            Current = this;
            Cfg = cfg;

            PopupMenu.UseDarkMenus();
            TrafficLogger.Load();
            HardwareHistoryLogger.Load();

            Panel = new PanelWindow(cfg);
            Dialogs.Owner = Panel.Hwnd;
            Controller = new PanelController(cfg, Panel);
            Controller.Notify += (title, text) => Notify(title, text, true);

            _tray = new TrayIcon(Panel.Hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
            _tray.MenuRequested += ShowMenu;
            _tray.DoubleClicked += () => { if (Panel.IsVisible) HidePanel(); else ShowPanel(); };
            Panel.Hook.Add(_tray.HandleMessage);
            Panel.Hook.Add((msg, w, l) =>
            {
                if (msg == WM_SHOWAPP) { ShowPanel(); return IntPtr.Zero; }
                return null;
            });
            Panel.MenuRequested += ShowMenu;
            Panel.DoubleClicked += HandleDoubleClick;
            Panel.AppWindow.Closing += (_, e) => { if (_exiting) return; e.Cancel = true; Exit(); };   // Alt+F4 on the panel quits, like LiteMonitor

            if (cfg.HideMainForm && cfg.HideTrayIcon) cfg.HideTrayIcon = false;   // never lose the app
            _tray.Visible = !cfg.HideTrayIcon;

            // first show: render, place, then show (or stay hidden)
            Panel.ShowPanel(activate: true);
            Panel.RestorePosition();
            Panel.ApplyAttributes();
            if (cfg.HideMainForm) Panel.HidePanel();

            // --open settings[:Page] | history | hardware | themes  (shortcuts; also used by the automated tests)
            var args = Environment.GetCommandLineArgs();
            int oi = Array.IndexOf(args, "--open");
            if (oi >= 0 && oi + 1 < args.Length)
            {
                string what = args[oi + 1];
                Panel.DispatcherQueue.TryEnqueue(() => Open(what));
            }

            _ = StartupChecksAsync();
        }

        private void Open(string what)
        {
            var parts = what.Split(':', 2);
            switch (parts[0].ToLowerInvariant())
            {
                case "settings": OpenSettings(parts.Length > 1 ? parts[1] : null); break;
                case "history": OpenTrend(); break;
                case "hardware": OpenHardwareInfo(); break;
                case "themes": OpenThemeEditor(); break;
            }
        }

        private async Task StartupChecksAsync()
        {
            try
            {
                await Task.Delay(1500);
                await Controller.Monitor.SmartCheckDriver();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public void Notify(string title, string text, bool warning) => _tray.Notify(title, text, warning);

        // ------------------------------------------------------------------ visibility
        public void ShowPanel()
        {
            Cfg.HideMainForm = false;
            Cfg.Save();
            Panel.ShowPanel(activate: false);
            Panel.ForceShow();
        }

        public void HidePanel()
        {
            Cfg.HideMainForm = true;
            Cfg.Save();
            Panel.HidePanel();
            if (Cfg.HideTrayIcon) { Cfg.HideTrayIcon = false; _tray.Visible = true; Cfg.Save(); }
        }

        // ------------------------------------------------------------------ applying settings
        public void ApplyWindowAttributes() => Panel.ApplyAttributes();

        public void ApplyVisibility()
        {
            if (Cfg.HideMainForm && Cfg.HideTrayIcon) { Cfg.HideTrayIcon = false; Cfg.Save(); }   // never lose the app
            _tray.Visible = !Cfg.HideTrayIcon;
            if (Cfg.HideMainForm) Panel.HidePanel();
            else if (!Panel.IsVisible) Panel.ShowPanel(activate: false);
        }

        public void ApplyThemeAndLayout(bool? wasHorizontal = null, bool retainData = false)
        {
            bool modeChanged = (wasHorizontal ?? Cfg.HorizontalMode) != Cfg.HorizontalMode;
            var center = modeChanged && Panel.IsVisible ? Panel.Center : ((int, int)?)null;
            Controller.ApplyTheme(retainData && !modeChanged);
            if (center.HasValue) Panel.CenterOn(center.Value);
            Panel.ClampToScreen(false);
        }

        public void ApplyMonitorLayout()
        {
            Controller.Monitor.RefreshHardwareConfig();
            Controller.RebuildLayout();
            _ = Controller.Monitor.SmartCheckDriver();   // e.g. FPS switched on: offer PresentMon
        }

        public void ApplyAutoStart() => AutoStart.Set(Cfg.AutoStart);

        public void ApplyAll()
        {
            ApplyWindowAttributes();
            ApplyThemeAndLayout();
            ApplyMonitorLayout();
            ApplyVisibility();
        }

        private void ToggleLayout()
        {
            bool old = Cfg.HorizontalMode;
            Cfg.HorizontalMode = !old;
            Cfg.Save();
            ApplyThemeAndLayout(old);
        }

        private void HandleDoubleClick()
        {
            switch (Cfg.MainFormDoubleClickAction)
            {
                case 1: SystemActions.OpenTaskManager(); break;
                case 2: OpenSettings(); break;
                case 4: CleanMemory(); break;
                default: ToggleLayout(); break;
            }
        }

        // ------------------------------------------------------------------ windows
        private SettingsWindow? _settings;
        private TrendWindow? _trend;
        private HardwareInfoWindow? _info;
        private ThemeEditorWindow? _themeEditor;

        public void OpenSettings(string? page = null)
        {
            if (_settings == null)
            {
                _settings = new SettingsWindow(this);
                _settings.Closed += (_, _) => _settings = null;
            }
            if (page != null) _settings.ShowPage(page);
            _settings.Activate();
        }

        public void OpenTrend()
        {
            if (_trend == null) { _trend = new TrendWindow(Cfg); _trend.Closed += (_, _) => _trend = null; }
            _trend.Activate();
        }

        public void OpenHardwareInfo()
        {
            if (_info == null) { _info = new HardwareInfoWindow(); _info.Closed += (_, _) => _info = null; }
            _info.Activate();
        }

        public void OpenThemeEditor()
        {
            if (_themeEditor == null) { _themeEditor = new ThemeEditorWindow(this); _themeEditor.Closed += (_, _) => _themeEditor = null; }
            _themeEditor.Activate();
        }

        public void CleanMemory() => CleanMemoryWindow.Run(Panel);

        // ------------------------------------------------------------------ menu
        // after the click or tray message has been handled, so the menu's own loop does not run inside it
        private void ShowMenu() => Panel.DispatcherQueue.TryEnqueue(() => PopupMenu.Show(Panel.Hwnd, BuildMenu()));

        private static string T(string key) => LanguageManager.T(key);

        private List<MenuEntry> BuildMenu()
        {
            var c = Cfg;
            var m = new List<MenuEntry>
            {
                MenuEntry.Item(T("Menu.CleanMemory"), CleanMemory),
                MenuEntry.Separator,
            };

            // display mode & window behaviour
            var mode = new List<MenuEntry>
            {
                MenuEntry.Item(T("Menu.Vertical"), () => SetMode(false), !c.HorizontalMode),
                MenuEntry.Item(T("Menu.Horizontal"), () => SetMode(true), c.HorizontalMode),
                MenuEntry.Separator,
                Toggle(T("Menu.TopMost"), c.TopMost, v => c.TopMost = v, ApplyWindowAttributes),
                Toggle(T("Menu.AutoHide"), c.AutoHide, v => c.AutoHide = v, ApplyWindowAttributes),
                Toggle(T("Menu.ClampToScreen"), c.ClampToScreen, v => c.ClampToScreen = v, null),
                Toggle(T("Menu.ClickThrough"), c.ClickThrough, v => c.ClickThrough = v, ApplyWindowAttributes),
                MenuEntry.Separator,
                MenuEntry.Sub(T("Menu.Opacity"), new[] { 1.0, 0.95, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5, 0.4, 0.3 }
                    .Select(o => MenuEntry.Item($"{o * 100:0}%", () => { c.Opacity = o; c.Save(); ApplyWindowAttributes(); }, Math.Abs(c.Opacity - o) < 0.01)).ToList()),
                MenuEntry.Sub(T("Menu.Width"), new[] { 180, 200, 220, 240, 260, 280, 300, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1080, 1140, 1200 }
                    .Select(w => MenuEntry.Item($"{w}px", () => { c.PanelWidth = w; c.Save(); ApplyThemeAndLayout(retainData: true); }, c.PanelWidth == w)).ToList()),
                MenuEntry.Sub(T("Menu.Scale"), new[] { 2.0, 1.75, 1.5, 1.25, 1.0, 0.9, 0.85, 0.8, 0.75, 0.7, 0.6, 0.5 }
                    .Select(s => MenuEntry.Item($"{s * 100:0}%", () => { c.UIScale = s; c.Save(); ApplyThemeAndLayout(retainData: true); }, Math.Abs(c.UIScale - s) < 0.01)).ToList()),
                MenuEntry.Separator,
                Toggle(T("Menu.HideMainForm"), c.HideMainForm, v => c.HideMainForm = v, ApplyVisibility),
                Toggle(T("Menu.HideTrayIcon"), c.HideTrayIcon, v => c.HideTrayIcon = v, ApplyVisibility),
            };
            m.Add(MenuEntry.Sub(T("Menu.DisplayMode"), mode));
            m.Add(MenuEntry.Sub(T("Menu.MonitorItemDisplay"), BuildItemsMenu()));

            // themes
            var themes = new List<MenuEntry> { MenuEntry.Item(T("Menu.ThemeEditor"), OpenThemeEditor), MenuEntry.Separator };
            foreach (var name in ThemeManager.GetAvailableThemes())
                themes.Add(MenuEntry.Item(name, () => { c.Skin = name; c.Save(); ApplyThemeAndLayout(); }, name.Equals(c.Skin, StringComparison.OrdinalIgnoreCase)));
            m.Add(MenuEntry.Sub(T("Menu.Theme"), themes));
            m.Add(MenuEntry.Separator);

            m.Add(MenuEntry.Item(T("Menu.HardwareInfo"), OpenHardwareInfo));
            m.Add(MenuEntry.Separator);
            m.Add(MenuEntry.Item(T("Menu.MonitorHistory"), OpenTrend));
            m.Add(MenuEntry.Separator);
            m.Add(new MenuEntry { Text = T("Menu.SettingsPanel"), Click = () => OpenSettings(), Bold = true });
            m.Add(MenuEntry.Separator);
            m.Add(Toggle(T("Menu.AutoStart"), c.AutoStart, v => c.AutoStart = v, ApplyAutoStart));

            // more
            var shutdown = new List<MenuEntry>();
            foreach (var min in new[] { 5, 10, 15, 30, 45 })
                shutdown.Add(MenuEntry.Item($"{min} {T("Menu.MinutesLater")}", () => SystemActions.ScheduleShutdown(min * 60)));
            foreach (var h in new[] { 1, 2, 3, 4, 5, 6, 8, 10, 12, 24 })
                shutdown.Add(MenuEntry.Item($"{h} {T("Menu.HoursLater")}", () => SystemActions.ScheduleShutdown(h * 3600)));
            shutdown.Add(MenuEntry.Separator);
            shutdown.Add(MenuEntry.Item(T("Menu.CancelShutdown"), () => SystemActions.ScheduleShutdown(0)));

            m.Add(MenuEntry.Sub(T("Menu.More"), new List<MenuEntry>
            {
                MenuEntry.Item(T("Menu.ActionTaskMgr"), SystemActions.OpenTaskManager),
                MenuEntry.Item(T("Menu.RestartExplorer"), SystemActions.RestartExplorer),
                MenuEntry.Separator,
                MenuEntry.Item(T("Menu.RefreshIcons"), SystemActions.RefreshIconCache),
                MenuEntry.Item(T("Menu.CleanTemp"), () => _ = SystemActions.CleanTempFilesAsync()),
                MenuEntry.Separator,
                MenuEntry.Item(T("Menu.PreventSleep"), SystemActions.TogglePreventSleep, SystemActions.IsPreventSleep),
                MenuEntry.Item(T("Menu.TurnOffMonitor"), () => SystemActions.TurnOffMonitor(Panel.Hwnd)),
                MenuEntry.Sub(T("Menu.ScheduledShutdown"), shutdown),
                MenuEntry.Separator,
                MenuEntry.Item(T("Menu.Feedback"), () => SystemActions.OpenUrl(AppInfo.IssuesUrl)),
                MenuEntry.Item(T("Menu.About"), () => OpenSettings("About")),
                MenuEntry.Separator,
                MenuEntry.Item(T("Menu.RestartApp"), Restart),
            }));
            m.Add(MenuEntry.Separator);
            m.Add(MenuEntry.Item(T("Menu.Exit"), Exit));
            return m;
        }

        private MenuEntry Toggle(string text, bool value, Action<bool> set, Action? apply) =>
            MenuEntry.Item(text, () => { set(!value); Cfg.Save(); apply?.Invoke(); }, value);

        private void SetMode(bool horizontal)
        {
            bool old = Cfg.HorizontalMode;
            Cfg.HorizontalMode = horizontal;
            Cfg.Save();
            ApplyThemeAndLayout(old);
        }

        /// <summary>The "Monitor Items" submenu: one entry per item; network, disk and traffic switch as a group.</summary>
        private List<MenuEntry> BuildItemsMenu()
        {
            var list = new List<MenuEntry>();
            var groups = Cfg.MonitorItems.OrderBy(x => x.SortIndex).GroupBy(x => x.UIGroup);
            var groupToggles = new HashSet<string> { "DISK", "NET", "DATA" };

            MenuEntry ItemEntry(MonitorItemConfig item)
            {
                string label = MetricLabelResolver.ResolveLabel(item);
                if (string.IsNullOrEmpty(label))
                {
                    label = T(UIUtils.Intern("Items." + item.Key));
                    if (label.StartsWith("Items.")) label = item.Key;
                }
                return MenuEntry.Item(label, () =>
                {
                    item.VisibleInPanel = !item.VisibleInPanel;
                    Cfg.Save();
                    ApplyMonitorLayout();
                    if (item.VisibleInPanel) RemindCalibration(item, label);
                }, item.VisibleInPanel);
            }

            foreach (var g in groups)
            {
                string name = Cfg.GroupAliases.TryGetValue(g.Key, out var alias) ? alias : T(UIUtils.Intern("Groups." + g.Key));
                if (g.Key == "BAT")
                    list.Add(MenuEntry.Sub(name, g.Select(ItemEntry).ToList()));
                else if (groupToggles.Contains(g.Key))
                {
                    bool any = g.Any(x => x.VisibleInPanel);
                    var items = g.ToList();
                    list.Add(MenuEntry.Item(name, () =>
                    {
                        foreach (var i in items) i.VisibleInPanel = !any;
                        Cfg.Save();
                        ApplyMonitorLayout();
                    }, any));
                }
                else
                {
                    list.Add(MenuEntry.Header(name));
                    list.AddRange(g.Select(ItemEntry));
                }
                list.Add(MenuEntry.Separator);
            }
            if (list.Count > 0 && list[^1].IsSeparator) list.RemoveAt(list.Count - 1);
            return list;
        }

        /// <summary>First time a clock / power / fan item is switched on: offer to set its real maximum.</summary>
        private void RemindCalibration(MonitorItemConfig item, string name)
        {
            bool calibrated = (item.Key.Contains("Clock") || item.Key.Contains("Power") || item.Key.Contains("Fan") || item.Key.Contains("Pump"))
                              && !item.Key.Contains("BAT");
            if (!calibrated || Cfg.MaxLimitTipShown) return;
            Cfg.MaxLimitTipShown = true;
            Cfg.Save();
            if (Dialogs.YesNo($"First use of {name}.\n\nSet the real maximum value so the bar is accurate?\n\n" +
                              "Choose No to skip: WinMonitor then learns the maximum under high load (e.g. gaming).", "WinMonitor"))
                OpenSettings("Hardware");
        }

        // ------------------------------------------------------------------ lifetime
        public void Restart()
        {
            SaveAll();
            try { _tray.Dispose(); } catch { }
            try { FpsCounter.ForceKillZombies(); } catch { }
            SystemActions.RestartApplication();
        }

        public void SaveAll()
        {
            try
            {
                Panel.SavePosition();
                Cfg.Save();
                TrafficLogger.Save();
                HardwareHistoryLogger.Save();
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        public void Exit()
        {
            if (_exiting) return;
            _exiting = true;
            SaveAll();
            try { _tray.Dispose(); } catch { }
            try { Controller.Dispose(); } catch { }
            try { FpsCounter.ForceKillZombies(); } catch { }
            Application.Current.Exit();
        }
    }
}
