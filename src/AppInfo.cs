using System.Reflection;

namespace WinMonitor
{
    internal static class AppInfo
    {
        public const string Name = "WinMonitor";
        public const string RepoUrl = "https://github.com/znbaboy/WinMonitor";
        public const string IssuesUrl = RepoUrl + "/issues";
        public static string Version =>
            typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0";
    }
}
