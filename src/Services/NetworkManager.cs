using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;
using Debug = System.Diagnostics.Debug;

namespace WinMonitor.Services
{
    public class NetworkManager
    {
        // State per adapter (not shared)
        private class NetworkState
        {
            public NetworkInterface? NativeAdapter;
            public long LastNativeUpload;
            public long LastNativeDownload;
            public DateTime LastMatchAttempt = DateTime.MinValue;
            
            // LibreHardwareMonitor sensors
            public ISensor? CachedUpSensor;
            public ISensor? CachedDownSensor;
        }
        private readonly Dictionary<IHardware, NetworkState> _netStates = new();
        
        // Selected adapter cache
        private IHardware? _cachedNetHw;
        private DateTime _lastNetScan = DateTime.MinValue;
        private readonly DateTime _startTime = DateTime.Now; // start time
        private static string _staticIPCache = ""; // static IP cache, kept across reloads (no flicker)
        private static DateTime _lastIPCheckTime = DateTime.MinValue; // last IP check
        private volatile bool _shouldResetAdapters = false; // set when the network changed

        // Performance counters (for SMB traffic)
        private readonly PerformanceCounterManager _perfManager;

        public NetworkManager(PerformanceCounterManager perfManager = null)
        {
            // May be null: SMB filtering is then off
            _perfManager = perfManager ?? new PerformanceCounterManager(); 
            
            // Network address changes reset the IP and adapter caches
            // (switching Wi-Fi or plugging a cable invalidates both)
            NetworkChange.NetworkAddressChanged += (s, e) => {
                _staticIPCache = null; 
                _shouldResetAdapters = true;
            };
        }

        public void ClearCache()
        {
            _netStates.Clear();
            _cachedNetHw = null;
        }

        // ===========================================================
        // Update
        // ===========================================================
        public void ProcessUpdate(IHardware hw, Settings cfg, double timeDelta, bool isSlowScanTick)
        {
            bool isTarget = (_cachedNetHw != null && hw == _cachedNetHw) ||
                            (hw.Name == cfg.LastAutoNetwork) ||
                            (hw.Name == cfg.PreferredNetwork);

            bool isStartupPhase = (DateTime.Now - _startTime).TotalSeconds < 3;

            if (isTarget)
            {
                hw.Update();
                AccumulateTraffic(hw, cfg, timeDelta);
            }
            else if (isStartupPhase || IsVirtualNetwork(hw.Name))
            {
                return;
            }
            else if (isSlowScanTick)
            {
                hw.Update();
            }
        }

        // Current IP (cached)
        public string GetCurrentIP()
        {
            // After a network change, reset every adapter
            // so MatchNativeNetworkAdapter finds the current NetworkInterface (and the new IP)
            if (_shouldResetAdapters)
            {
                try
                {
                    foreach (var state in _netStates.Values)
                    {
                        state.NativeAdapter = null;
                        state.LastMatchAttempt = DateTime.MinValue; // allow re-matching now
                    }
                    _shouldResetAdapters = false;
                }
                catch 
                { 
                    // concurrent change: keep the flag and retry next time
                }
            }

            // 1. Use the cache only when it holds a valid IP checked less than 30 s ago;
            // an empty cache is always retried
            if (!string.IsNullOrEmpty(_staticIPCache) && (DateTime.Now - _lastIPCheckTime).TotalSeconds < 30)
            {
                return _staticIPCache;
            }

            string foundIP = null;

            // 2. From the matched adapters (the most direct way)
            try 
            {
                // Also look in _netStates when no adapter is locked yet:
                // matching can succeed before _cachedNetHw is set,
                // and this used to skip it.
                
                // Locked adapter first
                if (_cachedNetHw != null && _netStates.TryGetValue(_cachedNetHw, out var state) && state.NativeAdapter != null)
                {
                     foundIP = GetIPv4FromAdapter(state.NativeAdapter);
                }
                
                // Otherwise any adapter that has traffic
                if (string.IsNullOrEmpty(foundIP))
                {
                    foreach (var kv in _netStates)
                    {
                        if (kv.Value.NativeAdapter != null)
                        {
                            string ip = GetIPv4FromAdapter(kv.Value.NativeAdapter);
                            if (!string.IsNullOrEmpty(ip))
                            {
                                foundIP = ip;
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Fallback: enumerate the system's adapters (once)
            if (string.IsNullOrEmpty(foundIP))
            {
                try
                {
                    var nics = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in nics)
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up) continue;
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        if (IsVirtualNetwork(nic.Description) || IsVirtualNetwork(nic.Name)) continue; // physical adapters only

                        foundIP = GetIPv4FromAdapter(nic);
                        if (!string.IsNullOrEmpty(foundIP)) break;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(foundIP))
            {
                _staticIPCache = foundIP;
                _lastIPCheckTime = DateTime.Now;
                return foundIP;
            }
            
            return _staticIPCache; 
        }

        private string GetIPv4FromAdapter(NetworkInterface nic)
        {
            try
            {
                var props = nic.GetIPProperties();
                string bestIP = null;

                foreach (var ip in props.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ipStr = ip.Address.ToString();
                        
                        // Skip APIPA (169.254.x.x): a temporary address after DHCP fails
                        if (ipStr.StartsWith("169.254.")) continue;

                        // Prefer 192.168.x.x local addresses
                        if (ipStr.StartsWith("192.168."))
                        {
                            return ipStr;
                        }

                        // Keep the first IPv4 as a fallback
                        if (bestIP == null)
                        {
                            bestIP = ipStr;
                        }
                    }
                }
                return bestIP;
            }
            catch { }
            return null;
        }

        // ===========================================================
        // Best network value (a preferred adapter is handled by HardwareValueProvider)
        // ===========================================================
        public float? GetBestValue(string key, Computer computer, Settings cfg, Dictionary<string, float> lastValidMap, object syncLock)
        {
            // 2. Automatic choice (cached)
            // A. Runtime cache
            if (_cachedNetHw != null)
            {
                // Drop the cached adapter if it no longer exists
                if (!computer.Hardware.Contains(_cachedNetHw))
                {
                    _cachedNetHw = null;
                }
                else
                {
                    float? cachedVal = ReadNetworkSensor(_cachedNetHw, key, lastValidMap, syncLock);
                    // Traffic, or less than 3 s since the last full scan: use it
                    if ((cachedVal.HasValue && cachedVal.Value > 0.1f) ||
                        (DateTime.Now - _lastNetScan).TotalSeconds < 3)
                    {
                        return cachedVal;
                    }
                }
            }

            // B. Adapter remembered in the settings
            if (_cachedNetHw == null && !string.IsNullOrEmpty(cfg.LastAutoNetwork))
            {
                // Try the remembered adapter
                var savedHw = computer.Hardware.FirstOrDefault(h => h.Name == cfg.LastAutoNetwork);
                if (savedHw != null)
                {
                    // Found: cache it and skip the full scan
                    _cachedNetHw = savedHw;
                    _lastNetScan = DateTime.Now;
                    return ReadNetworkSensor(savedHw, key, lastValidMap, syncLock);
                }
            }

            // C. Scan all adapters
            IHardware? bestHw = null;
            double bestScore = double.MinValue;
            ISensor? bestTarget = null;

            foreach (var hw in computer.Hardware.Where(h => h.HardwareType == HardwareType.Network))
            {
                double penalty = IsVirtualNetwork(hw.Name) ? -1e9 : 0;
                ISensor? up = null, down = null;
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) up ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) down ??= s;
                }
                if (up == null && down == null) continue;
                double score = (up?.Value ?? 0) + (down?.Value ?? 0) + penalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestHw = hw;
                    bestTarget = (key == "NET.Up") ? up : down;
                }
            }

            // D. Update the cache
            if (bestHw != null)
            {
                _cachedNetHw = bestHw;
                _lastNetScan = DateTime.Now;
                
                // Remember the choice
                if (cfg.LastAutoNetwork != bestHw.Name)
                {
                    cfg.LastAutoNetwork = bestHw.Name;
                }
            }

            // E. Return
            if (bestTarget?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        private float? ReadNetworkSensor(IHardware hw, string key, Dictionary<string, float> lastValidMap, object syncLock)
        {
            ISensor? target = null;
            foreach (var s in hw.Sensors)
            {
                if (s.SensorType != SensorType.Throughput) continue;
                if (key == "NET.Up" && _upKW.Any(k => SensorMap.Has(s.Name, k))) { target = s; break; } 
                if (key == "NET.Down" && _downKW.Any(k => SensorMap.Has(s.Name, k))) { target = s; break; }
            }

            if (target?.Value is float v && !float.IsNaN(v))
            {
                lock (syncLock) lastValidMap[key] = v;
                return v;
            }
            lock (syncLock) { if (lastValidMap.TryGetValue(key, out var last)) return last; }
            return null;
        }

        // ===========================================================
        // Traffic: estimate vs native counters
        // ===========================================================
        private void AccumulateTraffic(IHardware hw, Settings cfg, double seconds)
        {
            // 1. This adapter's state
            if (!_netStates.TryGetValue(hw, out var state))
            {
                state = new NetworkState();
                _netStates[hw] = state;
            }

            long finalUp = 0;
            long finalDown = 0;

            // A. LibreHardwareMonitor estimate
            if (state.CachedUpSensor == null || state.CachedDownSensor == null)
            {
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType != SensorType.Throughput) continue;
                    if (_upKW.Any(k => SensorMap.Has(s.Name, k))) state.CachedUpSensor ??= s;
                    if (_downKW.Any(k => SensorMap.Has(s.Name, k))) state.CachedDownSensor ??= s;
                }
            }
            long lhmUpDelta = (long)((state.CachedUpSensor?.Value ?? 0) * seconds);
            long lhmDownDelta = (long)((state.CachedDownSensor?.Value ?? 0) * seconds);

            // B. Native (exact) value
            MatchNativeNetworkAdapter(hw.Name, state);
            
            bool nativeValid = false;
            long nativeUpDelta = 0;
            long nativeDownDelta = 0;

            if (state.NativeAdapter != null)
            {
                try
                {
                    var stats = state.NativeAdapter.GetIPStatistics();
                    long currUp = stats.BytesSent;
                    long currDown = stats.BytesReceived;

                    if (currUp >= state.LastNativeUpload) nativeUpDelta = currUp - state.LastNativeUpload;
                    if (currDown >= state.LastNativeDownload) nativeDownDelta = currDown - state.LastNativeDownload;

                    state.LastNativeUpload = currUp;
                    state.LastNativeDownload = currDown;
                    nativeValid = true;
                }
                catch { state.NativeAdapter = null; }
            }

            // C. Decide
            if (nativeValid)
            {
                if ((nativeUpDelta + nativeDownDelta == 0) && (lhmUpDelta + lhmDownDelta > 51200))
                {
                    // wrong match
                    finalUp = lhmUpDelta;
                    finalDown = lhmDownDelta;
                    state.NativeAdapter = null; 
                }
                else
                {
                    finalUp = nativeUpDelta;
                    finalDown = nativeDownDelta;
                }
            }
            else
            {
                finalUp = lhmUpDelta;
                finalDown = lhmDownDelta;
            }

            // Leave out local network (SMB) traffic
            // when switched on and the counters are ready
            if (cfg.IgnoreSmbTraffic)
            {
                if (_perfManager.IsInitialized)
                {
                    // Estimated SMB traffic (includes a protocol overhead factor)
                    var smb = _perfManager.GetEstimatedSmbBytes(seconds);
                    
                    if (smb.UpBytes > 0 || smb.DownBytes > 0)
                    {
                        // Subtract (never below zero: sampling times differ)
                        if (smb.UpBytes > 0) finalUp = Math.Max(0, finalUp - smb.UpBytes);
                        if (smb.DownBytes > 0) finalDown = Math.Max(0, finalDown - smb.DownBytes);
                    }
                }
                else
                {
                    // Debug.WriteLine("[SMB_DEBUG] IgnoreSmbTraffic=True but PerfManager NOT Initialized!");
                }
            }

            // D. Store
            // Safety: a single step above 10 GB is discarded
            if (finalUp > 10737418240L || finalDown > 10737418240L) return;

            if (finalUp > 0 || finalDown > 0)
            {
                cfg.SessionUploadBytes += finalUp;
                cfg.SessionDownloadBytes += finalDown;
                TrafficLogger.AddTraffic(finalUp, finalDown);
            }
        }

        private void MatchNativeNetworkAdapter(string lhmName, NetworkState state)
        {
            if (state.NativeAdapter != null) return;
            if ((DateTime.Now - state.LastMatchAttempt).TotalSeconds < 10) return;
            state.LastMatchAttempt = DateTime.Now;

            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                
                // Pre-size the token list
                var lhmTokens = new List<string>(capacity: 10);
                SplitTokens(lhmName, lhmTokens);

                foreach (var nic in nics)
                {
                    // 1. Connection name
                    if (nic.Name.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); return; }
                    // 2. Hardware description
                    if (nic.Description.Equals(lhmName, StringComparison.OrdinalIgnoreCase)) { SetNativeAdapter(nic, state); return; }
                    // 3. Fuzzy match
                    if (lhmTokens.Count > 0 && lhmName.Length > 5) 
                    {
                        var nicTokens = new List<string>(capacity: 10);
                        SplitTokens(nic.Description, nicTokens);
                        
                        // Low-allocation matching
                        int matchCount = 0;
                        foreach (var token in lhmTokens)
                        {
                            if (nicTokens.Contains(token, StringComparer.OrdinalIgnoreCase))
                            {
                                matchCount++;
                                if (matchCount > 2) break; // enough matches
                            }
                        }
                        
                        if (matchCount > 2 && (double)matchCount / lhmTokens.Count > 0.6)
                        {
                            SetNativeAdapter(nic, state); 
                            return;
                        }
                    }
                }
            }
            catch { state.NativeAdapter = null; }
        }
        private static readonly char[] _tokenSeparators = { ' ', '(', ')', '[', ']', '-', '_', '#' };
        // Token splitting into a pre-sized list (fewer allocations)
        private void SplitTokens(string input, List<string> result)
        {
            result.Clear();
            int startIndex = 0;
            int length = input.Length;
            //char[] _tokenSeparators = { ' ', '(', ')', '[', ']', '-', '_', '#' };
            
            for (int i = 0; i < length; i++)
            {
                if (Array.IndexOf(_tokenSeparators, input[i]) >= 0)
                {
                    if (i > startIndex)
                    {
                        result.Add(UIUtils.Intern(input.Substring(startIndex, i - startIndex)));
                    }
                    startIndex = i + 1;
                }
            }
            
            if (startIndex < length)
            {
                result.Add(UIUtils.Intern(input.Substring(startIndex)));
            }
        }
        
        // Kept for compatibility
        private List<string> SplitTokens(string input)
        {
            var result = new List<string>(capacity: 10);
            SplitTokens(input, result);
            return result;
        }

        private void SetNativeAdapter(NetworkInterface nic, NetworkState state)
        {
            state.NativeAdapter = nic;
            try
            {
                var stats = nic.GetIPStatistics();
                state.LastNativeUpload = stats.BytesSent;
                state.LastNativeDownload = stats.BytesReceived;
            }
            catch { state.NativeAdapter = null; }
        }

        private bool IsVirtualNetwork(string name)
        {
            foreach (var k in _virtualNicKW)
            {
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static readonly string[] _upKW = { "upload", "up", "sent", "send", "tx", "transmit" };
        private static readonly string[] _downKW = { "download", "down", "received", "receive", "rx" };
        private static readonly string[] _virtualNicKW = { "virtual", "vmware", "hyper-v", "hyper v", "vbox", "loopback", "tunnel", "tap", "tun", "bluetooth", "zerotier", "tailscale", "wan miniport" };
    }
}