using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinMonitor.Core;
using WinMonitor.Services;

namespace WinMonitor.UI
{
    /// <summary>
    /// Settings → Monitor Items. One collapsible card per category with an on/off switch;
    /// rows inside it are ticked on or off, renamed in a small edit popup, and both rows and
    /// categories are reordered by dragging their ≡ handle. Every change applies straight away.
    /// </summary>
    internal sealed class MonitorItemsPage
    {
        private static readonly HashSet<string> Expanded = new();   // open cards, kept while the app runs

        private readonly AppHost _app;
        private readonly Settings _cfg;
        private readonly ReorderPanel _categories = new() { Spacing = 6 };
        public ScrollViewer Root { get; }

        private static string T(string key) => LanguageManager.T(key);

        public MonitorItemsPage(AppHost app)
        {
            _app = app;
            _cfg = app.Cfg;

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            tools.Children.Add(Ui.Button("Expand all", () => SetAllExpanded(true)));
            tools.Children.Add(Ui.Button("Collapse all", () => SetAllExpanded(false)));

            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(24, 8, 24, 24), MaxWidth = 1000 };
            stack.Children.Add(Ui.PageTitle(T("Menu.MonitorItemDisplay")));
            stack.Children.Add(Ui.Hint("Drag ≡ to reorder categories and rows. Switch a category off to hide it from the panel; its rows keep their ticks. Type over a greyed name to rename a row; clear it to go back to automatic."));
            stack.Children.Add(tools);
            stack.Children.Add(_categories);
            Root = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _categories.Reordered += SaveOrder;
            foreach (var g in _cfg.MonitorItems.GroupBy(x => x.UIGroup).OrderBy(g => g.Min(x => x.SortIndex)))
                _categories.Add(CategoryCard(g.Key, g.OrderBy(x => x.SortIndex).ToList()));
        }

        // ------------------------------------------------------------------ category card
        private FrameworkElement CategoryCard(string group, List<MonitorItemConfig> items)
        {
            var rows = new ReorderPanel { Spacing = 0 };
            rows.Reordered += SaveOrder;

            // header: handle, name, "2 of 5 shown", switch
            var handle = Handle(group);
            var name = new TextBlock { Text = _app.GroupName(group), FontWeight = Ui.Bold, VerticalAlignment = VerticalAlignment.Center };
            var count = new TextBlock { Foreground = Ui.Res("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            var toggle = new ToggleSwitch { IsOn = _cfg.IsGroupEnabled(group), OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(toggle, "Show this category on the panel");

            var header = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddAt(header, handle, 0); AddAt(header, name, 1); AddAt(header, count, 2); AddAt(header, toggle, 3);

            // body: category name, then the rows
            string defName = LanguageManager.GetOriginal("Groups." + group);
            if (defName.StartsWith("Groups.")) defName = group;
            var rename = new TextBox
            {
                Header = "Category name",
                PlaceholderText = defName,
                Text = _cfg.GroupAliases.TryGetValue(group, out var alias) ? alias : "",
                Width = 260,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(36, 0, 0, 8),
            };
            OnCommit(rename, () =>
            {
                string v = rename.Text.Trim();
                if (v.Length > 0 && v != defName) _cfg.GroupAliases[group] = v; else { _cfg.GroupAliases.Remove(group); rename.Text = ""; }
                name.Text = _app.GroupName(group);
                Apply();
            });

            var inner = new StackPanel { Spacing = 0 };
            inner.Children.Add(rename);
            if (!SystemBattery.Present && items.Exists(i => SystemBattery.IsLaptopRow(i.Key)))
                inner.Children.Add(new InfoBar
                {
                    IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
                    Message = "No laptop battery found on this PC, so the laptop battery rows are hidden.",
                    Margin = new Thickness(36, 0, 0, 8),
                });
            inner.Children.Add(rows);
            // a Control, so switching the category off disables everything inside it
            var body = new ContentControl
            {
                Content = inner,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsTabStop = false,
            };

            void Refresh()
            {
                bool on = toggle.IsOn;
                body.IsEnabled = on;
                body.Opacity = on ? 1 : 0.5;
                var usable = items.Where(i => SystemBattery.Present || !SystemBattery.IsLaptopRow(i.Key)).ToList();
                count.Text = on ? $"{usable.Count(i => i.VisibleInPanel)} of {usable.Count} shown" : "Hidden from the panel";
            }

            // rows are built the first time the card opens (most cards stay closed: faster page)
            bool built = false;
            void Build()
            {
                if (built) return;
                built = true;
                foreach (var it in items)
                {
                    var row = ItemRow(it, rows, Refresh);
                    if (!SystemBattery.Present && SystemBattery.IsLaptopRow(it.Key)) row.Visibility = Visibility.Collapsed;   // kept for the saved order
                    rows.Add(row);
                }
            }

            var card = new Expander
            {
                Header = header,
                Content = body,
                IsExpanded = Expanded.Contains(group),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = group,
            };
            if (Environment.GetEnvironmentVariable("WINMONITOR_EXPAND_ALL") == "1") card.IsExpanded = true;   // screenshots in the automated tests
            if (card.IsExpanded) Build();
            card.Expanding += (_, _) => { Build(); Expanded.Add(group); };
            card.Collapsed += (_, _) => Expanded.Remove(group);
            toggle.Toggled += (_, _) => { _app.SetGroupEnabled(group, toggle.IsOn); Refresh(); };
            Refresh();

            _categories.AttachHandle(handle, card);
            card.DataContext = (rows, items);   // read back when saving the order
            return card;
        }

        // ------------------------------------------------------------------ row
        /// <summary>One line per row: handle, show tick, name, bar. Names apply on Enter or when leaving the box.</summary>
        private FrameworkElement ItemRow(MonitorItemConfig item, ReorderPanel rows, Action refreshCount)
        {
            var handle = Handle(item.Key);
            var show = new CheckBox { IsChecked = item.VisibleInPanel, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(show, "Show on the panel");
            show.Click += (_, _) =>
            {
                item.VisibleInPanel = show.IsChecked == true;
                Apply();
                refreshCount();
            };

            // name: empty = automatic (shown greyed), a single space hides it
            string defName = AutomaticName(item);
            var name = new TextBox { Text = item.UserLabel ?? "", PlaceholderText = defName, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(name, $"{item.Key}: leave empty for the automatic name, a space hides it");
            OnCommit(name, () =>
            {
                string n = name.Text;
                item.UserLabel = n.Length > 0 && string.IsNullOrWhiteSpace(n) ? " " : (n.Trim() == defName ? "" : n.Trim());
                if (item.UserLabel == "") name.Text = "";
                Apply();
            });


            CheckBox? bar = null;
            if (item.HasBar)
            {
                bar = new CheckBox { Content = T("Menu.ShowBar"), IsChecked = item.ShowBar, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                bar.Click += (_, _) => { item.ShowBar = bar.IsChecked == true; Apply(); };
            }

            var row = new Grid { ColumnSpacing = 8, MinHeight = 44, Padding = new Thickness(0, 2, 4, 2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // handle
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });                    // show
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // name
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });                    // bar
            AddAt(row, handle, 0); AddAt(row, show, 1); AddAt(row, name, 2);
            if (bar != null) AddAt(row, bar, 3);
            row.Tag = item;
            rows.AttachHandle(handle, row);
            return row;
        }

        /// <summary>The name a row has without a custom one (live name, else the built-in text).</summary>
        private static string AutomaticName(MonitorItemConfig item)
        {
            string saved = item.UserLabel;
            item.UserLabel = "";
            string n = AppHost.ItemName(item);
            item.UserLabel = saved;
            return n;
        }

        // ------------------------------------------------------------------ helpers
        /// <summary>The ≡ drag handle (a Control, so screen readers and UI tests can find it by name).</summary>
        private static FrameworkElement Handle(string name)
        {
            var h = new ContentControl
            {
                Width = 28,
                Height = 32,
                IsTabStop = false,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),   // whole area grabs
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = "\uE700", FontSize = 14, Foreground = Ui.Res("TextFillColorSecondaryBrush") },
                VerticalAlignment = VerticalAlignment.Center,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(h, "Move " + name);
            ToolTipService.SetToolTip(h, "Drag to move");
            return h;
        }

        private static void AddAt(Grid g, FrameworkElement e, int column)
        {
            Grid.SetColumn(e, column);
            g.Children.Add(e);
        }

        private static void OnCommit(TextBox box, Action commit)
        {
            string last = box.Text;
            void Do() { if (box.Text == last) return; last = box.Text; commit(); }
            box.LostFocus += (_, _) => Do();
            box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Do(); };
        }

        private void SetAllExpanded(bool on)
        {
            foreach (var c in _categories.Children.OfType<Expander>()) c.IsExpanded = on;
        }

        /// <summary>Numbers every row in the order shown (categories, then rows inside each).</summary>
        private void SaveOrder()
        {
            int idx = 0;
            foreach (var card in _categories.Children.OfType<Expander>())
                if (card.DataContext is (ReorderPanel rows, List<MonitorItemConfig> items))
                {
                    if (rows.Children.Count == 0) { foreach (var item in items) item.SortIndex = idx++; continue; }   // never opened
                    foreach (var row in rows.Children.OfType<FrameworkElement>())
                        if (row.Tag is MonitorItemConfig item) item.SortIndex = idx++;
                }
            _cfg.MonitorItems = _cfg.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            Apply();
        }

        private void Apply()
        {
            _cfg.SyncToLanguage();   // custom names win over the built-in texts
            _app.SaveSettingsSoon();
            _app.ApplyMonitorLayoutSoon();
        }
    }

    /// <summary>
    /// A vertical list whose children are reordered by dragging a handle. The handle keeps the mouse
    /// while dragging; the row follows the pointer and the others slide aside (render transforms only,
    /// so nothing is re-parented mid-drag). The order changes once, on release, then
    /// <see cref="Reordered"/> fires. No Windows drag and drop: that does not reach an app running as administrator.
    /// </summary>
    internal sealed class ReorderPanel : StackPanel
    {
        public event Action? Reordered;

        private FrameworkElement? _drag;
        private int _from, _to;
        private double _startY, _step;

        // WINMONITOR_TRACE=1 writes drag steps to WinMonitor_Trace.log (automated tests)
        private static readonly bool Tracing = Environment.GetEnvironmentVariable("WINMONITOR_TRACE") == "1";
        private static void Trace(string m)
        {
            if (!Tracing) return;
            try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppInfo.DataDir, "WinMonitor_Trace.log"), $"{DateTime.Now:HH:mm:ss.fff} {m}\r\n"); } catch { }
        }

        public void Add(UIElement row) => Children.Add(row);

        private static TranslateTransform Shift(UIElement e)
        {
            if (e.RenderTransform is not TranslateTransform t) e.RenderTransform = t = new TranslateTransform();
            return t;
        }

        public void AttachHandle(UIElement handle, FrameworkElement row)
        {
            handle.PointerPressed += (_, e) =>
            {
                e.Handled = true;   // not a click on the card header
                if (_drag != null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                if (!handle.CapturePointer(e.Pointer)) return;
                _drag = row;
                _from = _to = Children.IndexOf(row);
                _startY = e.GetCurrentPoint(this).Position.Y;
                _step = row.ActualHeight + Spacing;
                Canvas.SetZIndex(row, 1);
                row.Opacity = 0.85;
                Trace($"press index={_from}");
            };
            handle.PointerMoved += (_, e) =>
            {
                if (_drag != row) return;
                e.Handled = true;
                AutoScroll(e);

                // keep the row inside the list
                double top0 = row.ActualOffset.Y;
                double dy = e.GetCurrentPoint(this).Position.Y - _startY;
                dy = Math.Max(-top0, Math.Min(dy, ActualHeight - row.ActualHeight - top0));
                Shift(row).Y = dy;

                // Drop slot: judged by the dragged row's top (its header, under the pointer), against the
                // middles of the other rows laid out as if the dragged one were gone. Works the same up and
                // down, however tall the dragged card is.
                double probe = top0 + dy + Math.Min(row.ActualHeight, 40) / 2;
                double y = 0;
                int to = 0;
                for (int i = 0; i < Children.Count; i++)
                {
                    if (i == _from) continue;
                    var c = (FrameworkElement)Children[i];
                    if (probe > y + c.ActualHeight / 2) to++;
                    y += c.ActualHeight + Spacing;
                }

                // the rows in between slide one place to make room
                for (int i = 0; i < Children.Count; i++)
                {
                    if (i == _from) continue;
                    double shift = (i > _from && i <= to) ? -_step : (i < _from && i >= to) ? _step : 0;
                    Shift(Children[i]).Y = shift;
                }
                if (to != _to) Trace($"move dy={dy:0} to={to}");
                _to = to;
            };
            handle.PointerReleased += (_, e) => { if (_drag == row) { e.Handled = true; Finish(); handle.ReleasePointerCapture(e.Pointer); } };
            handle.PointerCaptureLost += (_, _) => { if (_drag == row) Finish(); };
            handle.PointerCanceled += (_, _) => { if (_drag == row) Finish(); };
        }

        /// <summary>Scrolls the page when dragging near its top or bottom edge.</summary>
        private void AutoScroll(PointerRoutedEventArgs e)
        {
            DependencyObject? p = this;
            while (p != null && p is not ScrollViewer) p = VisualTreeHelper.GetParent(p);
            if (p is not ScrollViewer sv) return;
            double y = e.GetCurrentPoint(sv).Position.Y;
            const double edge = 48;
            if (y < edge) sv.ChangeView(null, Math.Max(0, sv.VerticalOffset - (edge - y) / 2), null, true);
            else if (y > sv.ViewportHeight - edge) sv.ChangeView(null, sv.VerticalOffset + (y - (sv.ViewportHeight - edge)) / 2, null, true);
        }

        private void Finish()
        {
            var row = _drag;
            if (row == null) return;
            _drag = null;
            foreach (var c in Children) Shift(c).Y = 0;
            Canvas.SetZIndex(row, 0);
            row.Opacity = 1;
            Trace($"drop from={_from} to={_to}");
            if (_to != _from)
            {
                Children.Move((uint)_from, (uint)_to);
                Reordered?.Invoke();
            }
        }
    }
}
