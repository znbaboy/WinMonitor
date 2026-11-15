// Ported from Halo Battery providers/razer.py (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
//
// Razer devices with a wireless receiver: a 90-byte feature report (report id 0), the same
// one Synapse and OpenRazer use.
//   [0] status (0x02 OK, 0x01 busy, 0x03 failure, 0x04 not responding, 0x05 not supported)
//   [1] transaction id   [5] data size   [6] command class (0x07 power)
//   [7] command id (0x80 battery level, 0x84 charging)   [9] value   [88] crc (xor 2..87)
// Not ported: the BlackShark V2 Pro 2020/2023 and Barracuda headset protocols.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WinMonitor.Services.Halo
{
    public sealed class RazerProvider : IBatteryProvider
    {
        public string Name => "razer";
        private const ushort RazerVid = 0x1532;

        // PID -> (name, preferred transaction id), from OpenRazer via Halo Battery
        private static readonly Dictionary<int, (string Name, int Tid)> Known = new()
        {
            [0x0528] = ("Razer BlackShark V2 Pro (2020)", 0x3F),
            [0x0555] = ("Razer BlackShark V2 Pro (2023)", 0x3F),
            [0x0556] = ("Razer BlackShark V2 Pro (2023)", 0x3F),
            [0x0557] = ("Razer BlackShark V2 Pro (2023)", 0x3F),
            [0x00A4] = ("Razer Mouse Dock Pro", 0x1F),
            [0x0290] = ("Razer DeathStalker V2 Pro", 0x9F),
            [0x0292] = ("Razer DeathStalker V2 Pro", 0x1F),
            [0x0296] = ("Razer DeathStalker V2 Pro TKL", 0x9F),
            [0x0298] = ("Razer DeathStalker V2 Pro TKL", 0x1F),
            [0x0271] = ("Razer BlackWidow V3 Mini HyperSpeed", 0x9F),
            [0x0258] = ("Razer BlackWidow V3 Mini HyperSpeed", 0x1F),
            [0x02BA] = ("Razer BlackWidow V4 Mini HyperSpeed", 0x9F),
            [0x02B9] = ("Razer BlackWidow V4 Mini HyperSpeed", 0x1F),
            [0x02D5] = ("Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x9F),
            [0x02D7] = ("Razer BlackWidow V4 Tenkeyless HyperSpeed", 0x1F),
            [0x001F] = ("Razer Naga Epic", 0xFF),
            [0x0024] = ("Razer Mamba (2012)", 0xFF),
            [0x0025] = ("Razer Mamba (2012)", 0xFF),
            [0x0032] = ("Razer Ouroboros", 0xFF),
            [0x003E] = ("Razer Naga Epic Chroma", 0xFF),
            [0x003F] = ("Razer Naga Epic Chroma", 0xFF),
            [0x0044] = ("Razer Mamba", 0xFF),
            [0x0045] = ("Razer Mamba", 0xFF),
            [0x0059] = ("Razer Lancehead", 0x3F),
            [0x005A] = ("Razer Lancehead", 0x3F),
            [0x0062] = ("Razer Atheris", 0x1F),
            [0x006F] = ("Razer Lancehead Wireless", 0x1F),
            [0x0070] = ("Razer Lancehead Wireless", 0x1F),
            [0x0072] = ("Razer Mamba Wireless", 0x3F),
            [0x0073] = ("Razer Mamba Wireless", 0x3F),
            [0x0077] = ("Razer Pro Click", 0x1F),
            [0x0080] = ("Razer Pro Click", 0x1F),
            [0x007A] = ("Razer Viper Ultimate", 0xFF),
            [0x007B] = ("Razer Viper Ultimate", 0xFF),
            [0x007C] = ("Razer DeathAdder V2 Pro", 0x3F),
            [0x007D] = ("Razer DeathAdder V2 Pro", 0x3F),
            [0x0083] = ("Razer Basilisk X HyperSpeed", 0xFF),
            [0x0086] = ("Razer Basilisk Ultimate", 0x1F),
            [0x0088] = ("Razer Basilisk Ultimate", 0x1F),
            [0x008F] = ("Razer Naga Pro", 0x1F),
            [0x0090] = ("Razer Naga Pro", 0x1F),
            [0x0094] = ("Razer Orochi V2", 0x1F),
            [0x009A] = ("Razer Pro Click Mini", 0x1F),
            [0x009C] = ("Razer DeathAdder V2 X HyperSpeed", 0x1F),
            [0x009E] = ("Razer Viper Mini SE", 0x1F),
            [0x009F] = ("Razer Viper Mini SE", 0x1F),
            [0x00A5] = ("Razer Viper V2 Pro", 0x1F),
            [0x00A6] = ("Razer Viper V2 Pro", 0x1F),
            [0x00A7] = ("Razer Naga V2 Pro", 0x1F),
            [0x00A8] = ("Razer Naga V2 Pro", 0x1F),
            [0x00AA] = ("Razer Basilisk V3 Pro", 0x1F),
            [0x00AB] = ("Razer Basilisk V3 Pro", 0x1F),
            [0x00AF] = ("Razer Cobra Pro", 0x1F),
            [0x00B0] = ("Razer Cobra Pro", 0x1F),
            [0x00B3] = ("Razer HyperPolling Dongle", 0x1F),
            [0x00B4] = ("Razer Naga V2 HyperSpeed", 0x1F),
            [0x00B6] = ("Razer DeathAdder V3 Pro", 0x1F),
            [0x00B7] = ("Razer DeathAdder V3 Pro", 0x1F),
            [0x00B8] = ("Razer Viper V3 HyperSpeed", 0x1F),
            [0x00B9] = ("Razer Basilisk V3 X HyperSpeed", 0x1F),
            [0x00BE] = ("Razer DeathAdder V4 Pro", 0x1F),
            [0x00BF] = ("Razer DeathAdder V4 Pro", 0x1F),
            [0x00C0] = ("Razer Viper V3 Pro", 0x1F),
            [0x00C1] = ("Razer Viper V3 Pro", 0x1F),
            [0x00C2] = ("Razer DeathAdder V3 Pro", 0x1F),
            [0x00C3] = ("Razer DeathAdder V3 Pro", 0x1F),
            [0x00C4] = ("Razer DeathAdder V3 HyperSpeed", 0x1F),
            [0x00C5] = ("Razer DeathAdder V3 HyperSpeed", 0x1F),
            [0x00C7] = ("Razer Pro Click V2 Vertical Edition", 0x1F),
            [0x00C8] = ("Razer Pro Click V2 Vertical Edition", 0x1F),
            [0x00CC] = ("Razer Basilisk V3 Pro 35K", 0x1F),
            [0x00CD] = ("Razer Basilisk V3 Pro 35K", 0x1F),
            [0x00D0] = ("Razer Pro Click V2", 0x1F),
            [0x00D1] = ("Razer Pro Click V2", 0x1F),
            [0x00D3] = ("Razer Basilisk Mobile", 0x1F),
            [0x00D4] = ("Razer Basilisk Mobile", 0x1F),
            [0x00D6] = ("Razer Basilisk V3 Pro 35K Phantom Green", 0x1F),
            [0x00D7] = ("Razer Basilisk V3 Pro 35K Phantom Green", 0x1F),
            [0x025A] = ("Razer BlackWidow V3 Pro", 0x3F),
            [0x025C] = ("Razer BlackWidow V3 Pro", 0x9F),
        };

        // keyboards take the commands on one USB interface (OpenRazer razer_get_report_params)
        private static readonly Dictionary<int, int> KeyboardInterface = new()
        {
            [0x0290] = 2, [0x0296] = 2, [0x02D5] = 2,
            [0x0292] = 3, [0x0298] = 3, [0x02D7] = 3,
            [0x0271] = 3, [0x0258] = 3, [0x02BA] = 3, [0x02B9] = 3,
        };

        private static readonly int[] TransactionIds = { 0x1F, 0x3F, 0xFF, 0x9F, 0x08 };
        private static readonly string[] WirelessWords =
            { "hyperspeed", "wireless", "receiver", "dongle", "dock", "blackshark", "barracuda", "nari" };
        // headsets with their own protocol (BlackShark V2 Pro 2020 + cable, 2023 PA, Barracuda Pro): not ported
        private static readonly HashSet<int> SkipPids = new() { 0x0528, 0x052E, 0x053A, 0x0555, 0x0556 };

        private const int StatusOk = 0x02, StatusBusy = 0x01, StatusTimeout = 0x04, StatusForeign = 0x100;
        private const int Sends = 3;
        private const double AsleepKeep = 300;

        private readonly Dictionary<string, (string Path, int Tid)> _cache = new();
        private readonly Dictionary<string, DateTime> _dead = new();

        // Interfaces that have answered before, and read errors in a row on a device's cached
        // interface. Right after the mouse wakes up the receiver can fail a read or two. Halo Battery
        // then drops the cached interface and blacklists every interface for 5 minutes (its greyed
        // last level hides that gap); here the row would show a dash for those 5 minutes instead.
        private readonly HashSet<string> _worked = new();
        private readonly Dictionary<string, int> _ioErrors = new();

        // The transaction id that last gave a battery reading, per device. While the mouse wakes
        // up, the right id (0x1F for most mice) can briefly fail; the scan then went on to other ids,
        // and one the receiver always answers with "not responding" (0xFF on the DeathAdder V3
        // HyperSpeed) was cached. Every later check then got "not responding", so the mouse looked
        // asleep forever (until WinMonitor restarted). Only the trusted id may be cached for "asleep".
        private readonly Dictionary<string, int> _goodTid = new();

        private int? TrustedTid(string gkey, int? prefTid) => _goodTid.TryGetValue(gkey, out var t) ? t : prefTid;
        private const int IoErrorsBeforeRescan = 3;
        private const double RetryWorkedSeconds = 10;

        internal readonly List<string> Diag = new();

        // test seams (unit tests replace the HID access)
        internal Func<List<HidInfo>> EnumerateFn = () => Hid.Enumerate(RazerVid);
        internal Func<HidInfo, int, (int? Status, int? Level, bool? Charging)> ReadFn = ReadPathCore;
        private readonly Dictionary<string, (int Level, bool Charging, DateTime At)> _last = new();

        private static bool MaybeWireless(int pid, string name)
        {
            string n = name.ToLowerInvariant();
            return Known.ContainsKey(pid) || WirelessWords.Any(w => n.Contains(w));
        }

        private static byte[] BuildRequest(int tid, int cmdClass, int cmdId, int size = 0x02)
        {
            var msg = new byte[90];
            msg[1] = (byte)tid;
            msg[5] = (byte)size;
            msg[6] = (byte)cmdClass;
            msg[7] = (byte)cmdId;
            byte crc = 0;
            for (int i = 2; i < 88; i++) crc ^= msg[i];
            msg[88] = crc;
            return msg;
        }

        private static byte[]? NormalizeReply(byte[]? resp)
        {
            if (resp == null) return null;
            byte[] data = resp;
            if (data.Length >= 91) data = data.Skip(1).Take(90).ToArray();
            return data.Length < 90 ? null : data;
        }

        private static bool Send(HidDevice dev, byte[] req)
        {
            var buf = new byte[91];          // report id 0 + 90 bytes
            Array.Copy(req, 0, buf, 1, 90);
            return dev.SendFeature(buf);
        }

        /// <summary>(status, value); status null = the interface rejected the request.</summary>
        private static (int? Status, int? Value) Query(HidDevice dev, int tid, int cmdClass, int cmdId)
        {
            var req = BuildRequest(tid, cmdClass, cmdId);
            if (!Send(dev, req)) return (null, null);
            int sends = 1, foreign = 0;
            var deadline = DateTime.UtcNow.AddSeconds(1.0);
            Thread.Sleep(60);
            while (true)
            {
                var data = NormalizeReply(dev.GetFeature(0x00, 91));
                if (data == null) return (null, null);
                int status = data[0];
                int? value = (data[6] == cmdClass && data[7] == cmdId) ? data[9] : null;
                bool isForeign = status == StatusOk && value == null;
                if (DateTime.UtcNow < deadline && (status == StatusBusy || isForeign))
                {
                    if (isForeign)
                    {
                        foreign++;
                        // another app (Synapse, RGB software) talks to the same collection: ask again
                        if (foreign % 4 == 0 && sends < Sends)
                        {
                            if (!Send(dev, req)) return (null, null);
                            sends++;
                        }
                    }
                    Thread.Sleep(80);
                    continue;
                }
                if (isForeign) return (StatusForeign, null);
                return (status, value);
            }
        }

        /// <summary>(status, level %, charging)</summary>
        private (int? Status, int? Level, bool? Charging) ReadPath(HidInfo info, int tid)
        {
            var r = ReadFn(info, tid);
            Diag.Add($"    iface={info.InterfacePerhaps()} usage={info.UsagePage:x4}:{info.Usage:x4} tid={tid:x2} -> " +
                     (r.Status == null ? "read error" : $"status {r.Status:x2}" + (r.Level != null ? $", {r.Level}%" : "")));
            return r;
        }

        private static (int? Status, int? Level, bool? Charging) ReadPathCore(HidInfo info, int tid)
        {
            using var dev = Hid.Open(info);
            if (dev == null) return (null, null, null);
            var (status, raw) = Query(dev, tid, 0x07, 0x80);
            if (status != StatusOk || raw == null) return (status, null, null);
            int level = (int)Math.Round(raw.Value / 255.0 * 100);
            var (cst, craw) = Query(dev, tid, 0x07, 0x84);
            bool charging = cst == StatusOk && craw != null && craw.Value != 0;
            return (status, level, charging);
        }

        public List<DeviceStatus> Poll()
        {
            var output = PollDevices();
            // one icon for a mouse on its cable while its receiver stays plugged in
            var models = new HashSet<string>(Known.Values.Select(v => v.Name));
            var live = new Dictionary<string, HashSet<string>>();
            foreach (var s in output)
                if (s.Online && models.Contains(s.Name))
                {
                    if (!live.TryGetValue(s.Name, out var set)) live[s.Name] = set = new HashSet<string>();
                    set.Add(s.Key.Split(':')[1]);
                }
            return output.Where(s => s.Online ||
                                     !(live.TryGetValue(s.Name, out var pids) && pids.Any(p => p != s.Key.Split(':')[1])))
                         .ToList();
        }

        private List<DeviceStatus> PollDevices()
        {
            Diag.Clear();
            var infos = EnumerateFn();

            // group interfaces by device (PID + serial)
            var groups = new Dictionary<(int Pid, string Serial), List<HidInfo>>();
            foreach (var d in infos)
            {
                var k = ((int)d.ProductId, d.Serial ?? "");
                if (!groups.TryGetValue(k, out var l)) groups[k] = l = new List<HidInfo>();
                l.Add(d);
            }
            // collections Windows re-parents (empty serial) join the one real device of that PID
            var named = new Dictionary<int, List<string>>();
            foreach (var (pid, serial) in groups.Keys)
                if (serial != "")
                {
                    if (!named.TryGetValue(pid, out var l)) named[pid] = l = new List<string>();
                    l.Add(serial);
                }
            foreach (var k in groups.Keys.ToList())
            {
                if (k.Serial == "" && named.TryGetValue(k.Pid, out var serials) && serials.Count == 1)
                {
                    groups[(k.Pid, serials[0])].AddRange(groups[k]);
                    groups.Remove(k);
                }
            }

            var output = new List<DeviceStatus>();
            foreach (var ((pid, serial), ifaces) in groups)
            {
                string? name = Known.TryGetValue(pid, out var known) ? known.Name : null;
                int? prefTid = known.Name != null ? known.Tid : null;
                if (string.IsNullOrEmpty(name))
                {
                    string product = ifaces[0].Product;
                    name = string.IsNullOrEmpty(product) ? $"Razer {pid:x4}" : product.Trim();
                }
                string key = $"razer:{pid:x4}:{serial}";
                string kind = KeyboardInterface.ContainsKey(pid) ? "keyboard" : "";

                if (SkipPids.Contains(pid)) continue;
                if (!MaybeWireless(pid, name)) continue;

                Diag.Add($"  {name} pid={pid:x4}, interfaces: {ifaces.Count}");
                var st = PollGroup(key, pid, ifaces, prefTid);
                if (st == null || st.Value.Status != StatusOk)
                {
                    // not responding (off or asleep): keep the last value greyed for a while
                    if (_last.TryGetValue(key, out var last) && (DateTime.UtcNow - last.At).TotalSeconds < AsleepKeep)
                        output.Add(new DeviceStatus(key, name, last.Level, last.Charging, false, "razer", kind: kind));
                    continue;
                }
                var (_, level, charging) = st.Value;
                if (level != null) _last[key] = (level.Value, charging == true, DateTime.UtcNow);
                output.Add(new DeviceStatus(key, name, level, charging == true, true, "razer", kind: kind));
            }
            return output;
        }

        private (int Status, int? Level, bool? Charging)? PollGroup(string gkey, int pid, List<HidInfo> ifaces, int? prefTid)
        {
            if (_cache.TryGetValue(gkey, out var cached))
            {
                var info = ifaces.FirstOrDefault(i => i.Path == cached.Path);
                if (info != null)
                {
                    var (s, l, c) = ReadPath(info, cached.Tid);
                    int? trusted = TrustedTid(gkey, prefTid);
                    bool trustedTid = trusted == null || trusted == cached.Tid;
                    if (s == StatusOk || ((s == StatusTimeout || s == StatusForeign) && trustedTid))
                    {
                        _ioErrors.Remove(gkey);
                        if (s == StatusOk) _goodTid[gkey] = cached.Tid;
                        return (s.Value, l, c);
                    }
                    // a read error on the interface that worked: usually the receiver is busy
                    // (the mouse is waking up). Keep it and try again next time instead of rescanning.
                    if (s == null)
                    {
                        int n = _ioErrors.TryGetValue(gkey, out var e) ? e + 1 : 1;
                        _ioErrors[gkey] = n;
                        if (n < IoErrorsBeforeRescan) return null;
                    }
                }
                _ioErrors.Remove(gkey);
                _cache.Remove(gkey);
            }

            int? want = KeyboardInterface.TryGetValue(pid, out var wi) ? wi : null;
            (int, int, int) Rank(HidInfo d)
            {
                int iface = d.InterfaceNumber < 0 ? 99 : d.InterfaceNumber;
                return (iface == want ? 0 : 1, d.UsagePage == 0x0001 || d.UsagePage == 0xFF00 ? 0 : 1, iface);
            }

            int? trustedScan = TrustedTid(gkey, prefTid);
            var tids = new List<int>();
            if (trustedScan != null) tids.Add(trustedScan.Value);
            if (prefTid != null && !tids.Contains(prefTid.Value)) tids.Add(prefTid.Value);
            tids.AddRange(TransactionIds.Where(t => !tids.Contains(t)));

            var now = DateTime.UtcNow;
            (int, int?, bool?)? timeoutHit = null;
            foreach (var d in ifaces.OrderBy(Rank))
            {
                if (_dead.TryGetValue(d.Path, out var until) && until > now) continue;
                bool answered = false;
                foreach (int tid in tids)
                {
                    var (status, level, charging) = ReadPath(d, tid);
                    if (status == null) break;      // the interface rejects feature reports
                    answered = true;
                    if (status == StatusOk)
                    {
                        _cache[gkey] = (d.Path, tid);
                        _goodTid[gkey] = tid;
                        _worked.Add(d.Path);
                        return (status.Value, level, charging);
                    }
                    bool trustedTid = trustedScan == null || tid == trustedScan;
                    if (status == StatusTimeout)
                    {
                        timeoutHit = (status.Value, null, null);
                        _worked.Add(d.Path);
                        if (trustedTid) { _cache[gkey] = (d.Path, tid); break; }
                        continue;   // not cached: the next check scans again, trusted id first
                    }
                    if (status == StatusForeign)
                    {
                        _worked.Add(d.Path);
                        if (trustedTid) _cache[gkey] = (d.Path, tid);
                        return (status.Value, null, null);
                    }
                }
                // an interface that never answers is left alone for 5 minutes (Halo Battery);
                // one that has worked before is only skipped briefly (a waking receiver)
                if (!answered) _dead[d.Path] = now.AddSeconds(_worked.Contains(d.Path) ? RetryWorkedSeconds : 300);
            }
            return timeoutHit;
        }
    }
}
