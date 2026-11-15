// Ported from Halo Battery (https://github.com/HeyOkay/HaloBattery), MIT License,
// Copyright (c) 2026 Halo Battery contributors.
using System.Collections.Generic;

namespace WinMonitor.Services.Halo
{
    /// <summary>providers/base.py DeviceStatus</summary>
    public sealed class DeviceStatus
    {
        public string Key = "";        // stable device identifier
        public string Name = "";
        public int? Level;             // 0..100, null = unknown
        public bool Charging;
        public bool Online = true;     // false = receiver present, device asleep/off (last known level)
        public string Source = "";
        public string Approx = "";     // coarse level text, e.g. "about 55% (medium)"
        public string Kind = "";       // headset / mouse / keyboard / gamepad when known

        public DeviceStatus() { }

        public DeviceStatus(string key, string name, int? level, bool charging, bool online, string source,
                            string approx = "", string kind = "")
        {
            Key = key; Name = name; Level = level; Charging = charging; Online = online;
            Source = source; Approx = approx; Kind = kind;
        }

        public DeviceStatus Asleep() => new DeviceStatus(Key, Name, Level, Charging, false, Source, Approx, Kind);
    }

    public interface IBatteryProvider
    {
        string Name { get; }
        List<DeviceStatus> Poll();
    }
}
