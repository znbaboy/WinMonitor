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
            stack.Children.Add(Ui.Hint("Drag ≡ to reorder categories and rows. Switch a category off to hide it from the panel; its rows keep their ticks. ✏️ renames a row or changes its unit."));
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
            var handle = Handle();
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
                count.Text = on ? $"{items.Count(i => i.VisibleInPanel)} of {items.Count} shown" : "Hidden from the panel";
            }

            foreach (var it in items) rows.Add(ItemRow(it, Refresh));

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
            card.Expanding += (_, _) => Expanded.Add(group);
            card.Collapsed += (_, _) => Expanded.Remove(group);
            toggle.Toggled += (_, _) => { _app.SetGroupEnabled(group, toggle.IsOn); Refresh(); };
            Refresh();

            _categories.AttachHandle(handle, card);
            card.DataContext = rows;   // the rows panel, read back when saving the order
            return card;
        }

        // ------------------------------------------------------------------ row
        private FrameworkElement ItemRow(MonitorItemConfig item, Action refreshCount)
        {
            var handle = Handle();
            var show = new CheckBox { Content = AppHost.ItemName(item), IsChecked = item.VisibleInPanel, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
            show.Click += (_, _) =>
            {
                item.VisibleInPanel = show.IsChecked == true;
                Apply();
                refreshCount();
            };

            var key = new TextBlock
            {
                Text = item.Key,
                Foreground = Ui.Res("TextFillColorTertiaryBrush"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };

            CheckBox? bar = null;
            if (item.HasBar)
            {
                bar = new CheckBox { Content = T("Menu.ShowBar"), IsChecked = item.ShowBar, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                bar.Click += (_, _) => { item.ShowBar = bar.IsChecked == true; Apply(); };
            }

            var edit = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Padding = new Thickness(8, 6, 8, 6),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Flyout = EditFlyout(item, show),
            };
            ToolTipService.SetToolTip(edit, "Rename or change the unit");

            var row = new Grid { ColumnSpacing = 8, MinHeight = 40, Padding = new Thickness(0, 0, 4, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddAt(row, handle, 0); AddAt(row, show, 1); AddAt(row, key, 2);
            if (bar != null) AddAt(row, bar, 3);
            AddAt(row, edit, 4);
            row.Tag = item;
            return row;
        }

        /// <summary>Name and unit for one row. Empty = automatic; a single space hides it.</summary>
        private Flyout EditFlyout(MonitorItemConfig item, CheckBox show)
        {
            string defName = LanguageManager.GetOriginal(UIUtils.Intern("Items." + item.Key));
            string defUnit = MetricUtils.GetUnitStr(item.Key, 0, MetricUtils.UnitContext.SettingsPanel);

            var name = new TextBox { Header = "Name", PlaceholderText = defName, Width = 260 };
            var unit = new TextBox { Header = "Unit", PlaceholderText = string.IsNullOrEmpty(defUnit) ? "(none)" : defUnit, Width = 260 };
            void Load()
            {
                name.Text = item.UserLabel ?? "";
                unit.Text = item.UnitPanel == null ? "" : (item.UnitPanel == "" ? " " : item.UnitPanel);
            }

            var flyout = new Flyout();
            void Save()
            {
                // name: empty = automatic, a single space hides it
                string n = name.Text;
                item.UserLabel = n.Length > 0 && string.IsNullOrWhiteSpace(n) ? " " : (n.Trim() == defName ? "" : n.Trim());
                // unit: empty = automatic, spaces = none, anything else = custom ({u} = the automatic unit)
                string u = unit.Text;
                item.UnitPanel = u.Length == 0 ? null : string.IsNullOrWhiteSpace(u) ? "" : (u == defUnit ? null : u);
                show.Content = AppHost.ItemName(item);
                Apply();
                flyout.Hide();
            }
            void Reset()
            {
                item.UserLabel = "";
                item.UnitPanel = null;
                show.Content = AppHost.ItemName(item);
                Apply();
                flyout.Hide();
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Ui.Button("Save", Save, accent: true));
            buttons.Children.Add(Ui.Button("Reset", Reset));
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(name);
            panel.Children.Add(unit);
            panel.Children.Add(Ui.Hint("Empty = automatic. A single space hides it. In units, {u} stands for the automatic unit (e.g. {u}/s)."));
            panel.Children.Add(buttons);
            flyout.Content = panel;
            flyout.Opening += (_, _) => Load();
            name.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Save(); };
            unit.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Save(); };
            return flyout;
        }

        // ------------------------------------------------------------------ helpers
        private static FrameworkElement Handle()
        {
            var h = new Border
            {
                Width = 28,
                Height = 32,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),   // whole area grabs
                Child = new FontIcon { Glyph = "", FontSize = 14, Foreground = Ui.Res("TextFillColorSecondaryBrush") },
                VerticalAlignment = VerticalAlignment.Center,
            };
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
                if (card.DataContext is ReorderPanel rows)
                    foreach (var row in rows.Children.OfType<FrameworkElement>())
                        if (row.Tag is MonitorItemConfig item) item.SortIndex = idx++;
            _cfg.MonitorItems = _cfg.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            Apply();
        }

        private void Apply()
        {
            _cfg.SyncToLanguage();   // custom names win over the built-in texts
            _cfg.Save();
            _app.ApplyMonitorLayout();
        }
    }

    /// <summary>
    /// A vertical list whose children are reordered by dragging a handle: the row moves live
    /// under the pointer, and <see cref="Reordered"/> fires once when it is dropped somewhere new.
    /// </summary>
    internal sealed class ReorderPanel : StackPanel
    {
        public event Action? Reordered;
        private UIElement? _drag;
        private int _startIndex;

        public ReorderPanel()
        {
            PointerMoved += OnMoved;
            PointerReleased += (_, _) => Drop();
            PointerCaptureLost += (_, _) => Drop();
        }

        public void Add(UIElement row) => Children.Add(row);

        /// <summary>Pressing the handle starts dragging the row (the panel holds the pointer, so the row can move).</summary>
        public void AttachHandle(UIElement handle, UIElement row)
        {
            handle.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                _drag = row;
                _startIndex = Children.IndexOf(row);
                row.Opacity = 0.6;
                CapturePointer(e.Pointer);
                e.Handled = true;   // not a click on the card header
            };
        }

        private void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_drag == null) return;
            double y = e.GetCurrentPoint(this).Position.Y;
            int from = Children.IndexOf(_drag);
            int to = Children.Count - 1;
            for (int i = 0; i < Children.Count; i++)
            {
                var child = (FrameworkElement)Children[i];
                double top = child.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (y < top + child.ActualHeight / 2) { to = i > from ? i - 1 : i; break; }
            }
            if (to != from && to >= 0) Children.Move((uint)from, (uint)to);
            e.Handled = true;
        }

        private void Drop()
        {
            if (_drag == null) return;
            var row = _drag;
            _drag = null;
            row.Opacity = 1;
            ReleasePointerCaptures();
            if (Children.IndexOf(row) != _startIndex) Reordered?.Invoke();
        }
    }
}
