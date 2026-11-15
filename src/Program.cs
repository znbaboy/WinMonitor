using System;
using System.IO;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinMonitor.Core;
using WinMonitor.Services;

namespace WinMonitor
{
    public static class Program
    {
        private static Mutex? _mutex;

        [STAThread]
        public static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

            // one instance per folder (two portable copies in different folders may run side by side)
            string folder = AppContext.BaseDirectory.TrimEnd('\\', '/').ToLowerInvariant();
            string name = "Global\\WinMonitor_" + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(folder)))[..16];
            _mutex = new Mutex(true, name, out bool first);
            if (!first && Array.IndexOf(args, "--restart") >= 0)
            {
                try { first = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }   // the old instance is closing
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                UI.Native.PostMessage((IntPtr)0xFFFF /* HWND_BROADCAST */, UI.AppHost.WM_SHOWAPP, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }

            try
            {
                LanguageManager.Load("en");
                var cfg = Settings.Load();
                cfg.SyncToLanguage();
                if (!DriverInstaller.CheckPawnIOBeforeHardware(cfg)) return 0;   // Windows must restart first

                WinRT.ComWrappersSupport.InitializeComWrappers();
                Application.Start(p =>
                {
                    var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(ctx);
                    new App();
                });
                return 0;
            }
            catch (Exception ex)
            {
                Report(ex);
                return 1;
            }
        }

        private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "WinMonitor_Error.log");

        /// <summary>Logs a non-fatal error next to the app.</summary>
        public static void Log(Exception? ex)
        {
            if (ex == null) return;
            try { File.AppendAllText(LogPath, $"{DateTime.Now:u}\r\n{ex}\r\n\r\n"); } catch { }
        }

        private static void Report(Exception? ex)
        {
            Log(ex);
            Dialogs.Error($"WinMonitor hit an error and has to close.\n\n{ex?.Message}\n\nDetails: {LogPath}", "WinMonitor crashed");
        }
    }
}
