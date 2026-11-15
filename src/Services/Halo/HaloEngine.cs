// Built-in Halo Battery: polls the device providers on a background thread.
// Providers ported from Halo Battery (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WinMonitor.Services.Halo
{
    public static class HaloEngine
    {
        /// <summary>Seconds between full device polls (Halo Battery's default is 60 and its minimum 5).</summary>
        public static int IntervalSeconds = 20;

        /// <summary>Optional source for the interval (the settings); read before every wait.</summary>
        public static Func<int>? IntervalProvider;

        private static int Interval
        {
            get
            {
                int s = IntervalSeconds;
                try { if (IntervalProvider != null) s = IntervalProvider(); } catch { }
                return Math.Clamp(s, 1, 86400);
            }
        }

        private const int CheckMs = 2000;   // how often a plugged / unplugged device is noticed

        private static readonly IBatteryProvider[] Providers =
        {
            new RazerProvider(),
            new LogitechProvider(),
            new PlayStationProvider(),
            new XInputProvider(),
            new BluetoothProvider(),
        };

        private static readonly object StartLock = new();
        private static readonly AutoResetEvent Wake = new(false);
        private static readonly List<string> Order = new();   // first-seen order keeps rows from jumping
        private static Thread? _thread;
        private static volatile IReadOnlyList<DeviceStatus> _snapshot = Array.Empty<DeviceStatus>();

        /// <summary>The latest reading of every device. Starts the poller on first use.</summary>
        public static IReadOnlyList<DeviceStatus> Snapshot
        {
            get
            {
                EnsureStarted();
                return _snapshot;
            }
        }

        public static void PollNow() => Wake.Set();

        /// <summary>What each provider saw on its last poll, for troubleshooting a device.</summary>
        public static string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"LiteMonitor device report {DateTime.Now:yyyy-MM-dd HH:mm:ss}, interval {Interval} s");
            sb.AppendLine("Devices:");
            foreach (var s in _snapshot)
                sb.AppendLine($"  {s.Key} '{s.Name}' level={(s.Level?.ToString() ?? "-")} charging={s.Charging} online={s.Online} kind='{s.Kind}' source={s.Source}{(s.Approx != "" ? " approx" : "")}");
            foreach (var p in Providers)
            {
                List<string>? diag = p switch
                {
                    RazerProvider rz => rz.Diag,
                    PlayStationProvider ps => ps.Diag,
                    XInputProvider xi => xi.Diag,
                    BluetoothProvider bt => bt.Diag,
                    _ => null,
                };
                if (diag == null) continue;
                sb.AppendLine($"[{p.Name}]");
                string[] lines;
                try { lines = diag.ToArray(); }                       // the poller may be writing to it
                catch { lines = new[] { "  (being updated, copy again)" }; }
                foreach (var line in lines) sb.AppendLine(line);
            }
            return sb.ToString();
        }

        private static void EnsureStarted()
        {
            if (_thread != null) return;
            lock (StartLock)
            {
                if (_thread != null) return;
                _thread = new Thread(Loop) { IsBackground = true, Name = "Halo battery poll", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }
        }

        private static int PathSignature()
        {
            try
            {
                var paths = Hid.InterfacePaths();
                int h = paths.Count;
                foreach (var p in paths) h = unchecked(h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(p));
                return h;
            }
            catch { return 0; }
        }

        private static void Loop()
        {
            int sig = PathSignature();
            while (true)
            {
                PollAll();
                var last = DateTime.UtcNow;
                // the interval is read again on every check, so a new setting applies at once
                while (DateTime.UtcNow < last.AddSeconds(Interval))
                {
                    if (Wake.WaitOne(Math.Min(CheckMs, Interval * 1000))) break;
                    int now = PathSignature();
                    if (now != sig) { sig = now; Thread.Sleep(1500); break; }   // let a new device settle
                }
            }
        }

        private static void PollAll()
        {
            var byKey = new Dictionary<string, DeviceStatus>();
            foreach (var p in Providers)
            {
                List<DeviceStatus> found;
                try { found = p.Poll(); }
                catch { continue; }   // one provider failing must not stop the others
                foreach (var st in found)
                {
                    if (string.IsNullOrEmpty(st.Key)) continue;
                    // a live reading wins over a greyed one with the same key
                    if (!byKey.TryGetValue(st.Key, out var prev) || (!prev.Online && st.Online)) byKey[st.Key] = st;
                }
            }
            foreach (var k in byKey.Keys) if (!Order.Contains(k)) Order.Add(k);
            _snapshot = byKey.Values.OrderBy(s => Order.IndexOf(s.Key)).ToList();
        }
    }
}
