using System;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinMonitor.Core;
using Windows.Graphics;

namespace WinMonitor.UI
{
    /// <summary>
    /// The floating panel: borderless, see-through corners, kept out of the taskbar and Alt+Tab.
    /// Window behaviour (drag, clamp, auto-hide at screen edges, always on top, click-through,
    /// position memory) ported from LiteMonitor's MainForm helpers.
    /// </summary>
    internal sealed class PanelWindow : Window
    {
        public IntPtr Hwnd { get; }
        public PanelView View { get; } = new();
        public WndProcHook Hook { get; }

        public event Action? MenuRequested;
        public event Action? DoubleClicked;

        private readonly Settings _cfg;
        private readonly DispatcherQueueTimer _autoHideTimer;
        private readonly DispatcherQueueTimer _topMostTimer;
        private readonly DispatcherQueueTimer _displayTimer;

        // dragging
        private bool _pressed;
        private (int X, int Y) _dragCursor, _dragWindow;
        public bool IsDragging => _pressed;

        // auto-hide
        private enum Edge { None, Left, Right, Top }
        private Edge _dock = Edge.None;
        private bool _hidden;
        private DateTime _keepVisibleUntil = DateTime.MinValue;
        private const int HideStrip = 4, HideThreshold = 10, HoverBand = 30;

        private int _widthPx, _heightPx;
        private bool _clickThrough;

        public PanelWindow(Settings cfg)
        {
            _cfg = cfg;
            Title = "WinMonitor";
            Content = View.Root;
            View.Root.Opacity = Math.Clamp(cfg.Opacity, 0.1, 1.0);

            Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Hook = new WndProcHook(Hwnd);

            var presenter = OverlappedPresenter.Create();
            AppWindow.SetPresenter(presenter);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
            AppWindow.IsShownInSwitchers = false;
            AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));

            // plain popup: no caption, frame or border lines
            long st = Native.Style(Hwnd);
            st &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX | 0x00800000L /* WS_BORDER */);
            Native.SetStyle(Hwnd, st | Native.WS_POPUP);
            Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

            // tool window: no taskbar button
            long ex = Native.ExStyle(Hwnd);
            Native.SetExStyle(Hwnd, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            Native.SetCorners(Hwnd, 1 /* square: the panel draws its own rounded corners */);
            SystemBackdrop = new TransparentBackdrop();

            // input on the panel content
            View.Root.PointerPressed += OnPointerPressed;
            View.Root.PointerMoved += OnPointerMoved;
            View.Root.PointerReleased += OnPointerReleased;
            View.Root.PointerCaptureLost += (_, _) => EndDrag(false);
            View.Root.RightTapped += (_, e) => { e.Handled = true; MenuRequested?.Invoke(); };
            View.Root.DoubleTapped += (_, e) => { e.Handled = true; DoubleClicked?.Invoke(); };

            Hook.Add((msg, w, l) =>
            {
                if (msg == Native.WM_DISPLAYCHANGE) { _displayTimer!.Stop(); _displayTimer.Start(); }   // restore position after the screens settle
                if (msg == Native.WM_DPICHANGED) DpiChanged?.Invoke();
                return null;
            });

            var dq = DispatcherQueue;
            _autoHideTimer = dq.CreateTimer();
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(250);
            _autoHideTimer.Tick += (_, _) => CheckAutoHide();
            _topMostTimer = dq.CreateTimer();
            _topMostTimer.Interval = TimeSpan.FromSeconds(10);
            _topMostTimer.Tick += (_, _) => { if (IsVisible) RefreshTopMost(false); };
            _topMostTimer.Start();
            _displayTimer = dq.CreateTimer();
            _displayTimer.Interval = TimeSpan.FromMilliseconds(500);
            _displayTimer.IsRepeating = false;
            _displayTimer.Tick += (_, _) => RestorePosition();
        }

        public event Action? DpiChanged;

        public double Scale => Native.Scale(Hwnd);
        public bool IsVisible => Native.IsWindowVisible(Hwnd);

        // ------------------------------------------------------------------ size & look
        /// <summary>Resizes the window to the panel's size (DIPs), keeping the top-left corner.</summary>
        public void SetContentSize(double widthDip, double heightDip)
        {
            double s = Scale;
            int w = Math.Max(1, (int)Math.Ceiling(widthDip * s));
            int h = Math.Max(1, (int)Math.Ceiling(heightDip * s));
            if (w == _widthPx && h == _heightPx) return;
            _widthPx = w; _heightPx = h;
            AppWindow.Resize(new SizeInt32(w, h));
        }

        public void ApplyAttributes()
        {
            View.Root.Opacity = Math.Clamp(_cfg.Opacity, 0.1, 1.0);
            SetClickThrough(_cfg.ClickThrough);
            RefreshTopMost(true);
            if (_cfg.AutoHide) _autoHideTimer.Start();
            else
            {
                _autoHideTimer.Stop();
                if (_hidden) ForceShow();
            }
        }

        public void RefreshTopMost(bool force)
        {
            bool isTop = (Native.ExStyle(Hwnd) & Native.WS_EX_TOPMOST) != 0;
            const uint f = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            if (_cfg.TopMost)
            {
                if (isTop && !force) return;
                if (force) Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, f);   // re-insert at the top of the topmost band
                Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, f);
            }
            else if (isTop || force)
            {
                Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, f);
            }
        }

        /// <summary>Clicks go to the window underneath (needs a layered window with full alpha).</summary>
        public void SetClickThrough(bool on)
        {
            if (on == _clickThrough) return;
            _clickThrough = on;
            long ex = Native.ExStyle(Hwnd);
            if (on)
            {
                Native.SetExStyle(Hwnd, ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
                Native.SetLayeredWindowAttributes(Hwnd, 0, 255, 0x2 /* LWA_ALPHA */);
            }
            else
            {
                Native.SetExStyle(Hwnd, ex & ~(Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED));
            }
        }

        public void ShowPanel(bool activate)
        {
            if (activate) Activate();
            else Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
            RefreshTopMost(true);
            KeepVisible(3);
        }

        public void HidePanel() => Native.ShowWindow(Hwnd, Native.SW_HIDE);

        // ------------------------------------------------------------------ dragging
        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(View.Root);
            if (!pt.Properties.IsLeftButtonPressed) return;
            _pressed = true;
            _dragCursor = Native.Cursor();
            var b = Native.Bounds(Hwnd);
            _dragWindow = (b.Left, b.Top);
            View.Root.CapturePointer(e.Pointer);
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed) return;
            var c = Native.Cursor();
            int dx = c.X - _dragCursor.X, dy = c.Y - _dragCursor.Y;
            if (dx == 0 && dy == 0) return;
            Native.Move(Hwnd, _dragWindow.X + dx, _dragWindow.Y + dy);
            _hidden = false;
            _dock = Edge.None;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            View.Root.ReleasePointerCapture(e.Pointer);
            EndDrag(true);
        }

        private void EndDrag(bool save)
        {
            if (!_pressed) return;
            _pressed = false;
            if (save)
            {
                ClampToScreen(false);
                SavePosition();
            }
        }

        // ------------------------------------------------------------------ position
        public void KeepVisible(double seconds) => _keepVisibleUntil = DateTime.Now.AddSeconds(seconds);

        public void ClampToScreen(bool force)
        {
            if (!_cfg.ClampToScreen && !force) return;
            var b = Native.Bounds(Hwnd);
            var area = Native.ScreenFromPoint(b.Left + b.Width / 2, b.Top + b.Height / 2).WorkArea;
            int x = b.Left, y = b.Top;
            if (x < area.Left) x = area.Left;
            if (x + b.Width > area.Right) x = area.Right - b.Width;
            if (y < area.Top) y = area.Top;
            if (y + b.Height > area.Bottom) y = area.Bottom - b.Height;
            if (x != b.Left || y != b.Top) Native.Move(Hwnd, x, y);
        }

        public void SavePosition()
        {
            if (_hidden) return;   // an edge-hidden panel keeps its last real position
            ClampToScreen(false);
            var b = Native.Bounds(Hwnd);
            _cfg.ScreenDevice = Native.ScreenFromPoint(b.Left + b.Width / 2, b.Top + b.Height / 2).DeviceName;
            _cfg.Position = new ScreenPoint(b.Left, b.Top);
            _cfg.Save();
        }

        public void RestorePosition()
        {
            var b = Native.Bounds(Hwnd);
            int w = b.Width, h = b.Height;
            var saved = string.IsNullOrEmpty(_cfg.ScreenDevice) ? null
                : Native.AllScreens().FirstOrDefault(s => s.DeviceName == _cfg.ScreenDevice);
            int x, y;
            Native.RECT area;
            if (saved != null && _cfg.Position.X != -1)
            {
                area = saved.WorkArea;
                x = _cfg.Position.X; y = _cfg.Position.Y;
            }
            else
            {
                area = Native.ScreenFromPoint(b.Left, b.Top).WorkArea;
                if (_cfg.Position.X >= 0) { x = _cfg.Position.X; y = _cfg.Position.Y; }
                else { x = area.Right - w - 50; y = area.Top + (area.Height - h) / 2; }   // first start: right side, centred
            }
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + w > area.Right) x = area.Right - w;
            if (y + h > area.Bottom) y = area.Bottom - h;
            Native.Move(Hwnd, x, y);
            _hidden = false;
            _dock = Edge.None;
            KeepVisible(0.5);
        }

        /// <summary>Moves the window so its centre stays put (used when switching layouts).</summary>
        public (int X, int Y) Center
        {
            get { var b = Native.Bounds(Hwnd); return (b.Left + b.Width / 2, b.Top + b.Height / 2); }
        }

        public void CenterOn((int X, int Y) c)
        {
            var b = Native.Bounds(Hwnd);
            Native.Move(Hwnd, c.X - b.Width / 2, c.Y - b.Height / 2);
            SavePosition();
        }

        // ------------------------------------------------------------------ auto-hide at screen edges
        public void ForceShow()
        {
            if (_hidden)
            {
                _hidden = false;
                _dock = Edge.None;
                ClampToScreen(true);
            }
            KeepVisible(3);
        }

        private void CheckAutoHide()
        {
            if (!_cfg.AutoHide || !IsVisible || _pressed || PopupMenu.IsOpen) return;
            if (DateTime.Now < _keepVisibleUntil) return;

            var b = Native.Bounds(Hwnd);
            var area = Native.ScreenFromPoint(b.Left + b.Width / 2, b.Top + b.Height / 2).WorkArea;
            var c = Native.Cursor();

            if (!_hidden)
            {
                bool nearLeft = b.Left <= area.Left + HideThreshold;
                bool nearRight = area.Right - b.Right <= HideThreshold;
                bool nearTop = b.Top <= area.Top + HideThreshold;
                if ((nearLeft || nearRight || nearTop) && !b.Contains(c.X, c.Y))
                {
                    if (nearRight) { Native.Move(Hwnd, area.Right - HideStrip, b.Top); _dock = Edge.Right; }
                    else if (nearLeft) { Native.Move(Hwnd, area.Left - (b.Width - HideStrip), b.Top); _dock = Edge.Left; }
                    else { Native.Move(Hwnd, b.Left, area.Top - (b.Height - HideStrip)); _dock = Edge.Top; }
                    _hidden = true;
                }
                return;
            }

            bool onStrip = _dock switch
            {
                Edge.Right => c.X >= area.Right - HideStrip && c.Y >= b.Top && c.Y <= b.Bottom,
                Edge.Left => c.X <= area.Left + HideStrip && c.Y >= b.Top && c.Y <= b.Bottom,
                Edge.Top => c.Y <= area.Top + HideStrip && c.X >= b.Left && c.X <= b.Right,
                _ => false,
            };
            if (!onStrip) return;
            if (_dock == Edge.Right && c.X >= area.Right - HoverBand) Native.Move(Hwnd, area.Right - b.Width, b.Top);
            else if (_dock == Edge.Left && c.X <= area.Left + HoverBand) Native.Move(Hwnd, area.Left, b.Top);
            else if (_dock == Edge.Top && c.Y <= area.Top + HoverBand) Native.Move(Hwnd, b.Left, area.Top);
            else return;
            _hidden = false;
            _dock = Edge.None;
        }
    }
}
