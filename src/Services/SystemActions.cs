using System;
using System.Diagnostics;

namespace WinMonitor.Services
{
    /// <summary>The few system actions WinMonitor needs.</summary>
    public static class SystemActions
    {
        public static void OpenTaskManager()
        {
            try { Process.Start(new ProcessStartInfo("taskmgr") { UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Error("Could not open Task Manager: " + ex.Message); }
        }

        public static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Error("Could not open the link: " + ex.Message); }
        }

        /// <summary>Starts a new copy (it waits for this one to close) and exits.</summary>
        public static void RestartApplication()
        {
            try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = true }); }
            catch { return; }
            Environment.Exit(0);
        }
    }
}
