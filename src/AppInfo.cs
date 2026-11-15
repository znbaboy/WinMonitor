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

        /// <summary>settings.json, next to the exe (created on first start).</summary>
        public static string SettingsFile => Path.Combine(ExeDir, "settings.json");

        /// <summary>A hidden ".winmonitor" folder next to the exe: history, traffic, logs, backups and your own themes.</summary>
        public static readonly string DataDir = InitDataDir();

        private static string InitDataDir()
        {
            string dir = Path.Combine(ExeDir, ".winmonitor");
            try
            {
                var info = Directory.CreateDirectory(dir);
                info.Attributes |= FileAttributes.Hidden;

                // tidy up older layouts: everything next to the exe, or a visible "settings" folder
                string oldFolder = Path.Combine(ExeDir, "settings");
                void Move(string from, string to) { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); }
                Move(Path.Combine(oldFolder, "settings.json"), SettingsFile);
                foreach (var name in new[] { "settings.json.bak", "TrafficHistory.json", "TrafficHistory.json.bak",
                                             "HardwareHistory.json", "HardwareHistory.json.bak", "WinMonitor_Error.log" })
                {
                    Move(Path.Combine(ExeDir, name), Path.Combine(dir, name));
                    Move(Path.Combine(oldFolder, name), Path.Combine(dir, name));
                }
                foreach (var oldThemes in new[] { Path.Combine(ExeDir, "resources", "themes"), Path.Combine(oldFolder, "themes") })
                {
                    if (!Directory.Exists(oldThemes)) continue;
                    string themes = Path.Combine(dir, "themes");
                    Directory.CreateDirectory(themes);
                    foreach (var f in Directory.GetFiles(oldThemes, "*.json")) Move(f, Path.Combine(themes, Path.GetFileName(f)));
                }
                foreach (var d in new[] { Path.Combine(oldFolder, "themes"), oldFolder })
                    if (Directory.Exists(d) && Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
            }
            catch { }
            return dir;
        }

        public static string Version =>
            typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    }
}
