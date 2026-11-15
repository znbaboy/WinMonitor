using System;
using System.IO;
using System.Reflection;

namespace WinMonitor
{
    internal static class AppInfo
    {
        public const string Name = "WinMonitor";
        public const string RepoUrl = "https://github.com/znbaboy/WinMonitor";
        public const string IssuesUrl = RepoUrl + "/issues";
        /// <summary>The folder WinMonitor.exe is in (the app itself runs from a temporary copy of its files).</summary>
        public static readonly string ExeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        /// <summary>The "settings" folder next to the exe: settings, history, logs and your own themes.</summary>
        public static readonly string DataDir = InitDataDir();

        private static string InitDataDir()
        {
            string dir = Path.Combine(ExeDir, "settings");
            try
            {
                Directory.CreateDirectory(dir);
                // move files from older versions, which kept them next to the exe
                foreach (var name in new[] { "settings.json", "settings.json.bak", "TrafficHistory.json", "TrafficHistory.json.bak",
                                             "HardwareHistory.json", "HardwareHistory.json.bak", "WinMonitor_Error.log" })
                {
                    string old = Path.Combine(ExeDir, name), now = Path.Combine(dir, name);
                    if (File.Exists(old) && !File.Exists(now)) File.Move(old, now);
                }
                string oldThemes = Path.Combine(ExeDir, "resources", "themes");
                if (Directory.Exists(oldThemes))
                {
                    string themes = Path.Combine(dir, "themes");
                    Directory.CreateDirectory(themes);
                    foreach (var f in Directory.GetFiles(oldThemes, "*.json"))
                    {
                        string to = Path.Combine(themes, Path.GetFileName(f));
                        if (!File.Exists(to)) File.Move(f, to);
                    }
                }
            }
            catch { }
            return dir;
        }

        public static string Version =>
            typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    }
}
