using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // For P/Invoke
using System.Diagnostics; // For Process
using System.Net.NetworkInformation; // For NetworkChange
using WinMonitor.Services;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    /// <summary>
    /// Dashboard values (singleton):
    /// host name, IP, time, uptime
    /// </summary>
    public class InfoService
    {
        #region Singleton
        private static InfoService _instance;
        public static InfoService Instance => _instance ??= new InfoService();
        private InfoService() { Initialize(); }
        #endregion

        // === Constants ===
        private const string KEY_HOST = "HOST";
        private const string KEY_IP   = "IP";
        private const string KEY_TIME = "Time";
        private const string KEY_UPTIME = "Uptime";

        // Default Values (User Friendly)
        private const string DEFAULT_IP   = "0.0.0.0";


        // Update Intervals (For IP/Host only, NOT for Uptime/Time)
        private const int INTERVAL_SLOW = 60000; // 1 min (Stable state: IP found)
        private const int INTERVAL_FAST = 2000;  // 2 sec (Retry state: IP missing)

        // === State ===
        private readonly Dictionary<string, string> _data = new();
        private readonly object _lock = new();
        
        private long _lastUpdateTick = 0;
        private int _currentInterval = INTERVAL_FAST;

        // Cache time strings to avoid allocs every tick
        private string _lastTimeStr = "";
        private int _lastSecond = -1;
        private string _lastUptimeStr = "";
        private int _lastUptimeMinute = -1;
        
        // Offset for Fast Startup handling
        private long _uptimeOffsetTicks = 0;

        // QueryUnbiasedInterruptTime leaves out sleep time (uptime would otherwise count it)
        [DllImport("kernel32.dll")]
        private static extern bool QueryUnbiasedInterruptTime(out ulong UnbiasedTime);

        /// <summary>
        /// Sets defaults and starts the first update
        /// </summary>
        private void Initialize()
        {
            lock (_lock)
            {
                _data[KEY_HOST] = Environment.MachineName; // Hostname always available
                _data[KEY_IP]   = DEFAULT_IP;
                _data[KEY_TIME] = DateTime.Now.ToString("ddd HH:mm:ss"); // show the current time straight away
            }
            
            // Network changes refresh the IP straight away
            NetworkChange.NetworkAddressChanged += (s, e) => {
                _currentInterval = INTERVAL_FAST;
                _lastUpdateTick = 0; // run the next Update now
            };
            
            // The process check runs in the background (slow, and can trigger antivirus scans)
            Task.Run(() => 
            {
                CalculateUptimeOffset();
                // Refresh the time values now (skip the per-minute cache)
                _lastUptimeMinute = -1;
                // Refresh once the correction is known
                UpdateTimeInfo();
            });

            // Calculate Uptime immediately so it's ready for first render
            // (Initial value might be uncorrected for a few ms, which is acceptable)
            UpdateTimeInfo();

            // Trigger first async update
            UpdateData();
        }

        /// <summary>
        /// Uptime correction: Fast Startup does not reset it, but a wait at the sign-in screen should count
        /// </summary>
        private void CalculateUptimeOffset()
        {
            try
            {
                // 1. How long this session has run
                // (several long-running processes, so one restart does not mislead)
                long minStart = long.MaxValue;
                foreach (var name in new[] { "explorer", "sihost", "taskhostw" })
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try { if (p.StartTime.Ticks < minStart) minStart = p.StartTime.Ticks; }
                        catch { }
                        p.Dispose();
                    }
                }

                if (minStart == long.MaxValue) return; // cannot tell: no correction

                long sessionTicks = DateTime.Now.Ticks - minStart;
                
                // A session older than 10 minutes means this is not a fresh start
                // (wake from sleep, or always on): trust the kernel time.
                if (sessionTicks > 600L * 1000 * 10000) return;

                // 2. Time without sleep (kernel)
                if (!QueryUnbiasedInterruptTime(out ulong unbiasedVal))
                    unbiasedVal = (ulong)Environment.TickCount64 * 10000;
                long unbiasedTicks = (long)unbiasedVal;

                // 3. Time including sleep (wall clock)
                // Environment.TickCount64 includes sleep
                // (faster than looking up the System process, no permission issues)
                long wallClockTicks = Environment.TickCount64 * 10000;

                // Restart-and-wait vs Fast Startup leftover:
                // A (restart, then 7 h at the sign-in screen):
                //    wall clock (7 h) ~ kernel (7 h): small difference -> keep 7 h
                // B (5 h yesterday, Fast Startup shutdown, sign in today):
                //    wall clock (24 h+) >> kernel (5 h+): it slept in between -> show 0 h
                
                // More than 30 minutes difference means a long sleep or shutdown happened
                if (wallClockTicks > unbiasedTicks + 1800L * 1000 * 10000)
                {
                    // Fast Startup leftover: count from this session
                    _uptimeOffsetTicks = unbiasedTicks - sessionTicks;
                }
            }
            catch { }
        }

        /// <summary>
        /// Thread-safe read
        /// </summary>
        public string GetValue(string key)
        {
            lock (_lock)
            {
                return _data.TryGetValue(key, out var val) ? val : "";
            }
        }

        /// <summary>
        /// Sets any value from outside
        /// </summary>
        public void InjectValue(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            // Intern keys and values from external injections
            SetData(UIUtils.Intern(key), value);
        }

        /// <summary>
        /// Sets the IP from outside (e.g. a cached value)
        /// </summary>
        public void InjectIP(string ip)
        {
            if (string.IsNullOrEmpty(ip) || ip == DEFAULT_IP) return;

            SetData(KEY_IP, ip);
            // If valid IP injected, switch to slow update immediately
            _currentInterval = INTERVAL_SLOW;
        }

        public void RemoveDataByPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            lock (_lock)
            {
                var keysToRemove = _data.Keys.Where(k => k.StartsWith(prefix)).ToList();
                foreach (var k in keysToRemove)
                {
                    _data.Remove(k);
                }
            }
        }

        /// <summary>
        /// Called from the refresh loop
        /// </summary>
        public void Update()
        {
            // 1. High Frequency: Time (Every tick)
            UpdateTimeInfo();

            // 2. Low Frequency: Network/Host (Based on interval)
            long now = Environment.TickCount64;
            if (now - _lastUpdateTick > _currentInterval)
            {
                _lastUpdateTick = now;
                UpdateData();
            }
        }

        private void UpdateTimeInfo()
        {
            var now = DateTime.Now;
            
            bool isNewSecond = now.Second != _lastSecond;

            // Only format time if second changed
            if (isNewSecond)
            {
                _lastSecond = now.Second;
                _lastTimeStr = now.ToString("ddd HH:mm:ss");
                SetData(KEY_TIME, _lastTimeStr);
            }

            // Uptime
            // Update every minute, hide seconds
            // QueryUnbiasedInterruptTime (no sleep) instead of TickCount64 (includes sleep)
            TimeSpan ts;
            try 
            {
                if (QueryUnbiasedInterruptTime(out ulong ticks))
                {
                    // minus the Fast Startup leftover
                    long realTicks = (long)ticks - _uptimeOffsetTicks;
                    if (realTicks < 0) realTicks = 0;
                    ts = TimeSpan.FromTicks(realTicks);
                }
                else
                {
                    // Fallback (should not happen):
                    // TickCount64 counts sleep and Fast Startup time
                    ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
                }
            }
            catch 
            {
                 ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
            }
            
            if (now.Minute != _lastUptimeMinute || string.IsNullOrEmpty(_lastUptimeStr))
            {
                _lastUptimeMinute = now.Minute;

                if (ts.TotalDays < 1)
                {
                    _lastUptimeStr = $"{ts.Hours}h {ts.Minutes}m";
                }
                else
                {
                    _lastUptimeStr = $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
                }
                
                SetData(KEY_UPTIME, _lastUptimeStr);
            }
        }

        private void UpdateData()
        {
            // Host info is static, only set once or if changed
            string currentHost = Environment.MachineName;
            if (GetValue(KEY_HOST) != currentHost)
            {
                SetData(KEY_HOST, currentHost);
            }

            // IP info is potentially slow, run async
            Task.Run(UpdateIPInfo);
        }

        private void UpdateIPInfo()
        {
            try
            {
                // Delegate to HardwareMonitor's NetworkManager (handles caching & multi-interface logic)
                string ip = HardwareMonitor.Instance?.GetNetworkIP();

                // Validate IP
                if (!string.IsNullOrEmpty(ip) && ip != DEFAULT_IP)
                {
                    // Only update if changed
                    if (GetValue(KEY_IP) != ip)
                    {
                        SetData(KEY_IP, ip);
                    }
                    _currentInterval = INTERVAL_SLOW; // Success -> Relax
                }
                else
                {
                    // Failed -> Retry fast
                    _currentInterval = INTERVAL_FAST;
                }
            }
            catch
            {
                _currentInterval = INTERVAL_FAST;
            }
        }

        private void SetData(string key, string value)
        {
            lock (_lock)
            {
                // Intern values to reduce duplicates (e.g. "KB/s", host names)
                // Do NOT intern dynamic time strings (Time, Uptime) as they change constantly and pollute the pool
                if (key == KEY_TIME || key == KEY_UPTIME)
                {
                    _data[key] = value;
                }
                else
                {
                    _data[key] = UIUtils.Intern(value);
                }
            }
        }
    }
}
