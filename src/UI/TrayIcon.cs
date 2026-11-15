using System;
using System.Runtime.InteropServices;

namespace WinMonitor.UI
{
    /// <summary>Notification-area icon: right click = menu, double click = show / hide the panel, balloons for alerts.</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private const uint WM_TRAY = 0x8001; // WM_APP + 1
        private static readonly uint WM_TASKBARCREATED = Native.RegisterWindowMessage("TaskbarCreated");

        private readonly IntPtr _hwnd;
        private readonly IntPtr _icon;
        private bool _added;
        private bool _wanted;

        public event Action? MenuRequested;
        public event Action? DoubleClicked;

        public TrayIcon(IntPtr hwnd, string icoPath)
        {
            _hwnd = hwnd;
            int size = GetSystemMetrics(49 /* SM_CXSMICON */);
            _icon = LoadImage(IntPtr.Zero, icoPath, 1 /* IMAGE_ICON */, size, size, 0x10 /* LR_LOADFROMFILE */);
        }

        private NOTIFYICONDATA Data() => new()
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uCallbackMessage = WM_TRAY,
            hIcon = _icon,
            szTip = "WinMonitor",
            szInfo = "",
            szInfoTitle = "",
        };

        public bool Visible
        {
            get => _wanted;
            set
            {
                _wanted = value;
                if (value && !_added) Add();
                else if (!value && _added) Remove();
            }
        }

        private void Add()
        {
            var d = Data();
            d.uFlags = 0x1 | 0x2 | 0x4; // NIF_MESSAGE | NIF_ICON | NIF_TIP
            _added = Shell_NotifyIcon(0 /* NIM_ADD */, ref d);
            d.uVersion = 4; // NOTIFYICON_VERSION_4
            Shell_NotifyIcon(4 /* NIM_SETVERSION */, ref d);
        }

        private void Remove()
        {
            var d = Data();
            Shell_NotifyIcon(2 /* NIM_DELETE */, ref d);
            _added = false;
        }

        /// <summary>A notification (shown as a Windows toast). Needs the icon to be visible.</summary>
        public void Notify(string title, string text, bool warning)
        {
            if (!_added) return;
            var d = Data();
            d.uFlags = 0x10; // NIF_INFO
            d.szInfoTitle = Trim(title, 63);
            d.szInfo = Trim(text, 255);
            d.dwInfoFlags = warning ? 0x2u /* NIIF_WARNING */ : 0x1u /* NIIF_INFO */;
            Shell_NotifyIcon(1 /* NIM_MODIFY */, ref d);
        }

        private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

        /// <summary>Call from the window procedure.</summary>
        public IntPtr? HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TASKBARCREATED) { _added = false; if (_wanted) Add(); return null; }   // Explorer restarted
            if (msg != WM_TRAY) return null;
            switch ((uint)((long)lParam & 0xFFFF))
            {
                case 0x7B:  // WM_CONTEXTMENU (right click, v4)
                case 0x205: // WM_RBUTTONUP
                    MenuRequested?.Invoke();
                    break;
                case 0x203: // WM_LBUTTONDBLCLK
                    DoubleClicked?.Invoke();
                    break;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_added) Remove();
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID, uFlags, uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState, dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    }
}
