using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinMonitor.Core;
using WinMonitor.Services;
using Color = Windows.UI.Color;

namespace WinMonitor.UI
{
    /// <summary>Edit a theme file with a live preview on the panel. Unsaved changes are dropped on close.</summary>
    internal sealed class ThemeEditorWindow : ToolWindow
    {
        private static readonly string[] CommonFonts =
        {
            "Microsoft YaHei UI", "Segoe UI", "Segoe UI Variable Display", "Segoe UI Semibold", "Bahnschrift",
            "Consolas", "Cascadia Mono", "Cascadia Code", "Lucida Console", "Courier New", "Arial", "Tahoma", "Verdana",
        };

        private readonly AppHost _app;
        private readonly ComboBox _themes = new() { Width = 240 };
        private readonly ContentControl _fields = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private string _name = "";
        private Theme _theme = new();
        private bool _dirty;
        private bool _loading;

        public ThemeEditorWindow(AppHost app) : base(LanguageManager.T("Menu.ThemeEditor") + " - WinMonitor", 760, 820)
        {
            _app = app;
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 0, 24, 8) };
            bar.Children.Add(_themes);
            bar.Children.Add(Ui.Button("Save", Save, accent: true));
            bar.Children.Add(Ui.Button("Use this theme", UseTheme));
            var more = new DropDownButton { Content = "More" };
            var flyout = new MenuFlyout();
            void Item(string text, Func<Task> act) { var i = new MenuFlyoutItem { Text = text }; i.Click += async (_, _) => await act(); flyout.Items.Add(i); }
            Item("New…", () => NameDialog("New theme", "", n => { ThemeFileService.CreateTheme(n); return n; }));
            Item("Duplicate…", () => NameDialog("Duplicate theme", _name + "_copy", n => { ThemeFileService.DuplicateTheme(_name, n); return n; }));
            Item("Rename…", () => NameDialog("Rename theme", _name, n => { ThemeFileService.RenameTheme(_name, n); if (_app.Cfg.Skin == _name) { _app.Cfg.Skin = n; _app.Cfg.Save(); } return n; }));
            Item("Delete", DeleteTheme);
            more.Flyout = flyout;
            bar.Children.Add(more);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(bar);
            Grid.SetRow(_fields, 1);
            root.Children.Add(_fields);
            SetBody(root);

            _themes.SelectionChanged += async (_, _) =>
            {
                if (_loading || _themes.SelectedItem is not string n || n == _name) return;
                if (_dirty && !await Confirm($"Drop the unsaved changes to {_name}?")) { _loading = true; _themes.SelectedItem = _name; _loading = false; return; }
                Load(n);
            };
            Closed += (_, _) => _app.ApplyThemeAndLayout(retainData: true);   // back to the theme in use, as saved
            FillList(_app.Cfg.Skin);
        }

        private void FillList(string select)
        {
            _loading = true;
            _themes.Items.Clear();
            foreach (var n in ThemeFileService.ListThemes()) _themes.Items.Add(n);
            _loading = false;
            var pick = _themes.Items.Cast<string>().FirstOrDefault(n => n.Equals(select, StringComparison.OrdinalIgnoreCase))
                       ?? _themes.Items.Cast<string>().FirstOrDefault();
            if (pick != null) { _loading = true; _themes.SelectedItem = pick; _loading = false; Load(pick); }
        }

        private void Load(string name)
        {
            try { _theme = ThemeFileService.LoadTheme(name); }
            catch (Exception ex) { Dialogs.Error(ex.Message); return; }
            _name = name;
            _dirty = false;
            _fields.Content = BuildFields();
            Preview();
        }

        private void Changed()
        {
            _dirty = true;
            Preview();
        }

        private void Preview() => _app.Controller.PreviewTheme(_theme);

        private UIElement BuildFields()
        {
            var L = _theme.Layout;
            var F = _theme.Font;
            var C = _theme.Color;
            Border Num(string title, int value, Action<int> set, int max = 200) =>
                Ui.Row(title, Ui.NumberUnit(value, 0, max, "px", v => { set((int)v); Changed(); }));
            Border Pt(string title, double value, Action<double> set) =>
                Ui.Row(title, Ui.NumberUnit(value, 4, 72, "pt", v => { set(v); Changed(); }, 0.5));

            return Ui.Page(
                Ui.Hint("Changes show on the panel straight away. Save writes them to the theme file; closing without saving drops them."),
                Ui.Section("Layout"),
                Num("Corner radius", L.CornerRadius, v => L.CornerRadius = v),
                Num("Row height", L.RowHeight, v => L.RowHeight = v),
                Num("Padding", L.Padding, v => L.Padding = v),
                Num("Group corner radius", L.GroupRadius, v => L.GroupRadius = v),
                Num("Group padding", L.GroupPadding, v => L.GroupPadding = v),
                Num("Group spacing", L.GroupSpacing, v => L.GroupSpacing = v),
                Num("Group bottom space", L.GroupBottom, v => L.GroupBottom = v),
                Num("Item gap", L.ItemGap, v => L.ItemGap = v),
                Num("Group title offset", L.GroupTitleOffset, v => L.GroupTitleOffset = v),

                Ui.Section("Fonts"),
                Ui.Row("Font", FontBox(F.Family, v => F.Family = v)),
                Ui.Row("Value font", FontBox(F.ValueFamily, v => F.ValueFamily = v)),
                Pt("Title size", F.Title, v => F.Title = v),
                Pt("Group title size", F.Group, v => F.Group = v),
                Pt("Item size", F.Item, v => F.Item = v),
                Pt("Value size", F.Value, v => F.Value = v),
                Ui.Row("Bold", Ui.Toggle(F.Bold, v => { F.Bold = v; Changed(); })),

                Ui.Section("Colours"),
                ColorRow("Background", C.Background, v => C.Background = v),
                ColorRow("Group background", C.GroupBackground, v => C.GroupBackground = v),
                ColorRow("Title text", C.TextTitle, v => C.TextTitle = v),
                ColorRow("Group title text", C.TextGroup, v => C.TextGroup = v),
                ColorRow("Text", C.TextPrimary, v => C.TextPrimary = v),
                ColorRow("Value: safe", C.ValueSafe, v => C.ValueSafe = v),
                ColorRow("Value: warning", C.ValueWarn, v => C.ValueWarn = v),
                ColorRow("Value: critical", C.ValueCrit, v => C.ValueCrit = v),
                ColorRow("Bar background", C.BarBackground, v => C.BarBackground = v),
                ColorRow("Bar: low", C.BarLow, v => C.BarLow = v),
                ColorRow("Bar: medium", C.BarMid, v => C.BarMid = v),
                ColorRow("Bar: high", C.BarHigh, v => C.BarHigh = v)
            );
        }

        private FrameworkElement FontBox(string value, Action<string> set)
        {
            var cb = new ComboBox { IsEditable = true, Width = 240, Text = value };
            foreach (var f in CommonFonts) cb.Items.Add(f);
            cb.TextSubmitted += (_, e) => { set(e.Text.Trim()); Changed(); };
            cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s) { set(s); Changed(); } };
            return cb;
        }

        private Border ColorRow(string title, string value, Action<string> set)
        {
            var swatch = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Ui.Res("ControlStrokeColorDefaultBrush") };
            var hex = new TextBox { Text = value, Width = 120, FontFamily = new FontFamily("Consolas") };
            void Show(string h) => swatch.Background = new SolidColorBrush(ThemeManager.ParseColor(h));
            Show(value);

            var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = false, Color = ThemeManager.ParseColor(value) };
            bool fromPicker = false;
            picker.ColorChanged += (_, e) =>
            {
                fromPicker = true;
                string h = Hex(e.NewColor);
                hex.Text = h;
                set(h); Show(h); Changed();
                fromPicker = false;
            };
            var pick = new Button { Content = swatch, Padding = new Thickness(4), Flyout = new Flyout { Content = picker } };

            void Commit()
            {
                if (fromPicker) return;
                string h = hex.Text.Trim();
                if (!h.StartsWith('#')) h = "#" + h;
                if (h.Length != 7 && h.Length != 9) { hex.Text = value; return; }
                value = h; hex.Text = h;
                set(h); Show(h); Changed();
            }
            hex.LostFocus += (_, _) => Commit();
            hex.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };

            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            p.Children.Add(pick);
            p.Children.Add(hex);
            return Ui.Row(title, p);
        }

        private static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private void Save()
        {
            try
            {
                ThemeFileService.SaveTheme(_name, _theme);
                _dirty = false;
                if (_app.Cfg.Skin.Equals(_name, StringComparison.OrdinalIgnoreCase)) _app.ApplyThemeAndLayout(retainData: true);
                _app.Notify("WinMonitor", $"Theme saved: {_name}", false);
            }
            catch (Exception ex) { Dialogs.Error("Could not save the theme:\n" + ex.Message); }
        }

        private void UseTheme()
        {
            if (_dirty) Save();
            _app.Cfg.Skin = _name;
            _app.Cfg.Save();
            _app.ApplyThemeAndLayout();
        }

        private async Task DeleteTheme()
        {
            if (ThemeFileService.ListThemes().Count <= 1) { Dialogs.Info("The last theme cannot be deleted."); return; }
            if (!await Confirm($"Delete the theme {_name}? This cannot be undone.")) return;
            ThemeFileService.DeleteTheme(_name);
            if (_app.Cfg.Skin.Equals(_name, StringComparison.OrdinalIgnoreCase))
            {
                _app.Cfg.Skin = ThemeFileService.ListThemes().First();
                _app.Cfg.Save();
            }
            _dirty = false;
            _app.ApplyThemeAndLayout();
            FillList(_app.Cfg.Skin);
        }

        private async Task<bool> Confirm(string text)
        {
            var d = new ContentDialog
            {
                Title = "WinMonitor",
                Content = text,
                PrimaryButtonText = "Yes",
                CloseButtonText = "No",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            return await d.ShowAsync() == ContentDialogResult.Primary;
        }

        private async Task NameDialog(string title, string initial, Func<string, string> apply)
        {
            var box = new TextBox { Text = initial, PlaceholderText = "Theme name" };
            var d = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await d.ShowAsync() != ContentDialogResult.Primary) return;
            string name = box.Text.Trim();
            if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
            {
                Dialogs.Warn("Please use a name that is also a valid file name.");
                return;
            }
            try
            {
                string selected = apply(name);
                _dirty = false;
                FillList(selected);
            }
            catch (Exception ex) { Dialogs.Error(ex.Message); }
        }
    }
}
