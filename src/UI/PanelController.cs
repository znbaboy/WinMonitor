using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using WinMonitor.Core;
using WinMonitor.Services;

namespace WinMonitor.UI
{
    /// <summary>
    /// Feeds the panel: reads the sensors, the wireless device batteries and the dashboard values,
    /// and raises the temperature and battery alerts. Ported from LiteMonitor's UIController.
    /// </summary>
    internal sealed class PanelController : IDisposable
    {
        private readonly Settings _cfg;
        private readonly PanelWindow _window;
        private readonly HardwareMonitor _mon;
        private readonly DispatcherQueueTimer _timer;

        private List<GroupLayoutInfo> _groups = new();
        private bool _layoutDirty = true;
        private bool _busy;
        private volatile bool _sensorReading;

        private Dictionary<string, HaloDevice> _haloMap = new();
        private readonly HashSet<string> _batteryAlerted = new();
        private readonly Dictionary<string, DateTime> _overheatStart = new();

        /// <summary>Raised for alerts: title, text.</summary>
        public event Action<string, string>? Notify;

        public HardwareMonitor Monitor => _mon;

        public PanelController(Settings cfg, PanelWindow window)
        {
            _cfg = cfg;
            _window = window;
            _mon = new HardwareMonitor(cfg);

            // the battery poller follows the live settings, so a change applies within 2 s
            Services.Halo.HaloEngine.IntervalProvider = () => Settings.Load().HaloPollSeconds;
            Services.Halo.PlayStationProvider.SwitchBluetooth = () => Settings.Load().HaloPsBluetoothFullMode;

            _timer = window.DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(80, cfg.RefreshMs));
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            window.DpiChanged += () => ApplyTheme(true);
            ApplyTheme(false);
        }

        // ------------------------------------------------------------------ theme & layout
        public void ApplyTheme(bool retainData)
        {
            var t = ThemeManager.Load(_cfg.Skin);
            t.Scale(1f, (float)_cfg.UIScale);          // WinUI works in DIPs: only the user's scale here
            t.Layout.Width = (int)(_cfg.PanelWidth * _cfg.UIScale);
            if (!retainData || _groups.Count == 0) BuildMetrics();
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(80, _cfg.RefreshMs));
            _layoutDirty = true;
            Render();
        }

        /// <summary>Shows a theme object straight away (the theme editor's live preview).</summary>
        public void PreviewTheme(Theme edited)
        {
            var theme = ThemeManager.Clone(edited);
            ThemeManager.SetCurrent(theme);
            theme.Scale(1f, (float)_cfg.UIScale);
            theme.Layout.Width = (int)(_cfg.PanelWidth * _cfg.UIScale);
            _layoutDirty = true;
            Render();
        }

        public void RebuildLayout()
        {
            BuildMetrics();
            _layoutDirty = true;
            Render();
        }

        private void Render()
        {
            if (!_layoutDirty) { _window.View.Update(); return; }
            _layoutDirty = false;
            string title = LanguageManager.T("Title");
            if (title == "Title") title = "";
            var size = _window.View.BuildVertical(_groups, _cfg.ShowTitle, title);
            _window.SetContentSize(size.Width, size.Height);
        }

        // ------------------------------------------------------------------ tick
        private void Tick()
        {
            if (_window.IsDragging || _busy) return;
            _busy = true;
            try
            {
                bool fresh = !_sensorReading;   // the last background read has finished: show it
                _haloMap = HaloBatteryService.ByType();

                void UpdateItem(MetricItem? it)
                {
                    if (it == null) return;
                    if (it.Key.StartsWith("HALO.")) { ApplyHalo(it); return; }
                    if (it.Key.StartsWith("DASH.")) { it.TextValue = InfoService.Instance.GetValue(it.Key.Substring(5)); return; }
                    if (fresh) it.Value = _mon.Get(it.Key);
                    it.TickSmooth(_cfg.AnimationSpeed);
                }
                foreach (var g in _groups) foreach (var it in g.Items) UpdateItem(it);

                if (fresh)
                {
                    HardwareHistoryLogger.RecordSnapshot(_cfg, key => _mon.Get(key));
                    CheckTemperatureAlert();
                    _sensorReading = true;
                    Task.Run(() =>
                    {
                        try { _mon.UpdateAll(); }
                        catch { }
                        finally { _sensorReading = false; }
                    });
                }
                CheckBatteryAlert();
                InfoService.Instance.Update();

                Render();
            }
            catch (Exception ex) { Program.Log(ex); }
            finally { _busy = false; }
        }

        // ------------------------------------------------------------------ metrics
        private IEnumerable<MonitorItemConfig> VisibleItems() => _cfg.MonitorItems
            .Where(_cfg.IsShown)
            .GroupBy(x => x.UIGroup)                  // keep each group together
            .OrderBy(g => g.Min(x => x.SortIndex))    // groups in the user's order
            .SelectMany(g => g.OrderBy(x => x.SortIndex));

        private string GroupLabel(string key) =>
            _cfg.GroupAliases.TryGetValue(key, out var alias) ? alias : LanguageManager.T(UIUtils.Intern("Groups." + key));

        private void BuildMetrics()
        {
            _groups = new List<GroupLayoutInfo>();
            string current = "";
            var list = new List<MetricItem>();
            foreach (var c in VisibleItems())
            {
                if (c.UIGroup != current && list.Count > 0)
                {
                    _groups.Add(new GroupLayoutInfo(current, list) { Label = GroupLabel(current) });
                    list = new List<MetricItem>();
                }
                current = c.UIGroup;
                list.Add(CreateMetric(c));
            }
            if (list.Count > 0) _groups.Add(new GroupLayoutInfo(current, list) { Label = GroupLabel(current) });
        }

        private MetricItem CreateMetric(MonitorItemConfig cfg)
        {
            var item = new MetricItem { Key = cfg.Key, BoundConfig = cfg };
            item.Label = LanguageManager.T(UIUtils.Intern("Items." + cfg.Key));       // a rename (UserLabel) wins over this
            item.ShortLabel = LanguageManager.T(UIUtils.Intern("Short." + cfg.Key));
            if (cfg.Key.StartsWith("HALO.")) ApplyHalo(item);
            else if (cfg.Key.StartsWith("DASH."))
            {
                string dashKey = cfg.Key.Substring(5);
                if (dashKey == "IP")
                {
                    string ip = _mon.GetNetworkIP();
                    if (!string.IsNullOrEmpty(ip) && ip != "?") InfoService.Instance.InjectIP(ip);
                }
                item.TextValue = InfoService.Instance.GetValue(dashKey);
                item.Value = null;
            }
            else
            {
                float? v = _mon.Get(item.Key);
                item.Value = v;
                if (v.HasValue) item.DisplayValue = v.Value;
            }
            return item;
        }

        // ------------------------------------------------------------------ Halo battery rows
        private static string HaloText(HaloDevice d) => (d.Approx ? "~" : "") + d.Level + "%" + (d.Charging ? " ⚡" : "");

        // red at or below the alert level, yellow within 10% above it
        private int HaloState(HaloDevice d)
        {
            int low = _cfg.AlertBatteryThreshold;
            return d.Charging ? 0 : d.Level <= low ? 2 : d.Level <= low + 10 ? 1 : 0;
        }

        private void ApplyHalo(MetricItem it)
        {
            string type = it.Key.Substring(5);
            string label = LanguageManager.T(UIUtils.Intern("Items." + it.Key));
            if (_haloMap.TryGetValue(type, out var d)) it.SetDirect(label, HaloText(d), HaloState(d), d.Level / 100.0);
            else it.SetDirect(label, "—", 0, 0);
        }

        // ------------------------------------------------------------------ alerts
        /// <summary>One notification each time a device drops to the alert level; it can alert again
        /// once it charges or climbs 5% above it.</summary>
        private void CheckBatteryAlert()
        {
            if (!_cfg.AlertBatteryEnabled) { _batteryAlerted.Clear(); return; }
            int low = _cfg.AlertBatteryThreshold;
            var lines = new List<string>();
            foreach (var d in HaloBatteryService.Connected())
            {
                if (d.Charging || d.Level > low + 5) { _batteryAlerted.Remove(d.Key); continue; }
                if (d.Level > low || _batteryAlerted.Contains(d.Key)) continue;
                _batteryAlerted.Add(d.Key);
                var row = _cfg.MonitorItems.FirstOrDefault(x => x.Key == "HALO." + d.Type);
                string name = !string.IsNullOrWhiteSpace(row?.UserLabel) ? row!.UserLabel : d.Name;
                lines.Add($"{name}: \U0001F50B{(d.Approx ? "~" : "")}{d.Level}%");
            }
            if (lines.Count == 0) return;
            Notify?.Invoke($"{LanguageManager.T("Menu.AlertBattery")} (≤{low}%)", string.Join("\n", lines));
        }

        private void CheckTemperatureAlert()
        {
            if (!_cfg.AlertTempEnabled) { _overheatStart.Clear(); return; }
            if ((DateTime.Now - _cfg.LastAlertTime).TotalMinutes < 3) return;

            int limit = _cfg.AlertTempThreshold;
            int diskLimit = _cfg.AlertDiskTempThreshold;
            string title = LanguageManager.T("Menu.AlertTemp");
            var lines = new List<string>();

            void Check(string key, int threshold, string suffix = "")
            {
                float? val = _mon.Get(key);
                if (val.HasValue && val.Value >= threshold)
                {
                    if (!_overheatStart.ContainsKey(key)) _overheatStart[key] = DateTime.Now;
                    if ((DateTime.Now - _overheatStart[key]).TotalSeconds >= 5)   // hot for 5 s, not a spike
                        lines.Add($"{LanguageManager.T("Short." + key)}: \U0001F525{val:F0}°C (limit {threshold}°C)");
                }
                else _overheatStart.Remove(key);
            }
            Check("CPU.Temp", limit);
            Check("GPU.Temp", limit);
            Check("MOBO.Temp", limit);
            Check("DISK.Temp", diskLimit);

            if (lines.Count == 0) return;
            Notify?.Invoke(title, string.Join("\n", lines));
            _cfg.LastAlertTime = DateTime.Now;
        }

        public void Dispose()
        {
            _timer.Stop();
            _mon.Dispose();
        }
    }
}
