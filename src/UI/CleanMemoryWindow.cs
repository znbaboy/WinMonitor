using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinMonitor.Core;
using WinMonitor.Services;
using Windows.Graphics;

namespace WinMonitor.UI
{
    /// <summary>A small panel-styled popup that shows memory cleaning progress, then closes itself.</summary>
    internal sealed class CleanMemoryWindow : Window
    {
        private static bool _running;
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6 };
        private readonly TextBlock _title = new();
        private readonly TextBlock _percent = new() { HorizontalAlignment = HorizontalAlignment.Right };

        private CleanMemoryWindow(PanelWindow panel)
        {
            var t = ThemeManager.Current;
            var fg = new SolidColorBrush(ThemeManager.ParseColor(t.Color.TextPrimary));
            _title.Text = LanguageManager.T("Menu.CleanMemory") + "…";
            _title.Foreground = fg;
            _percent.Foreground = fg;
            _bar.Foreground = new SolidColorBrush(ThemeManager.ParseColor(t.Color.BarLow));
            _bar.Background = new SolidColorBrush(ThemeManager.ParseColor(t.Color.BarBackground));

            var top = new Grid();
            top.Children.Add(_title);
            top.Children.Add(_percent);
            var stack = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) };
            stack.Children.Add(top);
            stack.Children.Add(_bar);
            Content = new Border
            {
                Child = stack,
                Background = new SolidColorBrush(ThemeManager.ParseColor(t.Color.Background)),
                CornerRadius = new CornerRadius(Math.Max(8, t.Layout.CornerRadius)),
            };

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var p = OverlappedPresenter.Create();
            AppWindow.SetPresenter(p);
            p.IsResizable = p.IsMaximizable = p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(false, false);
            AppWindow.IsShownInSwitchers = false;
            Native.SetExStyle(hwnd, Native.ExStyle(hwnd) | Native.WS_EX_TOOLWINDOW);
            Native.SetCorners(hwnd, 1);
            SystemBackdrop = new TransparentBackdrop();

            // centred on the panel's screen
            double s = Native.Scale(hwnd);
            var b = Native.Bounds(panel.Hwnd);
            var area = Native.ScreenFromPoint(b.Left + b.Width / 2, b.Top + b.Height / 2).WorkArea;
            int w = (int)(320 * s), h = (int)(96 * s);
            AppWindow.MoveAndResize(new RectInt32(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h));
        }

        public static async void Run(PanelWindow panel)
        {
            if (_running) return;
            _running = true;
            var win = new CleanMemoryWindow(panel);
            try
            {
                win.AppWindow.Show();
                var dq = win.DispatcherQueue;
                await Task.Run(() =>
                {
                    long last = 0;
                    HardwareMonitor.Instance?.CleanMemory(pct =>
                    {
                        long now = DateTime.Now.Ticks;
                        if (pct < 100 && now - last < 150_000) return;   // at most ~60 updates a second
                        last = now;
                        dq.TryEnqueue(() => win.SetProgress(pct));
                    });
                });
                win.SetProgress(100);
                win._title.Text = LanguageManager.T("Menu.CleanMemorySuccess");
                win._percent.Text = "OK";
                await Task.Delay(800);
            }
            catch (Exception ex) { Program.Log(ex); }
            finally
            {
                win.Close();
                _running = false;
            }
        }

        private void SetProgress(int v)
        {
            v = Math.Clamp(v, 0, 100);
            _bar.Value = v;
            _percent.Text = v + "%";
        }
    }
}
