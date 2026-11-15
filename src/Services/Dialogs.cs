using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinMonitor.UI;

namespace WinMonitor.Services
{
    /// <summary>
    /// Message dialogs in the Windows 11 style, callable from any thread.
    /// Pass the window's XamlRoot to show the dialog over that window.
    /// A plain message box is only used when the UI is not running (a crash before or after it).
    /// </summary>
    public static class Dialogs
    {
        /// <summary>The UI thread; set when the app starts.</summary>
        public static DispatcherQueue? UI;

        /// <summary>Set to skip every dialog (automated tests); questions then answer No.</summary>
        public static bool Silent = Environment.GetEnvironmentVariable("WINMONITOR_NO_DIALOG") == "1";

        public static void Info(string text, string title = "WinMonitor", XamlRoot? root = null) => _ = Show(title, text, "OK", null, root);
        public static void Warn(string text, string title = "WinMonitor", XamlRoot? root = null) => _ = Show(title, text, "OK", null, root);
        public static void Error(string text, string title = "WinMonitor", XamlRoot? root = null) => _ = Show(title, text, "OK", null, root);

        /// <summary>Asks a question without blocking; true when <paramref name="yes"/> was chosen.</summary>
        public static Task<bool> AskAsync(string text, string title, XamlRoot? root = null, string yes = "Yes", string no = "No") =>
            Show(title, text, yes, no, root);

        /// <summary>Asks and waits. For background threads (the UI thread must use <see cref="AskAsync"/>).</summary>
        public static bool YesNo(string text, string title = "WinMonitor", string yes = "Yes", string no = "No")
        {
            if (Silent) return false;
            if (UI == null || UI.HasThreadAccess) return NativeBox(text, title, MB_YESNO | MB_ICONQUESTION) == IDYES;
            return Show(title, text, yes, no, null).GetAwaiter().GetResult();
        }

        /// <summary>A plain message box, for when the UI may be gone (crash report).</summary>
        public static void NativeError(string text, string title) => NativeBox(text, title, MB_ICONERROR);

        private static Task<bool> Show(string title, string text, string primary, string? secondary, XamlRoot? root)
        {
            if (Silent) return Task.FromResult(false);
            var ui = UI;
            if (ui == null) return Task.FromResult(NativeBox(text, title, secondary == null ? MB_ICONINFO : MB_YESNO | MB_ICONQUESTION) == IDYES);

            var tcs = new TaskCompletionSource<bool>();
            bool queued = ui.TryEnqueue(async () =>
            {
                try { tcs.TrySetResult(await ModernDialog.ShowAsync(title, text, primary, secondary, root)); }
                catch (Exception ex)
                {
                    Program.Log(ex);
                    tcs.TrySetResult(NativeBox(text, title, secondary == null ? MB_ICONINFO : MB_YESNO | MB_ICONQUESTION) == IDYES);
                }
            });
            if (!queued) tcs.TrySetResult(false);
            return tcs.Task;
        }

        private const uint MB_YESNO = 0x4, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONINFO = 0x40,
                           MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        private const int IDYES = 6;

        private static int NativeBox(string text, string title, uint flags)
        {
            if (Silent) return 0;
            try { return MessageBoxW(IntPtr.Zero, text, title, flags | MB_SETFOREGROUND | MB_TOPMOST); }
            catch { return 0; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
    }
}
