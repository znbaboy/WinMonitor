using System;
using System.Runtime.InteropServices;

namespace WinMonitor.Services
{
    /// <summary>Plain Windows message boxes: work from any thread, before or after the UI starts.</summary>
    public static class Dialogs
    {
        /// <summary>Set by the UI so boxes stay on top of the panel; optional.</summary>
        public static IntPtr Owner = IntPtr.Zero;

        /// <summary>Set to skip every box (automated tests); YesNo then answers No.</summary>
        public static bool Silent = Environment.GetEnvironmentVariable("WINMONITOR_NO_DIALOG") == "1";

        private const uint MB_OK = 0x0, MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20,
                           MB_ICONWARNING = 0x30, MB_ICONINFO = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        private const int IDYES = 6;

        public static void Info(string text, string title = "WinMonitor") => Show(text, title, MB_OK | MB_ICONINFO);
        public static void Warn(string text, string title = "WinMonitor") => Show(text, title, MB_OK | MB_ICONWARNING);
        public static void Error(string text, string title = "WinMonitor") => Show(text, title, MB_OK | MB_ICONERROR);
        public static bool YesNo(string text, string title = "WinMonitor") => Show(text, title, MB_YESNO | MB_ICONQUESTION) == IDYES;

        private static int Show(string text, string title, uint flags)
        {
            if (Silent) return 0;
            try { return MessageBoxW(Owner, text, title, flags | MB_SETFOREGROUND | MB_TOPMOST); }
            catch { return 0; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
