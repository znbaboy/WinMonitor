using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinMonitor.Core;

namespace WinMonitor
{
    public static class SettingsHelper
    {
        // Cache path
        private static readonly string _cachedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        private static readonly string _backupPath = Path.Combine(AppContext.BaseDirectory, "settings.json.bak");
        private static readonly string _tempPath = Path.Combine(AppContext.BaseDirectory, "settings.json.tmp");
        private static readonly object _ioLock = new object();
        public static string FilePath => _cachedPath;

        // Global block save lock
        public static bool GlobalBlockSave { get; set; } = false;

        public static Settings Load(bool forceReload = false)
        {
            // Note: The singleton instance management is kept in Settings.Load() facade
            // or handled by the caller. This method strictly loads from disk/creates default.
            
            Settings s;
            lock (_ioLock)
            {
                s = TryLoadFile(FilePath) ?? TryLoadFile(_backupPath) ?? new Settings();
            }

            if (s.GroupAliases == null) s.GroupAliases = new Dictionary<string, string>();

            // 1. Check if new install
            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
            }
            else
            {
                // 2. Add monitor items introduced since this config was saved
                // (configs older than LiteMonitor 1.2 are not rebuilt)
                s.CheckAndAppendMissingItems();
            }

            // Removed redundant SyncToLanguage call. 
            // Caller (AppActions.ApplyAllSettings) is responsible for syncing language state.
            // s.SyncToLanguage();
            
            // actions 3 (traffic history) and 5 (web display) no longer exist
            if (s.MainFormDoubleClickAction is not (1 or 2 or 5)) s.MainFormDoubleClickAction = 2;   // removed actions open Settings
            if (s.Skin == "Dark") s.Skin = ThemeManager.DarkDefault;                              // build 4 settings
            else if (s.Skin == "Light") s.Skin = ThemeManager.LightDefault;
            if (s.Skin != ThemeManager.SystemMode && !File.Exists(Path.Combine(ThemeManager.ThemeDir, s.Skin + ".json")))
                s.Skin = ThemeManager.SystemMode;                                                 // theme file missing: follow Windows

            s.InternAllStrings();
            
            return s;
        }

        public static void Save(this Settings settings)
        {
            if (GlobalBlockSave) return;

            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                lock (_ioLock)
                {
                    AtomicWrite(json);
                }
            }
            catch
            {
                CleanupTempFile();
            }
        }

        public static void DeleteStoredSettings()
        {
            lock (_ioLock)
            {
                DeleteFileIfExists(FilePath);
                DeleteFileIfExists(_backupPath);
                DeleteFileIfExists(_tempPath);
            }
        }

        private static Settings? TryLoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch
            {
                return null;
            }
        }

        private static void AtomicWrite(string json)
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tempPath, json);

            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, _backupPath, true); }
                catch { }
            }

            File.Move(_tempPath, FilePath, true);
        }

        private static void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch { }
        }

        private static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public static void InitDefaultItems(this Settings settings)
        {
            settings.MonitorItems = new List<MonitorItemConfig>
            {
                // Dashboard Items
                // Dashboard items go to the end by default (SortIndex > 800)
                new MonitorItemConfig { Key = "DASH.HOST", SortIndex = 101, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Time", SortIndex = 102, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Uptime", SortIndex = 103, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.IP",   SortIndex = 104, VisibleInPanel = false, TaskbarLabel = " " },

                // wireless device batteries, one row per device type
                new MonitorItemConfig { Key = "HALO.Mouse",      SortIndex = 151, VisibleInPanel = true },
                new MonitorItemConfig { Key = "HALO.Keyboard",   SortIndex = 152, VisibleInPanel = true },
                new MonitorItemConfig { Key = "HALO.Headset",    SortIndex = 153, VisibleInPanel = false },
                new MonitorItemConfig { Key = "HALO.Controller", SortIndex = 154, VisibleInPanel = true },
                new MonitorItemConfig { Key = "HALO.Bluetooth",  SortIndex = 155, VisibleInPanel = false },
               
                // CPU
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 201, VisibleInPanel = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 202, VisibleInPanel = true },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 203, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 204, VisibleInPanel = false },
                // CPU Voltage
                new MonitorItemConfig { Key = "CPU.Voltage", SortIndex = 205, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Fan",   SortIndex = 206, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Pump",  SortIndex = 207, VisibleInPanel = false },

                // GPU
                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 301, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 302, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Clock", SortIndex = 303, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 304, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Fan",   SortIndex = 305, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 306, VisibleInPanel = true },

                // HOST (MEM, MOBO, DISK Temp, CASE Fan)
                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 401, VisibleInPanel = true },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 403, VisibleInPanel = false },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 404, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CASE.Fan",  SortIndex = 405, VisibleInPanel = false },
                
                // BATTERY (New Group)
                new MonitorItemConfig { Key = "BAT.Percent", SortIndex = 501, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Power",   SortIndex = 502, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Voltage", SortIndex = 503, VisibleInPanel = false },
                new MonitorItemConfig { Key = "BAT.Current", SortIndex = 504, VisibleInPanel = false },

                // DISK IO
                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 601, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 602, VisibleInPanel = true },

                // NET
                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 701, VisibleInPanel = true },
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 702, VisibleInPanel = true },

                // DATA
                new MonitorItemConfig { Key = "DATA.DayUp",  SortIndex = 801, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayDown",SortIndex = 802, VisibleInPanel = true },
            };
        }
        // Push renamed groups and items to the language manager
        // (clears all current overrides first)
        public static void SyncToLanguage(this Settings settings)
        {
            LanguageManager.ClearOverrides();
            if (settings.GroupAliases != null)
            {
                foreach (var kv in settings.GroupAliases)
                    LanguageManager.SetOverride(UIUtils.Intern("Groups." + kv.Key), kv.Value);
            }
            if (settings.MonitorItems != null)
            {
                foreach (var item in settings.MonitorItems)
                {
                    if (!string.IsNullOrEmpty(item.UserLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), item.UserLabel);
                    if (!string.IsNullOrEmpty(item.TaskbarLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), item.TaskbarLabel);
                }
            }
        }

        // Cache for standard keys to avoid repeated allocations
        private static readonly Lazy<HashSet<string>> _standardKeys = new Lazy<HashSet<string>>(() => 
        {
            var s = new Settings();
            s.InitDefaultItems();
            return new HashSet<string>(s.MonitorItems.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        });

        public static HashSet<string> GetStandardKeys() => _standardKeys.Value;

        public static void InternAllStrings(this Settings settings)
        {
            if (settings.MonitorItems != null)
            {
                // Remove Orphaned Items
                // Optimization: Use StandardKeys whitelist to avoid heavy allocations
                var whitelist = GetStandardKeys();
                var keysToRemove = new List<MonitorItemConfig>();

                foreach (var item in settings.MonitorItems)
                {
                    if (item == null) continue;
                    
                    item.Key = UIUtils.Intern(item.Key);
                    
                    // 1. Is it a standard item?
                    if (whitelist.Contains(item.Key)) continue;

                    // 2. A dashboard item? 
                    // Keep every DASH.* item
                    if (item.Key.StartsWith("DASH.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // keep all DASH.*
                    }
                    keysToRemove.Add(item);
                }
                
                foreach (var orphan in keysToRemove)
                {
                    settings.MonitorItems.Remove(orphan);
                }
            }

            settings.PreferredDisk = UIUtils.Intern(settings.PreferredDisk);
            settings.LastAutoDisk = UIUtils.Intern(settings.LastAutoDisk);
            settings.PreferredNetwork = UIUtils.Intern(settings.PreferredNetwork);
            settings.LastAutoNetwork = UIUtils.Intern(settings.LastAutoNetwork);
            settings.PreferredGpu = UIUtils.Intern(settings.PreferredGpu);
            
            settings.PreferredCpuFan = UIUtils.Intern(settings.PreferredCpuFan);
            settings.PreferredCpuPump = UIUtils.Intern(settings.PreferredCpuPump);
            settings.PreferredCaseFan = UIUtils.Intern(settings.PreferredCaseFan);
            settings.PreferredMoboTemp = UIUtils.Intern(settings.PreferredMoboTemp);

        }

        public static void CheckAndAppendMissingItems(this Settings settings)
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            var defaultList = temp.MonitorItems;
            
            // Find missing items (in default-list order)
            var newItems = defaultList
                .Where(std => !settings.MonitorItems.Any(usr => usr.Key.Equals(std.Key, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (newItems.Count == 0) return;

            bool listChanged = false;

            foreach (var newItem in newItems)
            {
                // Placement by neighbour:
                // Place a new item after its nearest neighbour in the default list (not at a fixed index),
                // so it follows wherever the user moved that neighbour.
                
                MonitorItemConfig? anchor = null;
                int myDefIdx = defaultList.FindIndex(x => x.Key == newItem.Key);
                
                // Walk back to the nearest existing neighbour
                for (int k = myDefIdx - 1; k >= 0; k--)
                {
                    var prevKey = defaultList[k].Key;
                    var existing = settings.MonitorItems.FirstOrDefault(x => x.Key.Equals(prevKey, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        anchor = existing;
                        break;
                    }
                }

                // === A. Panel SortIndex ===
                int targetIndex;
                if (anchor != null)
                {
                    targetIndex = anchor.SortIndex + 1;
                }
                else
                {
                    // No neighbour (first item): insert before the current first
                    int min = settings.MonitorItems.Count > 0 ? settings.MonitorItems.Min(x => x.SortIndex) : 0;
                    targetIndex = min - 1;
                }

                // Shift the following items
                foreach (var item in settings.MonitorItems.Where(x => x.SortIndex >= targetIndex))
                    item.SortIndex++;
                
                newItem.SortIndex = targetIndex;

                settings.MonitorItems.Add(newItem);
                listChanged = true;
            }

            if (listChanged)
            {
                settings.MonitorItems = settings.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            }
        }

        public static bool IsAnyEnabled(this Settings settings, string keyPrefix)
        {
            return settings.MonitorItems.Any(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && settings.IsShown(x));
        }

        public static void UpdateMaxRecord(this Settings settings, string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            if (key == "CPU.Power" && val > settings.RecordedMaxCpuPower) { settings.RecordedMaxCpuPower = val; changed = true; }
            else if (key == "CPU.Clock" && val > settings.RecordedMaxCpuClock) { settings.RecordedMaxCpuClock = val; changed = true; }
            else if (key == "GPU.Power" && val > settings.RecordedMaxGpuPower) { settings.RecordedMaxGpuPower = val; changed = true; }
            else if (key == "GPU.Clock" && val > settings.RecordedMaxGpuClock) { settings.RecordedMaxGpuClock = val; changed = true; }
            
            else if (key == "CPU.Fan" && val > settings.RecordedMaxCpuFan) { settings.RecordedMaxCpuFan = val; changed = true; }
            else if (key == "CPU.Pump" && val > settings.RecordedMaxCpuPump) { settings.RecordedMaxCpuPump = val; changed = true; }
            else if (key == "GPU.Fan" && val > settings.RecordedMaxGpuFan) { settings.RecordedMaxGpuFan = val; changed = true; }
            else if (key == "CASE.Fan" && val > settings.RecordedMaxChassisFan) { settings.RecordedMaxChassisFan = val; changed = true; }
            
            if (changed && (DateTime.Now - settings.LastAutoSaveTime).TotalSeconds > 30)
            {
                settings.Save();
                settings.LastAutoSaveTime = DateTime.Now;
            }
        }
    }
}
