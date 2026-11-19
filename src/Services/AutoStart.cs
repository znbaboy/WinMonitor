using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Security.Principal;
using System.Xml.Linq;

namespace WinMonitor.Services
{
    public static class AutoStart
    {
        private const string TaskName = "WinMonitor_AutoStart";
        private const string TaskPriority = "5";   // normal priority (the Task Scheduler default, 7, runs the app below normal)

        public static void Set(bool enabled)
        {
            string exePath = Process.GetCurrentProcess().MainModule!.FileName!;

            // 1. Scheduled tasks cannot run from a network drive
            try
            {
                string root = Path.GetPathRoot(exePath)!;
                if ((!string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network) || new Uri(exePath).IsUnc)
                {
                    Dialogs.Warn("Start with Windows does not work from a network drive.\nMove WinMonitor to a local drive.");
                    return;
                }
            }
            catch { }

            if (enabled)
            {
                if (IsEnabledForCurrentExe(exePath)) return;

                // A task XML is the only way to get both: no PowerShell errors, and starting on battery
                string tempXmlPath = Path.Combine(Path.GetTempPath(), $"WinMonitor_Task_{Guid.NewGuid()}.xml");

                try
                {
                    // Build the XML
                    var doc = GetTaskXml(exePath);
                    
                    // Write to a temporary file (XDocument writes UTF-16)
                    doc.Save(tempXmlPath);

                    // Import it with schtasks
                    // /F: overwrite
                    // /TN: task name
                    // /XML: the task definition
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Create /TN \"{TaskName}\" /XML \"{tempXmlPath}\" /F",
                        CreateNoWindow = true,
                        UseShellExecute = false // needed for CreateNoWindow
                    };
                    
                    startInfo.RedirectStandardError = true;
                    using (var p = Process.Start(startInfo))
                    {
                        string err = p?.StandardError.ReadToEnd() ?? "";
                        p?.WaitForExit();
                        if (p == null || p.ExitCode != 0)
                            Dialogs.Error("Could not set up Start with Windows." + (string.IsNullOrWhiteSpace(err) ? "" : "\n\n" + err.Trim()));
                    }
                }
                catch (Exception ex)
                {
                    // any IO or process error
                    Dialogs.Error($"Could not change Start with Windows: {ex.Message}");
                }
                finally
                {
                    // Always try to remove the temporary file
                    try
                    {
                        if (File.Exists(tempXmlPath)) File.Delete(tempXmlPath);
                    }
                    catch { /* a leftover temporary file is harmless */ }
                }
            }
            else
            {
                if (!IsEnabled()) return;
                // Delete the task
                var startInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Delete /TN \"{TaskName}\" /F",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var p = Process.Start(startInfo))
                {
                    p?.WaitForExit();
                }
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static bool IsEnabledForCurrentExe(string exePath)
        {
            try
            {
                var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\" /XML")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;

                    string xml = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) return false;

                    var doc = XDocument.Parse(xml);
                    var command = doc.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "Command")
                        ?.Value;
                    var enabled = doc.Descendants()
                        .FirstOrDefault(x => x.Name.LocalName == "Enabled" &&
                                             x.Parent?.Name.LocalName == "Settings")
                        ?.Value;

                    // A task disabled by the user or Windows must be re-created even if its command is unchanged.
                    bool taskEnabled = !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
                    string? priority = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Priority")?.Value;
                    return taskEnabled && priority == TaskPriority && string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The task XML: start at sign-in with highest rights, also on battery, short delay.
        /// </summary>
        private static XDocument GetTaskXml(string exePath)
        {
            // Working directory: the app's folder
            string exeDir = Path.GetDirectoryName(exePath)!;

            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

            // XDocument handles escaping (&, ' in paths)
            // and the UTF-16 declaration
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-16", null),
                new XElement(ns + "Task",
                    new XAttribute("version", "1.2"),
                    new XElement(ns + "RegistrationInfo",
                        new XElement(ns + "Description", "WinMonitor Auto Start")
                    ),
                    new XElement(ns + "Triggers",
                        new XElement(ns + "LogonTrigger",
                            new XElement(ns + "Enabled", "true"),
                            new XElement(ns + "Delay", "PT5S")
                        )
                    ),
                    new XElement(ns + "Principals",
                        new XElement(ns + "Principal",
                            new XAttribute("id", "Author"),
                            new XElement(ns + "LogonType", "InteractiveToken"),
                            new XElement(ns + "RunLevel", "HighestAvailable")
                        )
                    ),
                    new XElement(ns + "Settings",
                        new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                        new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                        new XElement(ns + "StopIfGoingOnBatteries", "false"),
                        new XElement(ns + "AllowHardTerminate", "true"),
                        new XElement(ns + "StartWhenAvailable", "false"),
                        new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                        new XElement(ns + "IdleSettings",
                            new XElement(ns + "StopOnIdleEnd", "true"),
                            new XElement(ns + "RestartOnIdle", "false")
                        ),
                        new XElement(ns + "AllowStartOnDemand", "true"),
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "Hidden", "false"),
                        new XElement(ns + "RunOnlyIfIdle", "false"),
                        new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                        new XElement(ns + "Priority", TaskPriority)
                    ),
                    new XElement(ns + "Actions",
                        new XAttribute("Context", "Author"),
                        new XElement(ns + "Exec",
                            new XElement(ns + "Command", exePath),
                            new XElement(ns + "WorkingDirectory", exeDir)
                        )
                    )
                )
            );

            return doc;
        }

    }
}
