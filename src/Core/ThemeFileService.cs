using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WinMonitor.Core;

namespace WinMonitor.Core
{
    /// <summary>
    /// Theme files: load, save, create, duplicate, rename, delete
    /// </summary>
    public static class ThemeFileService
    {
        /// <summary>
        /// Theme folder
        /// </summary>
        private static string Dir => ThemeManager.ThemeDir;

        /// <summary>
        /// All theme files
        /// </summary>
        public static List<string> ListThemes()
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            var list = new List<string>();

            foreach (var f in Directory.GetFiles(Dir, "*.json"))
                list.Add(Path.GetFileNameWithoutExtension(f));

            list.Sort();
            return list;
        }

        /// <summary>
        /// Loads a theme file
        /// </summary>
        public static Theme LoadTheme(string name)
        {
            string path = Path.Combine(Dir, name + ".json");

            if (!File.Exists(path))
                throw new FileNotFoundException($"Theme not found: {path}");

            string json = File.ReadAllText(path);

            var theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
            });

            if (theme == null)
                throw new Exception($"Failed to parse theme JSON: {name}");


            return theme;
        }

        /// <summary>
        /// Saves a theme to its file
        /// </summary>
        public static void SaveTheme(string name, Theme theme)
        {
            string path = Path.Combine(Dir, name + ".json");
            string json = JsonSerializer.Serialize(theme, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            SafeWrite(path, json);
        }

        /// <summary>
        /// Creates a theme (copy of the template)
        /// </summary>
        public static void CreateTheme(string name)
        {
            string path = Path.Combine(Dir, name + ".json");

            if (File.Exists(path))
                throw new Exception("Theme already exists: " + name);

            // Start from the current theme
            string template = Path.Combine(Dir, "DarkFlat_Classic.json");

            if (File.Exists(template))
                File.Copy(template, path);
            else
                File.WriteAllText(path, "{}");
        }

        /// <summary>
        /// Duplicates a theme
        /// </summary>
        public static void DuplicateTheme(string src, string dst)
        {
            string srcPath = Path.Combine(Dir, src + ".json");
            string dstPath = Path.Combine(Dir, dst + ".json");

            if (!File.Exists(srcPath))
                throw new FileNotFoundException("Source theme missing: " + src);

            if (File.Exists(dstPath))
                throw new Exception("Target theme already exists: " + dst);

            File.Copy(srcPath, dstPath);
        }

        /// <summary>
        /// Renames a theme
        /// </summary>
        public static void RenameTheme(string src, string dst)
        {
            string srcPath = Path.Combine(Dir, src + ".json");
            string dstPath = Path.Combine(Dir, dst + ".json");

            if (!File.Exists(srcPath))
                throw new FileNotFoundException("Source theme missing: " + src);

            if (File.Exists(dstPath))
                throw new Exception("Target theme already exists: " + dst);

            File.Move(srcPath, dstPath);
        }

        /// <summary>
        /// Deletes a theme
        /// </summary>
        public static void DeleteTheme(string name)
        {
            string path = Path.Combine(Dir, name + ".json");
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>
        /// Writes via a temporary file so a failed write cannot break the JSON
        /// </summary>
        private static void SafeWrite(string path, string content)
        {
            string tmp = path + ".tmp";

            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);

            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
