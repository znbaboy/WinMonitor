using System;
using System.Collections.Generic;
using System.Linq;
using WinMonitor.Services.Halo;

namespace WinMonitor.Services
{
    /// <summary>A connected wireless device.</summary>
    public sealed class HaloDevice
    {
        public string Key = "";
        public string Name = "";
        public int Level;
        public bool Charging;
        public bool Approx;     // coarse reading (e.g. XInput's four levels)
        public string Type = "";
        public int LowAlert = 20;
    }

    /// <summary>
    /// [Halo] Built-in Halo Battery. The panel has one row per device type (Monitor Items:
    /// HALO.Mouse, HALO.Keyboard, ...); each row shows the first connected device of its type.
    /// </summary>
    public static class HaloBatteryService
    {
        /// <summary>The device types, in Halo Battery's order ("Icon" menu); also the item keys HALO.&lt;type&gt;.</summary>
        public static readonly string[] Types = { "Mouse", "Keyboard", "Headset", "Controller", "Bluetooth" };

        private static readonly string[] HeadsetWords =
            { "blackshark", "kraken", "barracuda", "nari", "thresher", "headset", "headphone", "earbud", "buds", "hammerhead", "airpods" };
        private static readonly string[] GamepadWords = { "controller", "gamepad", "joystick", "joy-con" };

        /// <summary>halo_battery.pyw badge_for(): the device type.</summary>
        internal static string TypeOf(DeviceStatus s)
        {
            switch (s.Kind)
            {
                case "mouse": return "Mouse";
                case "keyboard": return "Keyboard";
                case "headset": return "Headset";
                case "gamepad": return "Controller";
            }
            string n = (s.Name ?? "").ToLowerInvariant();
            if (HeadsetWords.Any(n.Contains)) return "Headset";
            if (s.Source == "playstation" || s.Source == "xinput") return "Controller";
            if (s.Source == "bluetooth") return GamepadWords.Any(n.Contains) ? "Controller" : "Bluetooth";
            return "Mouse";
        }

        /// <summary>Every connected device with a battery reading.</summary>
        public static List<HaloDevice> Connected()
        {
            var list = new List<HaloDevice>();
            foreach (var s in HaloEngine.Snapshot)
            {
                if (s.Level == null || !s.Online) continue;
                list.Add(new HaloDevice
                {
                    Key = s.Key,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name,
                    Level = Math.Clamp(s.Level.Value, 0, 100),
                    Charging = s.Charging,
                    Approx = !string.IsNullOrEmpty(s.Approx),
                    Type = TypeOf(s),
                });
            }
            return list;
        }

        /// <summary>Type -> the first connected device of that type. Off, asleep and
        /// disconnected devices are left out (their row shows a dash).</summary>
        public static Dictionary<string, HaloDevice> ByType()
        {
            var map = new Dictionary<string, HaloDevice>();
            foreach (var s in HaloEngine.Snapshot)
            {
                if (s.Level == null || !s.Online) continue;
                string type = TypeOf(s);
                if (map.ContainsKey(type)) continue;
                map[type] = new HaloDevice
                {
                    Key = s.Key,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? s.Key : s.Name,
                    Level = Math.Clamp(s.Level.Value, 0, 100),
                    Charging = s.Charging,
                    Approx = !string.IsNullOrEmpty(s.Approx),
                    Type = type,
                };
            }
            return map;
        }
    }
}
