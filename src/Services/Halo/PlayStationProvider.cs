// Ported from Halo Battery providers/playstation.py (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
//
// DualShock 4 and DualSense / DualSense Edge, read from the controller's HID input report.
//  * USB: the controller streams its full report after a feature read.
//      DS4 report 0x01 byte 30 (low nibble 0..10, bit 4 cable); DualSense 0x01 byte 53
//  * Bluetooth: listen only (switching to the full report breaks DirectInput games, Halo #96).
//      DS4 report 0x11 byte 32; DualSense 0x31 byte 54 - read when Steam/a game already switched it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WinMonitor.Services.Halo
{
    public sealed class PlayStationProvider : IBatteryProvider
    {
        public string Name => "playstation";
        private const ushort SonyVid = 0x054C;
        private const int AdapterPid = 0x0BA0;
        private const double Window = 1.5, Budget = 2.5;

        private static readonly Dictionary<int, (string Name, bool DualSense)> Known = new()
        {
            [0x05C4] = ("Sony DualShock 4", false),
            [0x09CC] = ("Sony DualShock 4", false),
            [0x05C5] = ("Sony DualShock 4", false),
            [AdapterPid] = ("Sony DualShock 4", false),
            [0x0CE6] = ("Sony DualSense", true),
            [0x0DF2] = ("Sony DualSense Edge", true),
        };

        private static (int Rid, int Off) Report(bool dualSense, bool bluetooth) =>
            dualSense ? (bluetooth ? (0x31, 54) : (0x01, 53)) : (bluetooth ? (0x11, 32) : (0x01, 30));

        internal static (int Level, bool Charging) ParseDs4(int b) => (Math.Min(b & 0x0F, 10) * 10, (b & 0x10) != 0);

        internal static (int Level, bool Charging) ParseDualSense(int b)
        {
            int level = Math.Min(b & 0x0F, 10) * 10;
            int charge = (b >> 4) & 0x0F;
            return (level, charge == 0x1 || charge == 0x2);
        }

        /// <summary>Halo's "PlayStation over Bluetooth: full mode". When on, a Bluetooth controller is
        /// switched to its full report so its battery can be read; until the controller is turned off,
        /// games that read it through DirectInput may stop seeing its input (Halo #96).</summary>
        public static Func<bool> SwitchBluetooth = () => false;

        internal readonly List<string> Diag = new();

        private (int Level, bool Charging)? ReadPath(HidInfo info, bool dualSense, double window, bool adapter)
        {
            using var dev = Hid.Open(info);
            if (dev == null) { Diag.Add("    open failed"); return null; }
            bool bluetooth = info.IsBluetooth;
            var (rid, off) = Report(dualSense, bluetooth);
            bool listenOnly = bluetooth && !SwitchBluetooth();
            if (!listenOnly) dev.GetFeature((byte)(dualSense ? 0x05 : 0x02), 64);   // switch to the full report
            var deadline = DateTime.UtcNow.AddSeconds(window);
            while (DateTime.UtcNow < deadline)
            {
                var data = dev.Read(50);
                if (data == null) continue;
                if (data[0] != rid || data.Length <= off)
                {
                    if (listenOnly && data[0] == 0x01)
                    {
                        Diag.Add("    Bluetooth basic mode (report 0x01): no battery until full mode is on");
                        return null;
                    }
                    continue;
                }
                if (adapter && data.Length > 31 && (data[31] & 0x04) != 0) { Diag.Add("    adapter: no controller"); return null; }
                return dualSense ? ParseDualSense(data[off]) : ParseDs4(data[off]);
            }
            Diag.Add($"    no report 0x{rid:x2} within {window:0.0} s");
            return null;
        }

        public List<DeviceStatus> Poll()
        {
            Diag.Clear();
            var infos = Hid.Enumerate(SonyVid);
            var groups = new Dictionary<(int Pid, string Serial), List<HidInfo>>();
            foreach (var d in infos)
            {
                if (!Known.ContainsKey(d.ProductId)) continue;
                string serial = d.Serial;
                if (serial == "" && !d.IsBluetooth) serial = "usb-" + d.Instance;
                var k = ((int)d.ProductId, serial);
                if (!groups.TryGetValue(k, out var l)) groups[k] = l = new List<HidInfo>();
                l.Add(d);
            }

            var conns = new List<(int Pid, string Name, bool Bt, string Mac, string Usb, (int Level, bool Charging)? Reading)>();
            foreach (var ((pid, serial), ifaces) in groups)
            {
                var (name, dualSense) = Known[pid];
                bool bt = ifaces[0].IsBluetooth;
                Diag.Add($"  {name} pid={pid:x4} {(bt ? "Bluetooth" : "USB")} interfaces={ifaces.Count}");
                (int, bool)? res = null;
                var budgetEnd = DateTime.UtcNow.AddSeconds(Budget);
                // the gamepad collection carries the battery: try it first
                foreach (var d in ifaces.OrderBy(d => d.UsagePage == 0x01 && (d.Usage == 0x04 || d.Usage == 0x05) ? 0 : 1))
                {
                    double left = (budgetEnd - DateTime.UtcNow).TotalSeconds;
                    if (left <= 0) break;
                    res = ReadPath(d, dualSense, Math.Min(Window, left), pid == AdapterPid);
                    if (res != null) break;
                }
                Diag.Add(res != null ? $"    -> {res.Value.Item1}%{(res.Value.Item2 ? " charging" : "")}" : "    -> no reading");
                conns.Add((pid, name, bt, bt ? serial : "", bt ? "" : serial, res));
            }

            // one controller on Bluetooth AND the cable: one device, the cable (charging) reading wins
            var devs = new List<(int Pid, string Name, string Mac, string Usb, (int Level, bool Charging)? Reading)>();
            foreach (var group in conns.GroupBy(c => c.Pid))
            {
                var wireless = group.Where(c => c.Bt).ToList();
                var wired = group.Where(c => !c.Bt).ToList();
                if (wireless.Count == 1 && wired.Count > 0)
                {
                    var w = wireless[0];
                    var reading = Prefer(wired[0].Reading, w.Reading);
                    devs.Add((w.Pid, w.Name, w.Mac, w.Usb, reading));
                    devs.AddRange(wired.Skip(1).Select(c => (c.Pid, c.Name, c.Mac, c.Usb, c.Reading)));
                }
                else devs.AddRange(group.Select(c => (c.Pid, c.Name, c.Mac, c.Usb, c.Reading)));
            }

            var usbCount = devs.Where(d => d.Mac == "").GroupBy(d => d.Pid).ToDictionary(g => g.Key, g => g.Count());
            var output = new List<DeviceStatus>();
            foreach (var d in devs)
            {
                string ident = d.Mac != "" ? d.Mac : (usbCount.TryGetValue(d.Pid, out var n) && n > 1 ? d.Usb : "");
                string key = $"ps:{d.Pid:x4}:{ident}";
                if (d.Reading == null) continue;   // connected but no battery report: nothing to show
                output.Add(new DeviceStatus(key, d.Name, d.Reading.Value.Level, d.Reading.Value.Charging, true,
                                            "playstation", kind: "gamepad"));
            }
            return output;
        }

        private static (int, bool)? Prefer((int, bool)? a, (int, bool)? b)
        {
            if (a != null && a.Value.Item2) return a;
            if (b != null && b.Value.Item2) return b;
            return a ?? b;
        }
    }
}
