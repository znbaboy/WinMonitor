using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Windows.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace WinMonitor.UI
{
    /// <summary>A normal app window (settings, history, ...): custom title row, Mica where supported, centred.</summary>
    internal class ToolWindow : Window
    {
        public IntPtr Hwnd { get; }
        protected Grid Shell { get; } = new();
        private readonly TextBlock _title;

        protected ToolWindow(string title, int widthDip, int heightDip)
        {
            Title = title;
            Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            ExtendsContentIntoTitleBar = true;
            Shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            Shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var bar = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            _title = new TextBlock
            {
                Text = title,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            };
            bar.Children.Add(_title);
            Shell.Children.Add(bar);
            SetTitleBar(bar);

            if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
            else Shell.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
            Content = Shell;

            Shell.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
            UpdateCaptionButtons();

            double s = Native.Scale(Hwnd);
            var area = Native.ScreenFromPoint(Native.Cursor().X, Native.Cursor().Y).WorkArea;
            int w = Math.Min((int)(widthDip * s), area.Width), h = Math.Min((int)(heightDip * s), area.Height);
            AppWindow.MoveAndResize(new RectInt32(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h));
        }

        /// <summary>Puts the page content under the title row.</summary>
        protected void SetBody(UIElement body)
        {
            Grid.SetRow((FrameworkElement)body, 1);
            Shell.Children.Add(body);
        }

        private void UpdateCaptionButtons()
        {
            bool dark = Shell.ActualTheme == ElementTheme.Dark;
            var tb = AppWindow.TitleBar;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x20, 255, 255, 255) : Windows.UI.Color.FromArgb(0x20, 0, 0, 0);
            tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 255, 255, 255) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
        }
    }

    /// <summary>Small builders for settings-style pages (cards with a title on the left and a control on the right).</summary>
    internal static class Ui
    {
        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

        public static ScrollViewer Page(params UIElement[] children)
        {
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(24, 8, 24, 24), MaxWidth = 1000 };
            foreach (var c in children) stack.Children.Add(c);
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        public static TextBlock PageTitle(string text) => new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
            Margin = new Thickness(0, 0, 0, 12),
        };

        public static TextBlock Section(string text) => new()
        {
            Text = text,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(0, 20, 0, 6),
        };

        public static TextBlock Hint(string text) => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextFillColorSecondaryBrush"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Margin = new Thickness(2, 0, 2, 6),
        };

        public static Border Card(UIElement content, Thickness? padding = null) => new()
        {
            Child = content,
            Background = Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = padding ?? new Thickness(16, 10, 16, 10),
            Margin = new Thickness(0, 0, 0, 2),
        };

        /// <summary>A settings row: title (and optional description) left, control right.</summary>
        public static Border Row(string title, FrameworkElement control, string? description = null)
        {
            var g = new Grid { ColumnSpacing = 16, MinHeight = 44 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(description))
                text.Children.Add(new TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("TextFillColorSecondaryBrush"),
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                });
            g.Children.Add(text);
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return Card(g);
        }

        public static ToggleSwitch Toggle(bool value, Action<bool> changed)
        {
            var t = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
            t.Toggled += (_, _) => changed(t.IsOn);
            return t;
        }

        public static NumberBox Number(double value, double min, double max, Action<double> changed, double step = 1)
        {
            var n = new NumberBox
            {
                Value = value,
                Minimum = min,
                Maximum = max,
                SmallChange = step,
                LargeChange = step * 10,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Width = 130,
                NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 0, IsGrouped = false },
            };
            n.ValueChanged += (_, e) =>
            {
                if (double.IsNaN(e.NewValue)) { n.Value = e.OldValue; return; }
                changed(e.NewValue);
            };
            return n;
        }

        /// <summary>A number box with a unit after it.</summary>
        public static FrameworkElement NumberUnit(double value, double min, double max, string unit, Action<double> changed, double step = 1)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            p.Children.Add(Number(value, min, max, changed, step));
            p.Children.Add(new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, MinWidth = 36, Foreground = Res("TextFillColorSecondaryBrush") });
            return p;
        }

        public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed, double width = 220)
        {
            var c = new ComboBox { Width = width };
            foreach (var i in items) c.Items.Add(i);
            c.SelectedIndex = Math.Clamp(selected, -1, c.Items.Count - 1);
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
            return c;
        }

        public static Button Button(string text, Action click, bool accent = false)
        {
            var b = new Button { Content = text };
            if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            b.Click += (_, _) => click();
            return b;
        }

        public static HyperlinkButton Link(string text, string url) => new()
        {
            Content = text,
            NavigateUri = new Uri(url),
            Padding = new Thickness(0),
        };

        public static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        public static FontWeight Bold => FontWeights.SemiBold;
    }
}
