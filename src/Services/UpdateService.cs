using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinMonitor.Services
{
    /// <summary>
    /// Checks the GitHub releases of WinMonitor and installs a newer version in place.
    /// New sensor support (LibreHardwareMonitor), device IDs and runtime fixes all ship inside WinMonitor.exe,
    /// so updating the app updates them too. The download is checked against the SHA-256 GitHub publishes.
    /// </summary>
    internal static class UpdateService
    {
        private const string LatestApi = "https://api.github.com/repos/znbaboy/WinMonitor/releases/latest";
        private const string AssetName = "WinMonitor-portable-x64.zip";

        public sealed record Release(Version Version, string Tag, string Notes, string PageUrl, string DownloadUrl, string Sha256);

        private static HttpClient Http(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinMonitor/" + AppInfo.Version);
            return http;
        }

        public static Version Current => ParseVersion(AppInfo.Version) ?? new Version(0, 0);

        private static Version? ParseVersion(string s)
        {
            s = s.Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s[..cut];
            return Version.TryParse(s, out var v) ? v : null;
        }

        /// <summary>The latest release when it is newer than this copy, otherwise null. Throws when GitHub cannot be reached.</summary>
        public static async Task<Release?> CheckAsync()
        {
            using var http = Http(TimeSpan.FromSeconds(20));
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestApi).ConfigureAwait(false));
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseVersion(tag);
            if (version == null || version <= Current) return null;

            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind != JsonValueKind.Object) return null;

            string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
            return new Release(version, tag,
                root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                root.GetProperty("html_url").GetString() ?? AppInfo.RepoUrl + "/releases",
                asset.GetProperty("browser_download_url").GetString() ?? "",
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "");
        }

        /// <summary>
        /// Downloads and verifies the release, swaps WinMonitor.exe (a running exe can be renamed, not deleted),
        /// then starts the new copy. The caller exits the app afterwards.
        /// </summary>
        public static async Task InstallAsync(Release r)
        {
            if (string.IsNullOrEmpty(r.Sha256))
                throw new InvalidDataException("GitHub did not publish a checksum for this download, so it was not installed. Download it from the release page instead.");

            using var http = Http(TimeSpan.FromMinutes(5));
            byte[] zip = await http.GetByteArrayAsync(r.DownloadUrl).ConfigureAwait(false);
            if (!Convert.ToHexString(SHA256.HashData(zip)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download does not match GitHub's checksum, so it was not installed.");

            string exe = Environment.ProcessPath!;
            string fresh = exe + ".new", old = exe + ".old";
            using (var archive = new ZipArchive(new MemoryStream(zip)))
            {
                var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("WinMonitor.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("The download does not contain WinMonitor.exe.");
                entry.ExtractToFile(fresh, overwrite: true);
            }

            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(fresh, exe); }
            catch { File.Move(old, exe); throw; }   // put the running copy back if the swap fails
        }

        /// <summary>Removes the previous version left behind by an update (it was still running at the time).</summary>
        public static void CleanUpOldVersion()
        {
            try
            {
                string old = Environment.ProcessPath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { }
        }
    }
}
