using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinMonitor.UI
{
    /// <summary>Win32 calls used by the windows (styles, position, monitors, DWM).</summary>
    internal static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_WNDPROC = -4;
        public const long WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L,
                          WS_SYSMENU = 0x00080000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L;
        public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_TOPMOST = 0x8,
                          WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

        public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
                          SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;

        public const uint WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_ERASEBKGND = 0x0014,
                          WM_DWMCOMPOSITIONCHANGED = 0x031E, WM_SETTINGCHANGE = 0x001A;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] public static extern IntPtr CallWindowProc(IntPtr prev, IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RECT r, IntPtr brush);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);

        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_BLURBEHIND { public uint dwFlags; public bool fEnable; public IntPtr hRgnBlur; public bool fTransitionOnMaximized; }
        [DllImport("dwmapi.dll")] public static extern int DwmEnableBlurBehindWindow(IntPtr h, ref DWM_BLURBEHIND b);

        public static long ExStyle(IntPtr h) => (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        public static void SetExStyle(IntPtr h, long v) => SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)v);
        public static long Style(IntPtr h) => (long)GetWindowLongPtr(h, GWL_STYLE);
        public static void SetStyle(IntPtr h, long v) => SetWindowLongPtr(h, GWL_STYLE, (IntPtr)v);

        public static double Scale(IntPtr h)
        {
            uint dpi = 0;
            try { dpi = GetDpiForWindow(h); } catch { }
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }

        public static (int X, int Y) Cursor()
        {
            GetCursorPos(out var p);
            return (p.X, p.Y);
        }

        public static RECT Bounds(IntPtr h)
        {
            GetWindowRect(h, out var r);
            return r;
        }

        public static void Move(IntPtr h, int x, int y) =>
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        /// <summary>Windows 11: DWM corner style (1 = square, 2 = round) and no border colour.</summary>
        public static void SetCorners(IntPtr h, int preference)
        {
            try
            {
                DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, 4);
                int none = unchecked((int)0xFFFFFFFE);
                DwmSetWindowAttribute(h, 34 /* DWMWA_BORDER_COLOR */, ref none, 4);
            }
            catch { }
        }

        /// <summary>Dark title bar for the normal windows.</summary>
        public static void SetDarkTitleBar(IntPtr h, bool dark)
        {
            try
            {
                int v = dark ? 1 : 0;
                DwmSetWindowAttribute(h, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref v, 4);
            }
            catch { }
        }

        // ---------------- monitors ----------------
        public sealed record Screen(string DeviceName, RECT Bounds, RECT WorkArea);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor, rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);

        private static Screen? Info(IntPtr mon)
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            return GetMonitorInfo(mon, ref mi) ? new Screen(mi.szDevice ?? "", mi.rcMonitor, mi.rcWork) : null;
        }

        public static List<Screen> AllScreens()
        {
            var list = new List<Screen>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
            {
                var s = Info(m);
                if (s != null) list.Add(s);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static Screen ScreenFromPoint(int x, int y) =>
            Info(MonitorFromPoint(new POINT { X = x, Y = y }, 2 /* MONITOR_DEFAULTTONEAREST */))
            ?? new Screen("", new RECT { Right = 1920, Bottom = 1080 }, new RECT { Right = 1920, Bottom = 1040 });
    }

    /// <summary>Hooks a window's message procedure. The handler returns a result to swallow a message.</summary>
    internal sealed class WndProcHook
    {
        private delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        private readonly WndProc _proc;   // kept alive for the native side
        private readonly IntPtr _old;
        private readonly List<Func<uint, IntPtr, IntPtr, IntPtr?>> _handlers = new();

        // Windows keeps calling the procedure until the window is gone; never let a hook be collected.
        private static readonly List<WndProcHook> Alive = new();

        public WndProcHook(IntPtr hwnd)
        {
            lock (Alive) Alive.Add(this);
            _proc = Proc;
            _old = Native.SetWindowLongPtr(hwnd, Native.GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
        }

        public void Add(Func<uint, IntPtr, IntPtr, IntPtr?> handler) => _handlers.Add(handler);

        private IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            foreach (var handler in _handlers)
            {
                try
                {
                    var r = handler(msg, w, l);
                    if (r.HasValue) return r.Value;
                }
                catch (Exception ex) { Program.Log(ex); }
            }
            return Native.CallWindowProc(_old, h, msg, w, l);
        }
    }
}
