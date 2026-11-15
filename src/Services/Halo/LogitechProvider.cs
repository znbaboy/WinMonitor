// Ported from Halo Battery providers/logitech.py (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
//
// Logitech wireless mice, keyboards and headsets over HID++ 2.0 (works alongside G HUB).
//  * Receivers (Lightspeed, Unifying, Bolt, Nano): vendor collections ff00:0001 (short, 7 bytes)
//    and ff00:0002 (long, 20 bytes); paired devices in slots 1..6.
//  * A device on its USB cable: the same two collections, slot 0xFF.
//  * Headsets: only a long collection, slot 0xFF (ff43:0202; the G535 uses 000c:0001).
// Request: 11 <slot> <feature index> <function << 4 | swid> <params...>
// Battery features: 0x1004 unified, 0x1000 status, 0x1001 voltage, 0x1F20 ADC (headsets).
// HID++ 1.0 devices: registers 0x0D (charge) and 0x07 (status), read only.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace WinMonitor.Services.Halo
{
    public sealed class LogitechProvider : IBatteryProvider
    {
        public string Name => "logitech";
        private const ushort LogitechVid = 0x046D;

        private static readonly int[] Swids = { 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
        private const double Timeout = 0.6;
        private const double PingTimeout = 2.0;
        private const double AsleepKeep = 300;

        private const int F_ROOT = 0x0000, F_INFO = 0x0003, F_NAME = 0x0005;
        private const int F_UNIFIED = 0x1004, F_STATUS = 0x1000, F_VOLTAGE = 0x1001, F_ADC = 0x1F20;
        private static readonly int[] BatteryFeatures = { F_UNIFIED, F_STATUS, F_VOLTAGE, F_ADC };

        private const int REG_BATTERY_CHARGE = 0x0D, REG_BATTERY_STATUS = 0x07;
        private static readonly Dictionary<int, (int Level, string Word)> StatusLevels = new()
        { [7] = (90, "full"), [5] = (50, "good"), [3] = (20, "low"), [1] = (5, "critical") };

        private const int ERR_OLD_PROTOCOL = 0x01, ERR_CONNECT_FAIL = 0x04, ERR_EMPTY_SLOT = 0x08, ERR_UNREACHABLE = 0x09;

        private static readonly HashSet<int> Receivers = new()
        {
            0xC548,
            0xC52B, 0xC532,
            0xC52F, 0xC518, 0xC51A, 0xC51B, 0xC521, 0xC525, 0xC526,
            0xC52E, 0xC531, 0xC534, 0xC535, 0xC537,
            0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547,
            0xC54D,
        };

        private static readonly Dictionary<int, string> Headsets = new()
        {
            [0x0A66] = "Logitech G533", [0x0AC4] = "Logitech G535", [0x0A5C] = "Logitech G633",
            [0x0A89] = "Logitech G635", [0x0A5B] = "Logitech G933", [0x0A87] = "Logitech G935",
            [0x0AB5] = "Logitech G733", [0x0AFE] = "Logitech G733", [0x0B1F] = "Logitech G733",
            [0x0AA7] = "Logitech G PRO", [0x0AAA] = "Logitech G PRO X", [0x0ABA] = "Logitech G PRO X",
            [0x0AFB] = "Logitech G PRO X 2", [0x0AFC] = "Logitech G PRO X 2",
        };
        private static readonly Dictionary<int, (int Page, int Usage)> HeadsetCollection = new() { [0x0AC4] = (0x000C, 0x0001) };

        private static readonly Dictionary<int, string> Kinds = new()
        { [0] = "keyboard", [2] = "keyboard", [3] = "mouse", [4] = "mouse", [5] = "mouse" };

        private static readonly (int Mv, int Pct)[] VoltageCurve =
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        };
        private static readonly int[] OnCharger = { 1, 2, 3, 4 };
        private static readonly Dictionary<int, (int Level, string Word)> ApproxLevels = new()
        { [8] = (90, "full"), [4] = (50, "good"), [2] = (20, "low"), [1] = (5, "critical") };

        // slot = (pid, receiver instance, device index)
        private readonly Dictionary<(int, string, int), (string Name, string Kind, string Unit)> _ids = new();
        private readonly HashSet<(int, string, int)> _asleep = new();
        private readonly Dictionary<string, (DeviceStatus St, DateTime At)> _last = new();
        private readonly Dictionary<(int, string, int), string> _slotKey = new();

        internal static int VoltageToPercent(int mv)
        {
            if (mv >= VoltageCurve[0].Mv) return 100;
            for (int i = 0; i < VoltageCurve.Length - 1; i++)
            {
                var (hiMv, hiP) = VoltageCurve[i];
                var (loMv, loP) = VoltageCurve[i + 1];
                if (mv >= loMv)
                    return (int)Math.Round(loP + (mv - loMv) * (double)(hiP - loP) / (hiMv - loMv), MidpointRounding.ToEven);
            }
            return 0;
        }

        internal static (int? Level, bool Charging, string Approx) ParseBattery(int feature, int[] p)
        {
            if (feature == F_UNIFIED)
            {
                bool charging = OnCharger.Contains(p[2]);
                if (p[0] > 0 && p[0] <= 100) return (p[0], charging, "");
                if (p[0] == 0 && ApproxLevels.TryGetValue(p[1], out var a)) return (a.Level, charging, $"about {a.Level}% ({a.Word})");
                return (null, false, "");
            }
            if (feature == F_STATUS)
                return (p[0] > 0 && p[0] <= 100 ? p[0] : null, OnCharger.Contains(p[2]), "");
            if (feature == F_VOLTAGE || feature == F_ADC)
            {
                int mv = (p[0] << 8) | p[1];
                if (feature == F_ADC && (p[2] & 0x01) == 0) return (null, false, "");   // headset not connected
                if (mv < 2500) return (null, false, "");
                bool charging = feature == F_VOLTAGE ? (p[2] & 0x80) != 0 : (p[2] & 0x02) != 0;
                return (VoltageToPercent(mv), charging, "");
            }
            return (null, false, "");
        }

        internal static (int? Level, bool Charging, string Approx) ParseRegisterBattery(int reg, int[] p)
        {
            if (reg == REG_BATTERY_CHARGE)
            {
                int charge = p[0];
                if (!(charge > 0 && charge <= 100)) return (null, false, "");
                int st = p[2] & 0xF0;
                return (charge, st == 0x50 || st == 0x90, "");
            }
            if (reg == REG_BATTERY_STATUS)
            {
                int statusByte = p[0], chargingByte = p[1];
                bool charging = (chargingByte & 0x21) == 0x21 || (chargingByte & 0x22) == 0x22;
                if (StatusLevels.TryGetValue(statusByte, out var s)) return (s.Level, charging, $"about {s.Level}% ({s.Word})");
                return (null, charging, "");
            }
            return (null, false, "");
        }

        /// <summary>The HID++ collections of one receiver, cabled device or headset dongle.</summary>
        private sealed class Channel : IDisposable
        {
            public readonly List<HidDevice> Devs = new();
            public bool Error;
            public int ErrorCode;
            private int _swid;
            private readonly HidDevice _long;

            public Channel(HidInfo? shortInfo, HidInfo longInfo)
            {
                _long = Hid.Open(longInfo) ?? throw new InvalidOperationException("cannot open the long collection");
                Devs.Add(_long);
                if (shortInfo != null)
                {
                    var s = Hid.Open(shortInfo);
                    if (s != null) Devs.Add(s);
                }
            }

            public void Dispose()
            {
                foreach (var d in Devs) d.Dispose();
            }

            /// <summary>Params of the reply, or null on an error reply (Error set) or timeout.</summary>
            public int[]? Request(int idx, int feat, int func, int[]? prms = null, double timeout = Timeout)
            {
                Error = false; ErrorCode = 0;
                _swid = (_swid + 1) % Swids.Length;
                int fn = (func << 4) | Swids[_swid];
                var req = new byte[20];
                req[0] = 0x11; req[1] = (byte)idx; req[2] = (byte)feat; req[3] = (byte)fn;
                if (prms != null) for (int i = 0; i < prms.Length && 4 + i < 20; i++) req[4 + i] = (byte)prms[i];
                _long.Write(req);
                var end = DateTime.UtcNow.AddSeconds(timeout);
                while (DateTime.UtcNow < end)
                {
                    foreach (var d in Devs)
                    {
                        var r = d.Read(0);
                        if (r == null || r.Length < 4 || r[1] != idx) continue;
                        // an error reply names the feature and function it answers (G HUB shares the receiver)
                        if ((r[2] == 0x8F || r[2] == 0xFF) && r.Length >= 6 && r[3] == feat && r[4] == fn)
                        {
                            Error = true; ErrorCode = r[5];
                            return null;
                        }
                        if (r[2] == feat && r[3] == fn) return Params(r, 4);
                    }
                    Thread.Sleep(5);
                }
                return null;
            }

            /// <summary>HID++ 1.0 register read on the short collection (read only).</summary>
            public int[]? ReadRegister(int idx, int reg, double timeout = Timeout)
            {
                if (Devs.Count < 2) return null;
                Error = false; ErrorCode = 0;
                Devs[1].Write(new byte[] { 0x10, (byte)idx, 0x81, (byte)reg, 0, 0, 0 });
                var end = DateTime.UtcNow.AddSeconds(timeout);
                while (DateTime.UtcNow < end)
                {
                    foreach (var d in Devs)
                    {
                        var r = d.Read(0);
                        if (r == null || r.Length < 4 || r[1] != idx) continue;
                        if ((r[2] == 0x8F || r[2] == 0xFF) && r.Length >= 6 && r[3] == 0x81 && r[4] == reg)
                        {
                            Error = true; ErrorCode = r[5];
                            return null;
                        }
                        if (r[2] == 0x81 && r[3] == reg) return Params(r, 4);
                    }
                    Thread.Sleep(5);
                }
                return null;
            }

            private static int[] Params(byte[] r, int from)
            {
                var p = new int[Math.Max(0, r.Length - from) + 16];
                for (int i = from; i < r.Length; i++) p[i - from] = r[i];
                return p;
            }

            public int FeatureIndex(int idx, int featureId)
            {
                var r = Request(idx, 0, 0, new[] { featureId >> 8, featureId & 0xFF });
                return r != null ? r[0] : 0;
            }

            public (string Name, string Kind, string Unit) Identity(int idx)
            {
                string name = "", kind = "", unit = "";
                int fi = FeatureIndex(idx, F_NAME);
                if (fi != 0)
                {
                    var r = Request(idx, fi, 0);
                    int length = r != null ? r[0] : 0;
                    var raw = new List<byte>();
                    while (raw.Count < length)
                    {
                        r = Request(idx, fi, 1, new[] { raw.Count });
                        if (r == null) break;
                        for (int i = 0; i < 16; i++) raw.Add((byte)r[i]);
                    }
                    name = Encoding.UTF8.GetString(raw.Take(length).ToArray()).Trim().TrimEnd('\0').Trim();
                    r = Request(idx, fi, 2);
                    kind = r != null && Kinds.TryGetValue(r[0], out var k) ? k : "";
                }
                fi = FeatureIndex(idx, F_INFO);
                if (fi != 0)
                {
                    var r = Request(idx, fi, 0);
                    if (r != null && (r[1] | r[2] | r[3] | r[4]) != 0)
                        unit = $"{r[1]:X2}{r[2]:X2}{r[3]:X2}{r[4]:X2}";
                }
                return (name, kind, unit);
            }
        }

        private bool Ping(Channel ch, (int, string, int) slot)
        {
            int idx = slot.Item3;
            double timeout = _asleep.Contains(slot) ? Timeout : PingTimeout;
            if (ch.Request(idx, F_ROOT, 1, null, timeout) != null)
            {
                _asleep.Remove(slot);
                return true;
            }
            if (!ch.Error)
            {
                _asleep.Add(slot);
                return false;
            }
            _asleep.Remove(slot);
            if (ch.ErrorCode == ERR_EMPTY_SLOT) _ids.Remove(slot);  // a new device can take the slot
            return false;
        }

        private DeviceStatus? Read(Channel ch, int pid, int idx, string instance, bool multi)
        {
            var slot = (pid, instance, idx);
            if (!Ping(ch, slot))
            {
                if (ch.Error && ch.ErrorCode == ERR_OLD_PROTOCOL) return RegisterBattery(ch, slot, pid, idx, instance, multi);
                return null;
            }
            (string name, string kind, string unit) id;
            if (_ids.TryGetValue(slot, out var known)) id = known;
            else
            {
                id = ch.Identity(idx);
                // keep it only when the read worked; a device that just woke up can miss a request
                if (id.name != "" || id.unit != "") _ids[slot] = id;
            }
            string name = id.name, kind = id.kind, unit = id.unit;
            if (Headsets.TryGetValue(pid, out var hname)) { name = name != "" ? name : hname; kind = "headset"; }
            if (name == "") name = "Logitech device";

            foreach (int feature in BatteryFeatures)
            {
                int fi = ch.FeatureIndex(idx, feature);
                if (fi == 0) continue;
                var r = ch.Request(idx, fi, feature == F_UNIFIED ? 1 : 0);
                if (r == null) continue;
                var (level, chg, approx) = ParseBattery(feature, r);
                if (level != null)
                {
                    string prefix = multi ? instance + ":" : "";
                    string key = unit != "" ? $"logitech:{prefix}{unit}" : $"logitech:{prefix}{pid:x4}:{idx}";
                    if (_slotKey.TryGetValue(slot, out var old) && old != key) _last.Remove(old);
                    _slotKey[slot] = key;
                    return new DeviceStatus(key, name, level, chg, true, "logitech", approx, kind);
                }
            }
            return RegisterBattery(ch, slot, pid, idx, instance, multi);
        }

        private DeviceStatus? RegisterBattery(Channel ch, (int, string, int) slot, int pid, int idx, string instance, bool multi)
        {
            foreach (int reg in new[] { REG_BATTERY_CHARGE, REG_BATTERY_STATUS })
            {
                var r = ch.ReadRegister(idx, reg);
                if (r == null) continue;
                var (level, chg, approx) = ParseRegisterBattery(reg, r);
                if (level == null) continue;
                string name = _ids.TryGetValue(slot, out var id) && id.Name != "" ? id.Name : "Logitech device";
                string prefix = multi ? instance + ":" : "";
                string key = $"logitech:{prefix}{pid:x4}:{idx}";
                if (_slotKey.TryGetValue(slot, out var old) && old != key) _last.Remove(old);
                _slotKey[slot] = key;
                return new DeviceStatus(key, name, level, chg, true, "logitech", approx, "");
            }
            return null;
        }

        /// <summary>(pid, instance) -> {1: short, 2: long}. A headset has only a long collection.</summary>
        private static Dictionary<(int, string), Dictionary<int, HidInfo>> Collections(List<HidInfo> infos)
        {
            var groups = new Dictionary<(int, string), Dictionary<int, HidInfo>>();
            var headset = new Dictionary<(int, string), HidInfo>();
            foreach (var d in infos)
            {
                int pid = d.ProductId;
                var key = (pid, d.Instance);
                if (d.UsagePage == 0xFF00 && (d.Usage == 1 || d.Usage == 2))
                {
                    if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Dictionary<int, HidInfo>();
                    g[d.Usage] = d;
                }
                else if (Headsets.ContainsKey(pid))
                {
                    var want = HeadsetCollection.TryGetValue(pid, out var hc) ? hc : (0xFF43, 0x0202);
                    if (d.UsagePage == want.Item1 && d.Usage == want.Item2) headset[key] = d;
                }
            }
            foreach (var (key, info) in headset)
            {
                if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Dictionary<int, HidInfo>();
                if (!g.ContainsKey(2)) g[2] = info;
            }
            return groups;
        }

        public List<DeviceStatus> Poll()
        {
            var infos = Hid.Enumerate(LogitechVid);
            var groups = Collections(infos);
            var repeats = new HashSet<int>(groups.Keys.GroupBy(k => k.Item1).Where(g => g.Count() > 1).Select(g => g.Key));
            var found = new Dictionary<string, DeviceStatus>();

            foreach (var ((pid, inst), paths) in groups)
            {
                if (!paths.ContainsKey(2)) continue;
                string product = infos.FirstOrDefault(d => d.ProductId == pid)?.Product ?? "";
                bool receiver = Receivers.Contains(pid) || product.ToLowerInvariant().Contains("receiver");
                Channel ch;
                try { ch = new Channel(paths.TryGetValue(1, out var s) ? s : null, paths[2]); }
                catch { continue; }
                using (ch)
                {
                    try
                    {
                        var slots = receiver ? Enumerable.Range(1, 6) : new[] { 0xFF };
                        foreach (int idx in slots)
                        {
                            var st = Read(ch, pid, idx, inst, repeats.Contains(pid));
                            // the same device on the cable and through the receiver: charging wins
                            if (st != null && (!found.ContainsKey(st.Key) || st.Charging)) found[st.Key] = st;
                        }
                    }
                    catch { /* device went away mid-poll */ }
                }
            }

            var now = DateTime.UtcNow;
            var output = found.Values.ToList();
            foreach (var st in output) _last[st.Key] = (st, now);
            // asleep or switched off: keep the last value greyed for a while
            foreach (var (key, (st, at)) in _last.ToList())
            {
                if (found.ContainsKey(key)) continue;
                if ((now - at).TotalSeconds < AsleepKeep && groups.Count > 0) output.Add(st.Asleep());
                else _last.Remove(key);
            }
            return output;
        }
    }
}
