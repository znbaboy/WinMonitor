// Optional components, always downloaded from their official releases and checked against a pinned SHA-256:
// - PawnIO driver (CPU temperature / clock / power): https://github.com/namazso/PawnIO.Setup
// - PresentMon (FPS):                                 https://github.com/GameTechDev/PresentMon (MIT)
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    public class DriverInstaller
    {
        private static readonly Version RequiredPawnIOVersion = new(2, 2, 0, 0);
        private static readonly Version SafeInPlaceUpgradeVersion = new(2, 1, 0, 0);

        private const string PawnIOUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe";
        private const string PawnIOSha256 = "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032";
        private const string PresentMonUrl = "https://github.com/GameTechDev/PresentMon/releases/download/v2.6.0/PresentMon-2.6.0-x64.exe";
        private const string PresentMonSha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af";

        private const int ErrorAlreadyExists = 183;
        private const int ErrorSuccessRebootRequired = 3010;
        private const int ErrorServiceMarkedForDelete = 1072;

        /// <summary>Where PresentMon is kept (next to the app, so the app stays portable).</summary>
        public static string PresentMonPath => Path.Combine(AppContext.BaseDirectory, "resources", "PresentMon.exe");

        private readonly Settings _cfg;
        private readonly Action? _onReloadRequired;        // reload the sensors after a driver install
        private readonly Action? _onPrepareDriverInstall;  // release the sensors before a driver install

        private static readonly SemaphoreSlim Lock = new(1, 1);
        private static bool _startupSawPendingDriver;
        private static bool _restartRequired;
        private static bool _presentMonAsked;

        public DriverInstaller(Settings cfg, Action? onReloadRequired, Action? onPrepareDriverInstall)
        {
            _cfg = cfg;
            _onReloadRequired = onReloadRequired;
            _onPrepareDriverInstall = onPrepareDriverInstall;
        }

        // =====================================================================
        // Startup: install / update PawnIO before the sensors are opened.
        // Returns false when the app should exit (Windows must restart first).
        // =====================================================================
        public static bool CheckPawnIOBeforeHardware(Settings cfg)
        {
            var di = new DriverInstaller(cfg, null, null);
            var info = GetPawnIOInfo();
            if (!di.PawnIONeeded(info)) return true;
            _startupSawPendingDriver = true;
            var result = di.InstallPawnIO(info, startup: true).GetAwaiter().GetResult();
            return result != InstallResult.ExitApp;
        }

        // =====================================================================
        // Runtime: called after startup and when monitor items change.
        // =====================================================================
        public async Task SmartCheckDriver()
        {
            await Lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var info = GetPawnIOInfo();
                // a driver that was skipped or failed at startup is not asked about again in this session
                if (PawnIONeeded(info) && !_startupSawPendingDriver && !_restartRequired && !IsLegacy(info))
                {
                    _startupSawPendingDriver = true;
                    await InstallPawnIO(info, startup: false).ConfigureAwait(false);
                }
                if (_cfg.IsAnyEnabled("FPS") && !IsValidFile(PresentMonPath, PresentMonSha256))
                    await DownloadPresentMon(ask: true).ConfigureAwait(false);
            }
            finally { Lock.Release(); }
        }

        /// <summary>Makes sure PresentMon is there (the FPS counter calls this from its own thread).</summary>
        public async Task<bool> CheckAndDownloadPresentMon(bool silent = false)
        {
            if (IsValidFile(PresentMonPath, PresentMonSha256)) return true;
            await Lock.WaitAsync().ConfigureAwait(false);
            try { return await DownloadPresentMon(ask: true).ConfigureAwait(false); }
            finally { Lock.Release(); }
        }

        // =====================================================================
        // PresentMon
        // =====================================================================
        private async Task<bool> DownloadPresentMon(bool ask)
        {
            if (IsValidFile(PresentMonPath, PresentMonSha256)) return true;
            if (ask)
            {
                if (_presentMonAsked) return false;   // asked once per session
                _presentMonAsked = true;
                if (!Dialogs.YesNo("FPS needs PresentMon (Intel, MIT licence, about 1 MB).\n\n" +
                                   "Download it now from github.com/GameTechDev/PresentMon?", "WinMonitor: FPS"))
                    return false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PresentMonPath)!);
                string tmp = PresentMonPath + ".download";
                await DownloadVerified(PresentMonUrl, PresentMonSha256, tmp).ConfigureAwait(false);
                File.Move(tmp, PresentMonPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Dialogs.Error("PresentMon download failed:\n" + ex.Message, "WinMonitor: FPS");
                return false;
            }
        }

        // =====================================================================
        // PawnIO
        // =====================================================================
        private enum InstallResult { Done, Skipped, Failed, ExitApp }

        private bool PawnIONeeded(PawnIOInfo info) =>
            _cfg.IsAnyEnabled("CPU") && (!info.Installed || (info.Version != null && info.Version < RequiredPawnIOVersion));

        private static bool IsLegacy(PawnIOInfo info) =>
            info.Installed && info.Version != null && info.Version < SafeInPlaceUpgradeVersion;

        private async Task<InstallResult> InstallPawnIO(PawnIOInfo info, bool startup)
        {
            string what = info.Installed
                ? $"Your PawnIO driver ({info.VersionText ?? "unknown version"}) is out of date. Version {RequiredPawnIOVersion.ToString(3)} is needed."
                : "WinMonitor needs the PawnIO driver to read CPU temperature, clock and power.";
            if (!Dialogs.YesNo(what + "\n\nDownload and install it now from github.com/namazso/PawnIO.Setup (about 3 MB)?",
                               "WinMonitor: PawnIO driver"))
                return InstallResult.Skipped;

            string setup = Path.Combine(Path.GetTempPath(), $"PawnIO_setup_{Guid.NewGuid():N}.exe");
            try
            {
                await DownloadVerified(PawnIOUrl, PawnIOSha256, setup).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Dialogs.Error("PawnIO download failed:\n" + ex.Message, "WinMonitor: PawnIO driver");
                return InstallResult.Failed;
            }

            try
            {
                _onPrepareDriverInstall?.Invoke();

                // drivers older than 2.1 cannot be upgraded in place: uninstall first
                if (IsLegacy(info))
                {
                    var un = await Uninstall(info).ConfigureAwait(false);
                    if (un == OpResult.ServiceMarkedForDelete || un == OpResult.RestartRequired)
                        return RestartNeeded(startup, "The old PawnIO driver is still being removed.");
                    if (un == OpResult.Failed)
                    {
                        Dialogs.Error("The old PawnIO driver could not be removed. Restart Windows and try again.", "WinMonitor: PawnIO driver");
                        return InstallResult.Failed;
                    }
                }

                var r = await RunElevated(setup, "-install -silent", checkReady: true).ConfigureAwait(false);
                if (r == OpResult.RestartRequired || r == OpResult.ServiceMarkedForDelete)
                    return RestartNeeded(startup, "PawnIO was installed.");
                if (r == OpResult.Success || r == OpResult.AlreadyExists || PawnIOReady())
                {
                    if (!startup) Dialogs.Info("PawnIO driver installed. CPU readings will come back in a moment.", "WinMonitor");
                    return InstallResult.Done;
                }

                // silent install failed: offer the installer's own window
                if (Dialogs.YesNo("The PawnIO driver could not be installed automatically.\n\nOpen the PawnIO installer?", "WinMonitor: PawnIO driver"))
                {
                    await RunElevated(setup, "", checkReady: true, silent: false).ConfigureAwait(false);
                    if (PawnIOReady()) return InstallResult.Done;
                }
                return InstallResult.Failed;
            }
            finally
            {
                try { File.Delete(setup); } catch { }
                _onReloadRequired?.Invoke();
            }
        }

        private InstallResult RestartNeeded(bool startup, string what)
        {
            _restartRequired = true;
            Dialogs.Info(what + "\nWindows needs to restart before CPU readings work.", "WinMonitor: restart required");
            return startup ? InstallResult.ExitApp : InstallResult.Done;
        }

        private static bool PawnIOReady()
        {
            var info = GetPawnIOInfo();
            return info.Installed && (info.Version == null || info.Version >= RequiredPawnIOVersion);
        }

        private sealed record PawnIOInfo(bool Installed, Version? Version, string? VersionText, string? UninstallCommand);

        private static PawnIOInfo GetPawnIOInfo()
        {
            bool installed = false;
            string? text = null;
            try
            {
                const string keyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(keyPath);
                    if (key == null) continue;
                    installed = true;
                    string? v = key.GetValue("DisplayVersion") as string;
                    text ??= v;
                    string? un = (key.GetValue("QuietUninstallString") ?? key.GetValue("UninstallString")) as string;
                    if (!string.IsNullOrWhiteSpace(v) && Version.TryParse(v.Trim(), out var ver))
                        return new PawnIOInfo(true, ver, v, un);
                }
            }
            catch { }
            return new PawnIOInfo(installed, null, text, null);
        }

        private enum OpResult { Failed, Success, AlreadyExists, RestartRequired, ServiceMarkedForDelete }

        private async Task<OpResult> Uninstall(PawnIOInfo info)
        {
            if (string.IsNullOrWhiteSpace(info.UninstallCommand) || !SplitCommand(info.UninstallCommand, out var file, out var args))
                return OpResult.Failed;
            if (!args.Contains("-silent", StringComparison.OrdinalIgnoreCase)) args = (args + " -silent").Trim();
            return await RunElevated(file, args, checkReady: false).ConfigureAwait(false);
        }

        private static bool SplitCommand(string cmd, out string file, out string args)
        {
            cmd = cmd.Trim();
            file = ""; args = "";
            if (cmd.Length == 0) return false;
            if (cmd[0] == '"')
            {
                int end = cmd.IndexOf('"', 1);
                if (end <= 1) return false;
                file = cmd[1..end];
                args = cmd[(end + 1)..].Trim();
                return true;
            }
            int sp = cmd.IndexOf(' ');
            if (sp < 0) { file = cmd; return true; }
            file = cmd[..sp];
            args = cmd[(sp + 1)..].Trim();
            return true;
        }

        private static async Task<OpResult> RunElevated(string file, string args, bool checkReady, bool silent = true)
        {
            for (int attempt = 0; attempt < (silent ? 3 : 1); attempt++)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = file,
                        Arguments = args,
                        UseShellExecute = true,
                        Verb = "runas",
                        WindowStyle = silent ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    });
                    if (p != null)
                    {
                        await p.WaitForExitAsync().ConfigureAwait(false);
                        if (silent)
                        {
                            switch (p.ExitCode)
                            {
                                case ErrorAlreadyExists: return OpResult.AlreadyExists;
                                case ErrorSuccessRebootRequired: return OpResult.RestartRequired;
                                case ErrorServiceMarkedForDelete: return OpResult.ServiceMarkedForDelete;
                                case 0 when !checkReady || PawnIOReady(): return OpResult.Success;
                            }
                        }
                    }
                }
                catch { }
                await Task.Delay(2000).ConfigureAwait(false);
                if (checkReady && PawnIOReady()) return OpResult.Success;
            }
            return OpResult.Failed;
        }

        // =====================================================================
        // Download + verification
        // =====================================================================
        private static async Task DownloadVerified(string url, string sha256, string dest)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WinMonitor");
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!hash.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file does not match the expected checksum, so it was not used.");
            await File.WriteAllBytesAsync(dest, bytes).ConfigureAwait(false);
        }

        private static bool IsValidFile(string path, string sha256)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(fs)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
