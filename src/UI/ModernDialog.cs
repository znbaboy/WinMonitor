using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinMonitor.UI
{
    /// <summary>
    /// Windows 11 dialogs. Inside an open window: a ContentDialog over that window.
    /// Otherwise (from the panel, tray or startup): a small dialog window laid out the same way.
    /// </summary>
    internal static class ModernDialog
    {
        /// <summary>Shows the dialog; true when the primary button was chosen. UI thread only.</summary>
        public static async Task<bool> ShowAsync(string title, string text, string primary, string? secondary, XamlRoot? root)
        {
            App.EnsureControlResources();
            if (root != null)
            {
                var d = new ContentDialog
                {
                    Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                    Title = title,
                    Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = primary,
                    CloseButtonText = secondary ?? "",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = root,
                };
                if (root.Content is FrameworkElement fe) d.RequestedTheme = fe.ActualTheme;
                return await d.ShowAsync() == ContentDialogResult.Primary;
            }
            return await new DialogWindow(title, text, primary, secondary).Result;
        }

        /// <summary>A stand-alone dialog window: title and message on top, buttons in a footer bar.</summary>
        private sealed class DialogWindow : ToolWindow
        {
            private const int WidthDip = 460;
            private readonly TaskCompletionSource<bool> _result = new();
            public Task<bool> Result => _result.Task;

            public DialogWindow(string title, string text, string primary, string? secondary) : base("WinMonitor", WidthDip, 220)
            {
                Title = title;   // taskbar / Alt+Tab name; the caption row keeps "WinMonitor"
                var presenter = OverlappedPresenter.CreateForDialog();
                presenter.IsResizable = false;
                presenter.IsAlwaysOnTop = true;
                AppWindow.SetPresenter(presenter);

                var body = new StackPanel { Spacing = 12, Padding = new Thickness(24, 4, 24, 24) };
                body.Children.Add(new TextBlock
                {
                    Text = title,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"],
                });
                body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

                var buttons = new Grid { ColumnSpacing = 8 };
                var ok = new Button
                {
                    Content = primary,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Style = (Style)Application.Current.Resources["AccentButtonStyle"],
                };
                ok.Click += (_, _) => Finish(true);
                buttons.ColumnDefinitions.Add(new ColumnDefinition());
                buttons.Children.Add(ok);
                if (secondary != null)
                {
                    var no = new Button { Content = secondary, HorizontalAlignment = HorizontalAlignment.Stretch };
                    no.Click += (_, _) => Finish(false);
                    buttons.ColumnDefinitions.Add(new ColumnDefinition());
                    Grid.SetColumn(no, 1);
                    buttons.Children.Add(no);
                }
                else
                {
                    // a single button sits on the right half, like a ContentDialog with one button
                    buttons.ColumnDefinitions.Insert(0, new ColumnDefinition());
                    Grid.SetColumn(ok, 1);
                }

                var footer = new Border
                {
                    Child = buttons,
                    Padding = new Thickness(24),
                    Background = Ui.Res("SolidBackgroundFillColorBaseBrush"),
                    BorderBrush = Ui.Res("CardStrokeColorDefaultBrush"),
                    BorderThickness = new Thickness(0, 1, 0, 0),
                };

                var root = new Grid();
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.Children.Add(body);
                Grid.SetRow(footer, 1);
                root.Children.Add(footer);
                root.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape) Finish(false);
                    else if (e.Key == Windows.System.VirtualKey.Enter) Finish(true);
                };
                root.Loaded += (_, _) => ok.Focus(FocusState.Programmatic);
                SetBody(root);
                Closed += (_, _) => _result.TrySetResult(false);

                // fit the height to the text, then centre on the screen under the cursor
                root.Measure(new Size(WidthDip, double.PositiveInfinity));
                double s = Native.Scale(Hwnd);
                int w = (int)(WidthDip * s), h = (int)((root.DesiredSize.Height + 40) * s);
                var area = Native.ScreenFromPoint(Native.Cursor().X, Native.Cursor().Y).WorkArea;
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h));
                Activate();
            }

            private void Finish(bool value)
            {
                _result.TrySetResult(value);
                Close();
            }
        }
    }
}
