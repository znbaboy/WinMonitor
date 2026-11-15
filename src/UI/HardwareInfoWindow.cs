using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMonitor.Core;
using WinMonitor.Services;
using Windows.ApplicationModel.DataTransfer;

namespace WinMonitor.UI
{
    /// <summary>Every sensor LibreHardwareMonitor sees, with live value / min / max, a search box and copy.</summary>
    internal sealed class HardwareInfoWindow : ToolWindow
    {
        private readonly StackPanel _list = new() { Spacing = 6, Margin = new Thickness(16, 0, 16, 16) };
        private readonly TextBox _search = new() { PlaceholderText = "Search sensors", Width = 320 };
        private readonly List<(ISensor Sensor, TextBlock Value, TextBlock Min, TextBlock Max)> _rows = new();
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

        public HardwareInfoWindow() : base(LanguageManager.T("Menu.HardwareInfo") + " - WinMonitor", 900, 760)
        {
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(16, 0, 16, 8) };
            top.Children.Add(_search);
            top.Children.Add(Ui.Button("Expand all", () => SetExpanded(true)));
            top.Children.Add(Ui.Button("Collapse all", () => SetExpanded(false)));
            _search.TextChanged += (_, _) => Rebuild();

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(top);
            var scroll = new ScrollViewer { Content = _list };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            SetBody(root);

            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => RefreshValues();
            _timer.Start();
            Closed += (_, _) => _timer.Stop();
            Rebuild();
        }

        private static string TypeName(SensorType t) => t switch
        {
            SensorType.Temperature => "🌡️ Temperature",
            SensorType.Load => "⌛ Load",
            SensorType.Fan => "🌀 Fan",
            SensorType.Power => "⚡ Power",
            SensorType.Clock => "⏱️ Clock",
            SensorType.Control => "🎛️ Control",
            SensorType.Voltage => "🔋 Voltage",
            SensorType.Current => "🔌 Current",
            SensorType.Data => "📈 Data",
            SensorType.SmallData => "📶 Small data",
            SensorType.Throughput => "🚀 Throughput",
            SensorType.Level => "📉 Level",
            SensorType.Factor => "🔢 Factor",
            SensorType.Timing => "⏱️ Timing",
            SensorType.Frequency => "〰️ Frequency",
            SensorType.Energy => "🔋 Energy",
            SensorType.Noise => "🔊 Noise",
            _ => t.ToString(),
        };

        private static string Unit(SensorType t) => t switch
        {
            SensorType.Temperature => " °C",
            SensorType.Load or SensorType.Control or SensorType.Level => " %",
            SensorType.Fan => " RPM",
            SensorType.Power => " W",
            SensorType.Clock => " MHz",
            SensorType.Voltage => " V",
            SensorType.Current => " A",
            SensorType.Data => " GB",
            SensorType.SmallData => " MB",
            SensorType.Throughput => " B/s",
            SensorType.Frequency => " Hz",
            SensorType.Energy => " mWh",
            SensorType.Noise => " dBA",
            SensorType.Timing => " ns",
            _ => "",
        };

        private static string Fmt(float? v, SensorType t)
        {
            if (!v.HasValue) return "—";
            if (t == SensorType.Throughput) return MetricUtils.FormatDataSize(v.Value, "/s");
            return (Math.Abs(v.Value) >= 100 ? v.Value.ToString("0") : v.Value.ToString("0.##")) + Unit(t);
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            _rows.Clear();
            var computer = HardwareMonitor.Instance?.ComputerInstance;
            if (computer == null || computer.Hardware.Count == 0)
            {
                _list.Children.Add(new TextBlock { Text = "No hardware found yet. WinMonitor needs administrator rights to read sensors.", TextWrapping = TextWrapping.Wrap });
                return;
            }
            string filter = _search.Text.Trim();
            bool first = true;
            foreach (var hw in computer.Hardware)
            {
                var e = HardwareNode(hw, filter, first || filter.Length > 0);
                if (e != null) { _list.Children.Add(e); first = false; }
            }
            if (_list.Children.Count == 0) _list.Children.Add(new TextBlock { Text = "No sensor matches the search.", Opacity = 0.7 });
        }

        private Expander? HardwareNode(IHardware hw, string filter, bool expanded)
        {
            bool hwMatch = filter.Length == 0 || hw.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var body = new StackPanel { Spacing = 2 };

            foreach (var group in hw.Sensors.GroupBy(s => s.SensorType).OrderBy(g => g.Key))
            {
                var sensors = group.Where(s => hwMatch || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                     || s.Identifier.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (sensors.Count == 0) continue;
                body.Children.Add(new TextBlock { Text = TypeName(group.Key), FontWeight = Ui.Bold, Margin = new Thickness(0, 8, 0, 2) });
                body.Children.Add(Header());
                foreach (var s in sensors) body.Children.Add(SensorRow(s));
            }
            foreach (var sub in hw.SubHardware)
            {
                var child = HardwareNode(sub, filter, expanded);
                if (child != null) body.Children.Add(child);
            }
            if (body.Children.Count == 0) return null;

            var exp = new Expander
            {
                Header = new TextBlock { Text = $"{hw.Name}   ·   {hw.HardwareType}", FontWeight = Ui.Bold },
                Content = body,
                IsExpanded = expanded,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var menu = new MenuFlyout();
            menu.Items.Add(CopyItem("Copy name", () => hw.Name));
            menu.Items.Add(CopyItem("Copy ID", () => hw.Identifier.ToString()));
            ((FrameworkElement)exp.Header).ContextFlyout = menu;
            return exp;
        }

        private static Grid Columns(params FrameworkElement[] cells)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            for (int i = 1; i < cells.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < cells.Length; i++) { Grid.SetColumn(cells[i], i); g.Children.Add(cells[i]); }
            return g;
        }

        private static Grid Header()
        {
            TextBlock H(string t) => new() { Text = t, Foreground = Ui.Res("TextFillColorSecondaryBrush"), FontSize = 12 };
            return Columns(H("Sensor"), H("Value"), H("Min"), H("Max"));
        }

        private Grid SensorRow(ISensor s)
        {
            var name = new TextBlock { Text = s.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            var val = new TextBlock { Text = Fmt(s.Value, s.SensorType) };
            var min = new TextBlock { Text = Fmt(s.Min, s.SensorType), Opacity = 0.75 };
            var max = new TextBlock { Text = Fmt(s.Max, s.SensorType), Opacity = 0.75 };
            ToolTipService.SetToolTip(name, s.Identifier.ToString());
            var row = Columns(name, val, min, max);
            row.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);   // whole row right-clickable
            var menu = new MenuFlyout();
            menu.Items.Add(CopyItem("Copy name", () => s.Name));
            menu.Items.Add(CopyItem("Copy ID", () => s.Identifier.ToString()));
            menu.Items.Add(CopyItem("Copy value", () => Fmt(s.Value, s.SensorType)));
            row.ContextFlyout = menu;
            _rows.Add((s, val, min, max));
            return row;
        }

        private static MenuFlyoutItem CopyItem(string text, Func<string> value)
        {
            var i = new MenuFlyoutItem { Text = text };
            i.Click += (_, _) =>
            {
                var dp = new DataPackage();
                dp.SetText(value());
                Clipboard.SetContent(dp);
            };
            return i;
        }

        private void RefreshValues()
        {
            foreach (var (s, v, mn, mx) in _rows)
            {
                string a = Fmt(s.Value, s.SensorType), b = Fmt(s.Min, s.SensorType), c = Fmt(s.Max, s.SensorType);
                if (v.Text != a) v.Text = a;
                if (mn.Text != b) mn.Text = b;
                if (mx.Text != c) mx.Text = c;
            }
        }

        private void SetExpanded(bool on)
        {
            void Walk(UIElement e)
            {
                if (e is Expander x) { x.IsExpanded = on; if (x.Content is Panel p) foreach (var c in p.Children) Walk(c); }
            }
            foreach (var c in _list.Children) Walk(c);
        }
    }
}
