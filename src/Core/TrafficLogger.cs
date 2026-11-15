using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinMonitor.Core
{
    // === Data ===
    public class TrafficData
    {
        // Key format: "2023-10-27"
        public Dictionary<string, DailyRecord> History { get; set; } = new Dictionary<string, DailyRecord>();
    }

    public class DailyRecord
    {
        public long Upload { get; set; }
        public long Download { get; set; }
    }

    // === Store (static) ===
    public static class TrafficLogger
    {
        private static readonly string _filePath = Path.Combine(AppInfo.DataDir, "TrafficHistory.json");
        private static readonly string _backupPath = Path.Combine(AppInfo.DataDir, "TrafficHistory.json.bak");
        private static readonly string _tempPath = Path.Combine(AppInfo.DataDir, "TrafficHistory.json.tmp");
        private static readonly object _ioLock = new object(); // file lock
        private static readonly object _dataLock = new object(); // data lock
        // Cached values
        private static DateTime _cachedDate;
        private static string _cachedDateKey = "";

        public static TrafficData Data { get; private set; } = new TrafficData();

        // Load at startup
        public static void Load()
        {
            lock (_ioLock)
            {
                TrafficData loaded = TryLoadFile(_filePath) ?? TryLoadFile(_backupPath) ?? new TrafficData();
                loaded.History ??= new Dictionary<string, DailyRecord>();
                lock (_dataLock)
                {
                    Data = loaded;
                }
            }
        }

        private static TrafficData? TryLoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;

                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<TrafficData>(json) ?? new TrafficData();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrafficLogger] could not read the traffic history: {path}, {ex.Message}");
                return null;
            }
        }

        private static TrafficData CloneDataUnsafe()
        {
            Data.History ??= new Dictionary<string, DailyRecord>();
            return new TrafficData
            {
                History = Data.History.ToDictionary(
                    kv => kv.Key,
                    kv => new DailyRecord
                    {
                        Upload = kv.Value?.Upload ?? 0,
                        Download = kv.Value?.Download ?? 0
                    })
            };
        }

        private static void AtomicWrite(string json)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_tempPath, json);

            if (File.Exists(_filePath))
            {
                try { File.Copy(_filePath, _backupPath, true); }
                catch (Exception ex) { Debug.WriteLine($"[TrafficLogger] could not write the backup: {ex.Message}"); }
            }

            File.Move(_tempPath, _filePath, true);
        }

        private static void CleanupTempFile()
        {
            try
            {
                if (File.Exists(_tempPath)) File.Delete(_tempPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TrafficLogger] could not remove the temporary file: {ex.Message}");
            }
        }

        private static string SerializeSnapshot()
        {
            var opt = new JsonSerializerOptions { WriteIndented = true };
            lock (_dataLock)
            {
                return JsonSerializer.Serialize(CloneDataUnsafe(), opt);
            }
        }

        private static DailyRecord GetOrCreateTodayRecordUnsafe(string key)
        {
            Data.History ??= new Dictionary<string, DailyRecord>();
            if (!Data.History.TryGetValue(key, out var record) || record == null)
            {
                record = new DailyRecord();
                Data.History[key] = record;
            }
            return record;
        }

        private static string GetTodayKey()
        {
            if (DateTime.Today != _cachedDate)
            {
                _cachedDate = DateTime.Today;
                _cachedDateKey = UIUtils.Intern(_cachedDate.ToString("yyyy-MM-dd"));
            }
            return _cachedDateKey;
        }

        private static void SaveInternal()
        {
            lock (_ioLock)
            {
                try
                {
                    string json = SerializeSnapshot();
                    AtomicWrite(json);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[TrafficLogger] could not save the traffic history: {ex.Message}");
                    CleanupTempFile();
                }
            }
        }

        // Save on exit
        public static void Save()
        {
            SaveInternal();
        }

        // Add traffic
        public static void AddTraffic(long upBytes, long downBytes)
        {
            string key = GetTodayKey();

            lock (_dataLock)
            {
                var record = GetOrCreateTodayRecordUnsafe(key);
                record.Upload += upBytes;
                record.Download += downBytes;
            }
        }

        // Today's totals (for the panel)
        public static (long up, long down) GetTodayStats()
        {
            string key = GetTodayKey();
            lock (_dataLock)
            {
                Data.History ??= new Dictionary<string, DailyRecord>();
                if (Data.History.TryGetValue(key, out var rec))
                {
                    return (rec.Upload, rec.Download);
                }
            }
            return (0, 0);
        }

        // Deletes one day
        public static void RemoveRecord(string dateKey)
        {
            bool changed;
            lock (_dataLock)
            {
                Data.History ??= new Dictionary<string, DailyRecord>();
                changed = Data.History.Remove(dateKey);
            }

            if (changed) Save(); // save straight away
        }

        // Deletes all history
        public static void ClearHistory()
        {
            lock (_dataLock)
            {
                Data.History ??= new Dictionary<string, DailyRecord>();
                Data.History.Clear();
            }
            Save();
        }
    }
}
