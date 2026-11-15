using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WinMonitor.Core
{
    public static class LanguageManager
    {
        public static string CurrentLang { get; private set; } = "zh";
        private static Dictionary<string, string> _texts = new();

        // User overrides (renamed items and groups) win over the language file
        private static Dictionary<string, string> _overrides = new();

        private static string LangDir
        {
            get
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "resources/lang");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static void Load(string langCode)
        {
            // Skip reloading when the requested language is already loaded
            // (case-insensitive, e.g. "en" vs "EN")
            if (string.Equals(CurrentLang, langCode, StringComparison.OrdinalIgnoreCase) && _texts.Count > 0)
            {
                return;
            }

            try
            {
                var path = Path.Combine(LangDir, $"{langCode}.json");
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[LanguageManager] Missing lang file: {langCode}.json");
                    return;
                }
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                _texts = Flatten(doc.RootElement);
                CurrentLang = langCode;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LanguageManager] Load failed: {ex.Message}");
            }
        }

        // Inject / clear user overrides
        public static void SetOverride(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(value))
            {
                if (_overrides.ContainsKey(key)) _overrides.Remove(key);
            }
            else
            {
                _overrides[key] = value;
            }
        }

        public static void ClearOverrides() => _overrides.Clear();

        // The language file's text for a key, ignoring user overrides
        public static string GetOriginal(string key)
        {
            // 1. Language file
            if (_texts.TryGetValue(key, out var val)) return val;

            // 2. Not found: fall back to the last part of the key
            int dot = key.IndexOf('.');
            return dot >= 0 ? key[(dot + 1)..] : key;
        }
        public static string T(string key)         
        {
            // 1. A user override wins
            if (_overrides.TryGetValue(key, out var overrideVal)) return UIUtils.Intern(overrideVal);

            // 2. Otherwise the language file's text (interned)
            return UIUtils.Intern(GetOriginal(key));
        }

        // Intern keys so strings like Items.CPU.Load are not duplicated
        private static Dictionary<string, string> Flatten(JsonElement element, string prefix = "")
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in element.EnumerateObject())
            {
                // Intern the key here too
                string fullKey = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in Flatten(prop.Value, fullKey))
                        dict[kv.Key] = kv.Value;
                }
                else
                {
                    dict[UIUtils.Intern(fullKey)] = prop.Value.GetString() ?? "";
                }
            }
            return dict;
        }
    }
}