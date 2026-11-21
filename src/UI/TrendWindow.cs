using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinMonitor.Core;
using WinMonitor.Services;
using Windows.Foundation;
using Color = Windows.UI.Color;

namespace WinMonitor.UI
{
    /// <summary>Monitor history: temperature, load, power and clock over 1 h / 24 h / 7 days (kept for 7 days).</summary>
    internal sealed class TrendWindow : ToolWindow
    {
        private sealed class Point { public DateTime Time; public float Avg, Max, Min; public int Count; }
        private sealed class Series { public string Key = "", Label = "", Unit = ""; public Color Color; public List<Point> Points = new(); }

        private static readonly (string Name, string[] Keys)[] Categories =
        {
            ("Temp", HardwareHistoryLogger.TemperatureKeys),
            ("Load", HardwareHistoryLogger.LoadKeys),
            ("Power", HardwareHistoryLogger.PowerKeys),
            ("Clock", HardwareHistoryLogger.FrequencyKeys),
        };
        private static readonly (string Name, TimeSpan Span, int Bucket)[] Ranges =
        {
            ("1 hour", TimeSpan.FromHours(1), 1),
            ("24 hours", TimeSpan.FromDays(1), 5),
            ("7 days", TimeSpan.FromDays(7), 60),
        };
        private static readonly Color[] Palette =
        {
            Color.FromArgb(255, 0x4C, 0xC2, 0xFF), Color.FromArgb(255, 0xFF, 0x8C, 0x42), Color.FromArgb(255, 0x6C, 0xD9, 0x7E),
            Color.FromArgb(255, 0xE0, 0x6C, 0xD9), Color.FromArgb(255, 0xFF, 0xD6, 0x66),
        };

        private readonly Settings _cfg;
        private int _category, _range;
        private bool _showRange; // min–max band, off by default
        private List<Series> _series = new();
        private readonly Canvas _chart = new();
        private readonly StackPanel _table = new() { Spacing = 2 };
        private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
        private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refresh;

        public TrendWindow(Settings cfg) : base(LanguageManager.T("Menu.MonitorHistory") + " - WinMonitor", 980, 680)
        {
            _cfg = cfg;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(16, 0, 16, 8) };
            var catButtons = new List<ToggleButton>();
            var rangeButtons = new List<ToggleButton>();
            for (int i = 0; i < Categories.Length; i++)
            {
                int idx = i;
                var b = new ToggleButton { Content = Categories[i].Name, IsChecked = i == 0, MinWidth = 64 };
                b.Click += (_, _) => { _category = idx; for (int j = 0; j < catButtons.Count; j++) catButtons[j].IsChecked = j == idx; LoadData(); };
                catButtons.Add(b);
                toolbar.Children.Add(b);
            }
            toolbar.Children.Add(new AppBarSeparator());
            for (int i = 0; i < Ranges.Length; i++)
            {
                int idx = i;
                var b = new ToggleButton { Content = Ranges[i].Name, IsChecked = i == 0, MinWidth = 64 };
                b.Click += (_, _) => { _range = idx; for (int j = 0; j < rangeButtons.Count; j++) rangeButtons[j].IsChecked = j == idx; LoadData(); };
                rangeButtons.Add(b);
                toolbar.Children.Add(b);
            }
            toolbar.Children.Add(new AppBarSeparator());
            var rangeBand = new ToggleButton { Content = "Min–max", IsChecked = false };
            ToolTipService.SetToolTip(rangeBand, "Shade the lowest-to-highest reading of each time slice");
            rangeBand.Click += (_, _) => { _showRange = rangeBand.IsChecked == true; Draw(); };
            toolbar.Children.Add(rangeBand);
            toolbar.Children.Add(Ui.Button("Export CSV", ExportCsv));

            var chartHost = new Grid { Margin = new Thickness(16, 0, 16, 8) };
            chartHost.Children.Add(Ui.Card(_chart, new Thickness(0)));
            chartHost.Children.Add(_empty);
            _chart.SizeChanged += (_, _) => Draw();

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(toolbar);
            Grid.SetRow(chartHost, 1);
            root.Children.Add(chartHost);
            var tableCard = Ui.Card(_table, new Thickness(12, 8, 12, 8));
            tableCard.Margin = new Thickness(16, 0, 16, 16);
            Grid.SetRow(tableCard, 2);
            root.Children.Add(tableCard);
            SetBody(root);

            _refresh = DispatcherQueue.CreateTimer();
            _refresh.Interval = TimeSpan.FromSeconds(30);
            _refresh.Tick += (_, _) => LoadData();
            _refresh.Start();
            Closed += (_, _) => _refresh.Stop();
            LoadData();
        }

        private string Label(string key)
        {
            var item = _cfg.MonitorItems.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            string l = item != null ? MetricLabelResolver.ResolveLabel(item) : "";
            if (string.IsNullOrWhiteSpace(l)) l = LanguageManager.T("Items." + key);
            if (string.IsNullOrWhiteSpace(l)) return key;
            // CPU and GPU rows share short names ("Temp"); the chart mixes them, so say which
            string grp = key.Split('.')[0];
            return grp is "CPU" or "GPU" && !l.Contains(grp, StringComparison.OrdinalIgnoreCase) ? grp + " " + l : l;
        }

        private static string Unit(string key) => MetricUtils.GetType(key) switch
        {
            MetricType.Temperature => "°C",
            MetricType.Percent or MetricType.Memory => "%",
            MetricType.Frequency => "GHz",
            MetricType.Power => "W",
            _ => "",
        };

        private static float Normalize(string key, float v) => MetricUtils.GetType(key) == MetricType.Frequency ? v / 1000f : v;

        private static string Format(float v, string unit)
        {
            string text = unit == "GHz" ? v.ToString("0.0", CultureInfo.InvariantCulture)
                        : v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
            return text + unit;
        }

        private void LoadData()
        {
            var (_, span, bucket) = Ranges[_range];
            var raw = HardwareHistoryLogger.GetPoints(DateTime.Now - span);
            long ticks = TimeSpan.FromMinutes(bucket).Ticks;
            _series = Categories[_category].Keys.Select((key, i) => new Series
            {
                Key = key,
                Label = Label(key),
                Unit = Unit(key),
                Color = Palette[i % Palette.Length],
                Points = raw.Where(p => p.Values.ContainsKey(key))
                    .Select(p => (p.Time, V: p.Values[key]))
                    .GroupBy(x => new DateTime(x.Time.Ticks / ticks * ticks))
                    .Select(g =>
                    {
                        int count = g.Sum(x => Math.Max(1, x.V.Count));
                        return new Point
                        {
                            Time = g.Key,
                            Avg = count > 0 ? g.Sum(x => Normalize(key, x.V.Avg) * Math.Max(1, x.V.Count)) / count : 0,
                            Max = g.Max(x => Normalize(key, x.V.Max)),
                            Min = g.Min(x => Normalize(key, x.V.Min)),
                            Count = count,
                        };
                    })
                    .OrderBy(p => p.Time).ToList(),
            }).Where(s => s.Points.Count > 0).ToList();

            // hidden items are recorded too when their hardware is already polled for another visible item
            var mon = HardwareMonitor.Instance;
            bool enabled = Categories[_category].Keys.Any(k =>
                _cfg.MonitorItems.Any(i => i.Key == k && _cfg.IsShown(i)) || (mon?.IsReadInBackground(k) ?? false));
            _empty.Text = _series.Count > 0 ? "" : enabled ? "No history yet: values are recorded every 10 seconds while WinMonitor runs."
                                                           : "Nothing here is being read. Show an item from this category to record its history.";
            Draw();
            FillTable();
        }

        private void Draw()
        {
            _chart.Children.Clear();
            double w = _chart.ActualWidth, h = _chart.ActualHeight;
            if (w < 50 || h < 50) return;
            var (_, span, _) = Ranges[_range];
            DateTime to = DateTime.Now, from = to - span;
            const double left = 56, right = 16, top = 16, bottom = 28;
            double pw = w - left - right, ph = h - top - bottom;

            float max = _series.Count == 0 ? 100 : _series.SelectMany(s => s.Points).Max(p => _showRange ? p.Max : p.Avg);
            float min = _series.Count == 0 ? 0 : Math.Min(0, _series.SelectMany(s => s.Points).Min(p => _showRange ? p.Min : p.Avg));
            double niceMax = NiceCeiling(Math.Max(1, max * 1.1));
            var grid = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            var axisText = Ui.Res("TextFillColorSecondaryBrush");
            string unit = _series.FirstOrDefault()?.Unit ?? "";

            for (int i = 0; i <= 4; i++)
            {
                double v = min + (niceMax - min) * i / 4.0;
                double y = top + ph - ph * i / 4.0;
                _chart.Children.Add(new Line { X1 = left, X2 = left + pw, Y1 = y, Y2 = y, Stroke = grid, StrokeThickness = 1 });
                var t = new TextBlock { Text = Format((float)v, unit), FontSize = 11, Foreground = axisText, Width = left - 8, TextAlignment = TextAlignment.Right };
                Canvas.SetLeft(t, 0); Canvas.SetTop(t, y - 8);
                _chart.Children.Add(t);
            }
            for (int i = 0; i <= 4; i++)
            {
                var time = from + TimeSpan.FromTicks(span.Ticks * i / 4);
                double x = left + pw * i / 4.0;
                var t = new TextBlock { Text = span.TotalDays > 1.5 ? time.ToString("MM-dd HH:mm") : time.ToString("HH:mm"), FontSize = 11, Foreground = axisText };
                Canvas.SetLeft(t, Math.Min(w - 70, Math.Max(0, x - 20))); Canvas.SetTop(t, top + ph + 6);
                _chart.Children.Add(t);
            }

            foreach (var s in _series)
            {
                var line = new Polyline { Stroke = new SolidColorBrush(s.Color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                var band = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(40, s.Color.R, s.Color.G, s.Color.B)) };
                var lower = new List<Windows.Foundation.Point>();
                foreach (var p in s.Points)
                {
                    double x = left + pw * (p.Time - from).TotalSeconds / span.TotalSeconds;
                    if (x < left) continue;
                    double Y(float v) => top + ph - ph * (v - min) / (niceMax - min);
                    line.Points.Add(new Windows.Foundation.Point(x, Y(p.Avg)));
                    band.Points.Add(new Windows.Foundation.Point(x, Y(p.Max)));
                    lower.Insert(0, new Windows.Foundation.Point(x, Y(p.Min)));
                }
                foreach (var p in lower) band.Points.Add(p);
                if (_showRange) _chart.Children.Add(band);
                _chart.Children.Add(line);
            }
        }

        private static double NiceCeiling(double v)
        {
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            foreach (var step in new[] { 1, 2, 2.5, 5, 10 }) if (step * mag >= v) return step * mag;
            return 10 * mag;
        }

        private void FillTable()
        {
            _table.Children.Clear();
            Grid Row(string[] cells, Color? color, bool header)
            {
                var g = new Grid { ColumnSpacing = 12 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                for (int i = 1; i < cells.Length; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < cells.Length; i++)
                {
                    var t = new TextBlock { Text = cells[i], FontWeight = header ? Ui.Bold : Microsoft.UI.Text.FontWeights.Normal };
                    if (header) t.Foreground = Ui.Res("TextFillColorSecondaryBrush");
                    else if (i == 0 && color.HasValue) t.Foreground = new SolidColorBrush(color.Value);
                    Grid.SetColumn(t, i);
                    g.Children.Add(t);
                }
                return g;
            }
            _table.Children.Add(Row(new[] { "Item", "Latest", "Average", "Max", "Min", "Samples" }, null, true));
            foreach (var s in _series)
            {
                int count = s.Points.Sum(p => p.Count);
                float avg = count > 0 ? s.Points.Sum(p => p.Avg * p.Count) / count : 0;
                _table.Children.Add(Row(new[] { s.Label, Format(s.Points[^1].Avg, s.Unit), Format(avg, s.Unit),
                    Format(s.Points.Max(p => p.Max), s.Unit), Format(s.Points.Min(p => p.Min), s.Unit), count.ToString() }, s.Color, false));
            }
            if (_series.Count == 0) _table.Children.Add(new TextBlock { Text = "—", Opacity = 0.6 });
        }

        private async void ExportCsv()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"WinMonitor-history-{DateTime.Now:yyyyMMdd-HHmm}" };
                picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                var file = await picker.PickSaveFileAsync();
                if (file == null) return;
                var sb = new StringBuilder("Time,Key,Name,Average,Max,Min,Unit,Samples\r\n");
                string Esc(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
                foreach (var s in _series)
                    foreach (var p in s.Points)
                        sb.Append(p.Time.ToString("yyyy-MM-dd HH:mm")).Append(',').Append(Esc(s.Key)).Append(',').Append(Esc(s.Label)).Append(',')
                          .Append(p.Avg.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                          .Append(p.Max.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                          .Append(p.Min.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                          .Append(s.Unit).Append(',').Append(p.Count).Append("\r\n");
                await File.WriteAllTextAsync(file.Path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Services.Dialogs.Error("Export failed: " + ex.Message, root: Content.XamlRoot); }
        }
    }
}
