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

            PopupMenu.UseDarkMenus(ThemeManager.IsDark(cfg.Skin));
            TrafficLogger.Load();
            HardwareHistoryLogger.Load();

            Panel = new PanelWindow(cfg);
            Controller = new PanelController(cfg, Panel);
            Controller.Notify += (title, text) => Notify(title, text, true);

            _tray = new TrayIcon(Panel.Hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
            _tray.MenuRequested += ShowMenu;
            _tray.DoubleClicked += () => { if (Panel.IsVisible) HidePanel(); else ShowPanel(); };
            Panel.Hook.Add(_tray.HandleMessage);
            Panel.Hook.Add((msg, w, l) =>
            {
                if (msg == WM_SHOWAPP) { ShowPanel(); return IntPtr.Zero; }
                if (msg == Native.WM_SETTINGCHANGE && Cfg.Skin == "System" && l != IntPtr.Zero
                    && System.Runtime.InteropServices.Marshal.PtrToStringUni(l) == "ImmersiveColorSet")
                    Panel.DispatcherQueue.TryEnqueue(ApplyAppTheme);   // Windows switched light / dark
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
                // keep Start with Windows pointing at this exe (the folder may have moved since it was set)
                if (Cfg.AutoStart) _ = Task.Run(() => AutoStart.Set(true));
                await Task.Delay(1500);
                await Controller.Monitor.SmartCheckDriver();
                await Task.Delay(8000);
                UpdateService.CleanUpOldVersion();
                if (Cfg.CheckForUpdates && DateTime.UtcNow - Cfg.LastUpdateCheckUtc > TimeSpan.FromDays(1))
                    await CheckForUpdateAsync(quiet: true);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        /// <summary>The newer release found by the last check, if any.</summary>
        public UpdateService.Release? AvailableUpdate { get; private set; }

        /// <summary>Checks GitHub. Quiet checks (startup) only speak up with a tray notification when there is a new version.</summary>
        public async Task<UpdateService.Release?> CheckForUpdateAsync(bool quiet)
        {
            try
            {
                AvailableUpdate = await UpdateService.CheckAsync();
                Cfg.LastUpdateCheckUtc = DateTime.UtcNow;
                Cfg.Save();
                if (AvailableUpdate != null && quiet)
                    Notify("WinMonitor " + AvailableUpdate.Version.ToString(3) + " is available", "Open Settings > About to install it.", false);
                return AvailableUpdate;
            }
            catch when (quiet) { return null; }
        }

        /// <summary>Downloads, verifies and swaps in the new WinMonitor.exe, then restarts into it.</summary>
        public async Task InstallUpdateAsync(UpdateService.Release r)
        {
            await Task.Run(() => UpdateService.InstallAsync(r));
            Restart();
        }

        public void Notify(string title, string text, bool warning) => _tray.Notify(title, text, warning);

        // ------------------------------------------------------------------ visibility
        public void ShowPanel()
        {
            Cfg.HideMainForm = false;
            Cfg.Save();
            Panel.ShowPanel(activate: false);
            Panel.ClampToScreen(true);
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

        public void ApplyThemeAndLayout(bool retainData = false)
        {
            Controller.ApplyTheme(retainData);
            Panel.ClampToScreen(false);
        }

        /// <summary>Light or dark everywhere: panel, menus and windows.</summary>
        public void ApplyAppTheme()
        {
            ApplyThemeAndLayout(retainData: true);
            bool dark = ThemeManager.IsDark(Cfg.Skin);
            PopupMenu.UseDarkMenus(dark);
            ThemeChanged?.Invoke(dark);
        }

        /// <summary>Raised with "dark?" when the look changes; open windows follow it.</summary>
        public event Action<bool>? ThemeChanged;

        public void ApplyMonitorLayout()
        {
            Controller.Monitor.RefreshHardwareConfig();
            Controller.RebuildLayout();
            _ = Controller.Monitor.SmartCheckDriver();   // e.g. CPU items switched on: offer the PawnIO driver
        }

        public void ApplyAutoStart() => AutoStart.Set(Cfg.AutoStart);

        public void ApplyAll()
        {
            ApplyWindowAttributes();
            ApplyThemeAndLayout();
            ApplyMonitorLayout();
            ApplyVisibility();
        }

        private void HandleDoubleClick()
        {
            switch (Cfg.MainFormDoubleClickAction)
            {
                case 1: SystemActions.OpenTaskManager(); break;
                case 5: break;
                default: OpenSettings(); break;
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
            if (_themeEditor == null) { _themeEditor = new ThemeEditorWindow(this); _themeEditor.Closed += (_, _) => { _themeEditor = null; ApplyAppTheme(); }; }
            _themeEditor.Activate();
        }

        // ------------------------------------------------------------------ menu
        // after the click or tray message has been handled, so the menu's own loop does not run inside it
        private void ShowMenu() => Panel.DispatcherQueue.TryEnqueue(() => PopupMenu.Show(Panel.Hwnd, BuildMenu()));

        private static string T(string key) => LanguageManager.T(key);

        public const string SystemThemeLabel = "Use system theme";

        public void SetTheme(string skin)
        {
            Cfg.Skin = skin;
            Cfg.Save();
            ApplyAppTheme();
        }

        private List<MenuEntry> BuildMenu()
        {
            var c = Cfg;
            var m = new List<MenuEntry>();

            // window behaviour and look
            var mode = new List<MenuEntry>
            {
                Toggle(T("Menu.TopMost"), c.TopMost, v => c.TopMost = v, ApplyWindowAttributes),
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
            m.Add(MenuEntry.Sub(T("Menu.MonitorItemDisplay"), BuildItemsMenu()));
            m.Add(MenuEntry.Sub(T("Menu.MainFormSettings"), mode));
            var themes = new List<MenuEntry>
            {
                MenuEntry.Item(SystemThemeLabel, () => SetTheme(ThemeManager.SystemMode), c.Skin == ThemeManager.SystemMode),
                MenuEntry.Separator,
            };
            foreach (var name in ThemeManager.GetAvailableThemes())
                themes.Add(MenuEntry.Item(name, () => SetTheme(name), name.Equals(c.Skin, StringComparison.OrdinalIgnoreCase)));
            themes.Add(MenuEntry.Separator);
            themes.Add(MenuEntry.Item(T("Menu.ThemeEditor"), OpenThemeEditor));
            m.Add(MenuEntry.Sub(T("Menu.Theme"), themes));
            m.Add(MenuEntry.Separator);

            m.Add(MenuEntry.Item(T("Menu.HardwareInfo"), OpenHardwareInfo));
            m.Add(MenuEntry.Separator);
            m.Add(MenuEntry.Item(T("Menu.MonitorHistory"), OpenTrend));
            m.Add(MenuEntry.Separator);
            m.Add(new MenuEntry { Text = T("Menu.SettingsPanel"), Click = () => OpenSettings(), Bold = true });
            m.Add(MenuEntry.Separator);
            m.Add(Toggle(T("Menu.AutoStart"), c.AutoStart, v => c.AutoStart = v, ApplyAutoStart));

            m.Add(MenuEntry.Item(T("Menu.About"), () => OpenSettings("About")));
            m.Add(MenuEntry.Separator);
            m.Add(MenuEntry.Item(T("Menu.Exit"), Exit));
            return m;
        }

        private MenuEntry Toggle(string text, bool value, Action<bool> set, Action? apply) =>
            MenuEntry.Item(text, () => { set(!value); Cfg.Save(); apply?.Invoke(); }, value);

        /// <summary>The "Monitor Items" submenu: one submenu per category, with its on/off switch first.</summary>
        private List<MenuEntry> BuildItemsMenu()
        {
            var list = new List<MenuEntry>();
            var groups = Cfg.MonitorItems.GroupBy(x => x.UIGroup).OrderBy(g => g.Min(x => x.SortIndex));
            foreach (var g in groups)
            {
                string key = g.Key;
                bool on = Cfg.IsGroupEnabled(key);
                var sub = new List<MenuEntry>
                {
                    MenuEntry.Item("Show this category", () => { SetGroupEnabled(key, !on); }, on),
                    MenuEntry.Separator,
                };
                foreach (var item in g.OrderBy(x => x.SortIndex))
                {
                    if (!SystemBattery.Present && SystemBattery.IsLaptopRow(item.Key)) continue;   // no laptop battery here
                    var it = item;
                    string label = ItemName(it);
                    sub.Add(new MenuEntry
                    {
                        Text = label,
                        Checked = it.VisibleInPanel,
                        Enabled = on,
                        Click = () =>
                        {
                            it.VisibleInPanel = !it.VisibleInPanel;
                            Cfg.Save();
                            ApplyMonitorLayout();
                            if (it.VisibleInPanel) RemindCalibration(it, label);
                        },
                    });
                }
                list.Add(new MenuEntry { Text = GroupName(key), Items = sub, Checked = on });
            }
            return list;
        }

        public string GroupName(string key) =>
            Cfg.GroupAliases.TryGetValue(key, out var alias) ? alias : T(UIUtils.Intern("Groups." + key));

        public static string ItemName(MonitorItemConfig item)
        {
            string label = MetricLabelResolver.ResolveLabel(item);
            if (string.IsNullOrWhiteSpace(label)) label = T(UIUtils.Intern("Items." + item.Key));
            return label.StartsWith("Items.") ? item.Key : label;
        }

        /// <summary>Switches a whole category on or off (its rows keep their own ticks).</summary>
        public void SetGroupEnabled(string group, bool on)
        {
            Cfg.DisabledGroups.Remove(group);
            if (!on) Cfg.DisabledGroups.Add(group);
            Cfg.Save();
            ApplyMonitorLayout();
        }

        /// <summary>First time a clock / power / fan item is switched on: offer to set its real maximum.</summary>
        private void RemindCalibration(MonitorItemConfig item, string name)
        {
            bool calibrated = (item.Key.Contains("Clock") || item.Key.Contains("Power") || item.Key.Contains("Fan") || item.Key.Contains("Pump"))
                              && !item.Key.Contains("BAT");
            if (!calibrated || Cfg.MaxLimitTipShown) return;
            Cfg.MaxLimitTipShown = true;
            Cfg.Save();
            _ = AskCalibration(name);
        }

        private async System.Threading.Tasks.Task AskCalibration(string name)
        {
            if (await Dialogs.AskAsync("Set its real maximum so the bar is accurate, or skip and WinMonitor learns it under high load (for example while gaming).",
                                       $"First use of {name}", null, "Set maximum", "Skip"))
                OpenSettings("Hardware");
        }

        // ------------------------------------------------------------------ lifetime
        public void Restart()
        {
            SaveAll();
            try { _tray.Dispose(); } catch { }
            SystemActions.RestartApplication();
        }

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _saveTimer, _layoutTimer;

        /// <summary>Writes settings.json shortly after the last change (one write for a burst of edits).</summary>
        public void SaveSettingsSoon()
        {
            if (_saveTimer == null)
            {
                _saveTimer = Panel.DispatcherQueue.CreateTimer();
                _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
                _saveTimer.IsRepeating = false;
                _saveTimer.Tick += (_, _) => Cfg.Save();
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Rebuilds the panel shortly after the last change (ticking several rows rebuilds once).</summary>
        public void ApplyMonitorLayoutSoon()
        {
            if (_layoutTimer == null)
            {
                _layoutTimer = Panel.DispatcherQueue.CreateTimer();
                _layoutTimer.Interval = TimeSpan.FromMilliseconds(150);
                _layoutTimer.IsRepeating = false;
                _layoutTimer.Tick += (_, _) => ApplyMonitorLayout();
            }
            _layoutTimer.Stop();
            _layoutTimer.Start();
        }

        /// <summary>Writes pending settings now (a settings window closed, or the app exits).</summary>
        public void FlushSettings()
        {
            if (_saveTimer?.IsRunning == true) { _saveTimer.Stop(); Cfg.Save(); }
            if (_layoutTimer?.IsRunning == true) { _layoutTimer.Stop(); ApplyMonitorLayout(); }
        }

        public void SaveAll()
        {
            try
            {
                _saveTimer?.Stop();
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
            Application.Current.Exit();
        }
    }
}
