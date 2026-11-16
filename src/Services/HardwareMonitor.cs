using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Runtime.InteropServices; // for working-set trimming
using System.Reflection; // for turning off sensor history
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;
using System.Linq;
using System.Threading;

namespace WinMonitor.Services
{
    public sealed class HardwareMonitor : IDisposable
    {
        #region Singleton & Events
        public static HardwareMonitor? Instance { get; private set; }
        public event Action? OnValuesUpdated;
        #endregion

        #region Private Fields
        private readonly Settings _cfg;
        private readonly Computer _computer;
        private readonly object _lock = new object();

        // Helper services
        private readonly SensorMap _sensorMap;
        private readonly NetworkManager _networkManager;
        private readonly DiskManager _diskManager;
        private readonly DriverInstaller _driverInstaller;
        private readonly HardwareValueProvider _valueProvider;

        // Performance counters
        private readonly PerformanceCounterManager _perfCounterManager;

        private readonly Dictionary<string, float> _lastValidMap = new();
        
        // Reload guard
        private volatile bool _isReloading = false;
        private volatile bool _isOpening = false;
        private bool _gpuMapRepairAttempted = false;

        // Timing
        private long _tickCounter = 0;
        private double _secondAccumulator = 0;
        private long _secondsCounter = 0;
        private DateTime _lastTrafficTime = DateTime.Now;
        #endregion

        #region Public Properties
        // The raw hardware tree (for the Hardware Info window)
        public IComputer ComputerInstance => _computer;
        public object SyncLock => _lock;
        #endregion

        #region Constructor
        public HardwareMonitor(Settings cfg)
        {
            _cfg = cfg;
            Instance = this;

            // 1. Set up LibreHardwareMonitor
            _computer = new Computer()
            {
                // CPU must always be on.
                // If it followed the CPU items, LibreHardwareMonitor would skip the CPU node when none is shown,
                // and sensors that depend on it (e.g. fans switched on later) could never be read,
                // because RefreshHardwareConfig only toggles IsControllerEnabled.
                IsCpuEnabled = true, 
                
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                
                // Controllers (fans / pumps) only when needed: scanning them can disturb USB devices
                IsControllerEnabled = ShouldEnableController(), 

                // Battery
                IsBatteryEnabled = true,

                // No power supply monitoring (only some high-end PSUs report anything)
                IsPsuEnabled = false
            };

            // Performance counters first (HardwareValueProvider uses them)
            _perfCounterManager = new PerformanceCounterManager();

            // 2. Helper services
            _sensorMap = new SensorMap();
            _networkManager = new NetworkManager(_perfCounterManager);
            _diskManager = new DiskManager();
            _driverInstaller = new DriverInstaller(cfg, ReloadComputerSafe, ReleaseComputerForDriverInstall);

            // Give the counters to HardwareValueProvider
            _valueProvider = new HardwareValueProvider(
                _computer, 
                cfg, 
                _sensorMap, 
                _networkManager, 
                _diskManager, 
                _perfCounterManager, 
                _lock, 
                _lastValidMap
            );

            // 3. Open the hardware in the background (keeps the UI responsive)
            InitializeAsync();
        }
        #endregion

        #region Public Methods

        public float? Get(string key)
        {
            return _isOpening ? _valueProvider.GetStartupValue(key) : _valueProvider.GetValue(key);
        }

        public string GetNetworkIP() => _networkManager.GetCurrentIP();

        // Lets the app run the driver check itself (avoids prompts at a bad moment)
        public Task SmartCheckDriver() => _driverInstaller.SmartCheckDriver();
        public Task<bool> InstallDriverFromSettings() => _driverInstaller.InstallFromSettings();

        // Switch hardware groups on the fly
        public void RefreshHardwareConfig()
        {
            // 1. Wanted state
            bool targetState = ShouldEnableController();

            // 2. Current state (a plain bool property)
            bool currentState = _computer.IsControllerEnabled;

            // 3. Nothing to do
            if (targetState == currentState) return;

            // Switching fans / pumps off does not reload (only the first switch-on does),
            // so hiding an item never stalls the app or reconnects devices.
            if (currentState && !targetState)
            {
                System.Diagnostics.Debug.WriteLine($"[HotSwap] Controller disable requested but ignored to avoid reload (Latch Mode).");
                return;
            }

            // 4. Off -> on: reload in the background
            System.Diagnostics.Debug.WriteLine($"[HotSwap] Controller State Change: {currentState} -> {targetState}");

            // Fire and forget (does not block the UI)
            Task.Run(() =>
            {
                // Update the setting
                _computer.IsControllerEnabled = targetState;

                // Reload safely.
                // ReloadComputerSafe holds _lock throughout, and UpdateAll / GetValue use TryEnter,
                // so the panel just shows empty values for a few seconds instead of freezing.
                ReloadComputerSafe();
            });
        }

        public void UpdateAll()
        {
            if (_isOpening) return;

            // Reload needed (hardware changed or failed)
            bool needsReload = false;

            try
            {
                // 1. Timers
                double timeDelta = UpdateTiming();

                // 2. What needs updating
                var requirements = CheckUpdateRequirements();

                _valueProvider.OnUpdateTickStarted();

                lock (_lock)
                {
                    // After the 10-minute safety rebuild, refresh the value cache so old sensor objects are not used.
                    if (_sensorMap.EnsureFresh(_computer, _cfg))
                        _valueProvider.PreCacheAllSensors(_sensorMap);

                    // every 3 s (slow scan)
                    bool isSlowScanTick = (_secondsCounter % 3 == 0); 
                    // every 10 s (background disk scan)
                    bool needDiskBgScan = (_secondsCounter % 10 == 0);
                    bool repairGpuMapAfterLoop = false;

                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu && requirements.NeedCpu) { hw.Update(); continue; }
                        
                        // GPU switching on hybrid laptops can make Update throw
                        if (IsGpu(hw) && requirements.NeedGpu) 
                        { 
                            if (!requirements.ForceAll && !ShouldUpdateGpuHardware(hw)) continue;

                            try
                            {
                                hw.Update();
                                if (ShouldRepairGpuSensorMap()) repairGpuMapAfterLoop = true;
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[GPU Error] Update failed: {ex.Message}");
                                needsReload = true;
                            }
                            continue; 
                        }

                        if (hw.HardwareType == HardwareType.Memory && requirements.NeedMem) { hw.Update(); continue; }
                        if (hw.HardwareType == HardwareType.Battery && requirements.NeedBat) { hw.Update(); continue; }

                        if (hw.HardwareType == HardwareType.Network && requirements.NeedNet)
                        {
                            // use the real time delta (requirements.TimeDelta is 0 here)
                            _networkManager.ProcessUpdate(hw, _cfg, timeDelta, isSlowScanTick);
                            continue;
                        }
                        if (hw.HardwareType == HardwareType.Storage && requirements.NeedDisk)
                        {
                            _diskManager.ProcessUpdate(hw, _cfg, isSlowScanTick, needDiskBgScan);
                            continue;
                        }
                        
                        // Motherboard / SuperIO (recursive)
                        if (IsMoboOrCooler(hw) && requirements.NeedMobo)
                        {
                             // Slow devices like SuperIO update every 3 s at most (less I/O blocking),
                             if (isSlowScanTick)
                             {
                                 UpdateWithSubHardware(hw);
                             }
                             continue;
                        }
                    }

                    if (repairGpuMapAfterLoop)
                    {
                        RepairGpuSensorMap();
                    }
                }
                
                // Spread-out maintenance (SystemOptimizer)
                SystemOptimizer.RunMaintenanceTasks(_secondsCounter);
                
                OnValuesUpdated?.Invoke();
            }
            catch { }

            // Recover from hardware failures:
            // a lost GPU or crashed driver triggers a safe reload
            if (needsReload && !_isReloading)
            {
                _isReloading = true;
                System.Diagnostics.Debug.WriteLine("[HardwareMonitor] Hardware change detected, triggering reload...");
                
                // Reload a little later so the hardware can settle
                Task.Run(async () => 
                {
                    try
                    {
                        await Task.Delay(2000); // wait 2 s for the hardware list to settle
                        ReloadComputerSafe();
                    }
                    finally
                    {
                        _isReloading = false;
                    }
                });
            }
        }


        public void Dispose()
        {
            // Lock, so this cannot run while UpdateAll is running
            lock (_lock)
            {
                try 
                {
                    // Close inside the lock
                    _computer.Close(); 
                }
                catch (Exception ex)
                {
                    // Ignore errors while closing
                    System.Diagnostics.Debug.WriteLine($"Dispose Error: {ex.Message}");
                }
            }

            _valueProvider.Dispose();
            _perfCounterManager.Dispose();
            _networkManager.ClearCache();
            _diskManager.ClearCache();
        }
        #endregion

        #region Private Helpers (Logic)

        private void InitializeAsync()
        {
            Task.Run(async () =>
            {
                _isOpening = true;
                try
                {
                    // Warm up the counters (does not block the UI)
                    _perfCounterManager.InitializeAsync();

                    lock (_lock)
                    {
                        // Opening two NVIDIA GPUs can be slow. Get() falls back to performance counters meanwhile,
                        // so this lock only holds the hardware tree steady for the settings page.
                        _computer.Open();
                        WarmUpMotherboardSensors();
                        WarmUpBatterySensors();

                        // Warm up GPUs only when GPU items are shown, so late sensors still get mapped.
                        _sensorMap.Rebuild(_computer, _cfg);
                        
                        // Put every sensor object in the value cache
                        _valueProvider.PreCacheAllSensors(_sensorMap);
                    }

                    _isOpening = false;

                    // After the first values: turn off sensor history and trim memory
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);

                        lock (_lock)
                        {
                            DisableSensorHistory();
                        }

                        // Clean up after startup
                        GC.Collect(2, GCCollectionMode.Forced, true, true);
                        SystemOptimizer.TrimWorkingSet();
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Init Error: {ex.Message}");
                }
                finally
                {
                    _isOpening = false;
                }
            });
        }

        // Are controllers (fans / pumps) needed?
        private bool ShouldEnableController()
        {
            // Any item that reads an external controller?
            // Motherboard temperature uses the Motherboard / SuperIO tree, not controllers.
            if (_cfg.IsAnyEnabled("CPU.Fan")) return true;
            if (_cfg.IsAnyEnabled("CPU.Pump")) return true;
            if (_cfg.IsAnyEnabled("CASE.Fan")) return true;
            return false;
        }

        private void WarmUpMotherboardSensors()
        {
            // The motherboard tree is always on. Warm it up once so its temperature gets mapped
            // by SensorMap.Rebuild even while hidden, and showing it later reads the cache.
            foreach (var hw in _computer.Hardware)
            {
                if (IsMotherboardSensorHardware(hw))
                {
                    try { UpdateWithSubHardware(hw); }
                    catch { }
                }
            }
        }

        private void WarmUpBatterySensors()
        {
            // Battery sensors may need one Update before they report values.
            // Only the battery is warmed up (a full update can stall with several GPUs / disks).
            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Battery)
                {
                    try { hw.Update(); }
                    catch { }
                }
            }
        }

        private double UpdateTiming()
        {
            // 1. Heartbeat (UpdateAll runs about once a second)
            _tickCounter++;
            
            // 2. Exact time delta (for network speeds)
            DateTime now = DateTime.Now;
            double timeDelta = (now - _lastTrafficTime).TotalSeconds;
            _lastTrafficTime = now;
            if (timeDelta > 5.0) timeDelta = 0; // ignore spikes after sleep

            // Refresh-rate independent timing:
            // whether UpdateAll runs every 300 ms or every 2 s,
            // time is added up and _secondsCounter only grows per real second,
            // so the % 60 / % 600 checks follow real time, not call counts.
            _secondAccumulator += timeDelta;
            while (_secondAccumulator >= 1.0)
            {
                _secondAccumulator -= 1.0;
                _secondsCounter++;
            }

            return timeDelta;
        }

        /// <summary>
        /// True when the sensor behind <paramref name="key"/> is refreshed on every tick anyway
        /// (its hardware is polled for another visible item), so recording it costs nothing extra.
        /// Mirrors <see cref="CheckUpdateRequirements"/>; hidden hardware is never woken up for history.
        /// </summary>
        public bool IsReadInBackground(string key)
        {
            var r = CheckUpdateRequirements();
            string group = key.Split('.')[0].ToUpperInvariant();
            return group switch
            {
                "CPU" => r.NeedCpu,
                "GPU" => r.NeedGpu,
                // counters or LHM both only run while a memory item is visible
                "MEM" => _cfg.IsAnyEnabled("MEM"),
                "DISK" => r.NeedDisk,
                "MOBO" => r.NeedMobo,
                "BAT" => r.NeedBat,
                _ => false
            };
        }

        private (bool ForceAll, bool NeedCpu, bool NeedGpu, bool NeedMem, bool NeedNet, bool NeedDisk, bool NeedBat, bool NeedMobo, double TimeDelta) CheckUpdateRequirements()
        {
            // === What needs updating ===
        
            // 1. Counter state
            bool useCounter = _cfg.UseWinPerCounters && _perfCounterManager.IsInitialized;
            
            // the web display (which needed every sensor) is gone; only enabled items are read
            bool forceAll = false;

            // 2. CPU: always (temperatures come from LibreHardwareMonitor)
            bool needCpu = forceAll || _cfg.IsAnyEnabled("CPU");
            
            // 3. GPU: always
            bool needGpu = forceAll || _cfg.IsAnyEnabled("GPU");
            
            // 4. Memory: not needed when the counters provide it
            bool needMem = forceAll || (_cfg.IsAnyEnabled("MEM") && !useCounter);
            
            // 5. Network: unchanged
            bool needNet = forceAll || _cfg.IsAnyEnabled("NET") || _cfg.IsAnyEnabled("DATA");
            
            // 7. Battery: only when shown
            bool needBat = forceAll || _cfg.IsAnyEnabled("BAT");

            // 6. Disks:
            // only when counters are off, a disk is chosen, or temperature is shown
            bool needDiskTemp = _cfg.IsAnyEnabled("DISK.Temp");
            bool hasSpecificDisk = !string.IsNullOrEmpty(_cfg.PreferredDisk);
            bool needDiskSpeed = _cfg.IsAnyEnabled("DISK") && (!useCounter || hasSpecificDisk);
            bool needDisk = forceAll || needDiskSpeed || needDiskTemp;

            // Motherboard
            bool needMobo = forceAll ||
                _cfg.IsAnyEnabled("MOBO") ||
                _cfg.IsAnyEnabled("CPU.Fan") ||
                _cfg.IsAnyEnabled("CPU.Pump") ||
                _cfg.IsAnyEnabled("CASE.Fan");
            
            return (forceAll, needCpu, needGpu, needMem, needNet, needDisk, needBat, needMobo, 0);
        }

        private void ReloadComputerSafe()
        {
            try
            {
                lock (_lock)
                {
                    // 1. Clear value caches
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    _gpuMapRepairAttempted = false;
                    
                    _valueProvider.ClearCache();
                    
                    // Clear scanner caches
                    HardwareScanner.ClearCache();

                    // 2. Clear the string pool
                    UIUtils.ClearStringPool();

                    // 3. Close the old hardware
                    if (_computer != null)
                    {
                        _computer.Accept(new HardwareVisitor(h => { }));
                        _computer.Close();
                        // Clear the hardware list by hand:
                        // LibreHardwareMonitor's Close() keeps it, so opening again would add duplicates
                        _computer.Hardware.Clear();
                    }
                    
                    _computer.Open();
                    WarmUpMotherboardSensors();
                    WarmUpBatterySensors();

                    DisableSensorHistory();

                    // Building the map updates GPUs, so it must hold the same lock as reading and closing.
                    _sensorMap.Rebuild(_computer, _cfg);
                    _valueProvider.PreCacheAllSensors(_sensorMap);
                }

                // 4. Trim memory again after a reset
                GC.Collect();
                SystemOptimizer.TrimWorkingSet();
            }
            catch { }
        }

        private void ReleaseComputerForDriverInstall()
        {
            try
            {
                lock (_lock)
                {
                    _networkManager.ClearCache();
                    _diskManager.ClearCache();
                    _sensorMap.Clear();
                    _gpuMapRepairAttempted = false;
                    _valueProvider.ClearCache();
                    HardwareScanner.ClearCache();

                    try
                    {
                        _computer.Accept(new HardwareVisitor(h => { }));
                        _computer.Close();
                        _computer.Hardware.Clear();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DriverInstaller] could not release the sensors: {ex.Message}");
                    }
                }
            }
            catch { }
        }

        // =========================================================
        // Turn off sensor history:
        // stops LibreHardwareMonitor keeping 24 hours of values in memory
        // =========================================================
        private void DisableSensorHistory()
        {
            try { _computer.Accept(new DisableHistoryVisitor()); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemoryFix] Failed: {ex.Message}"); }
        }

        // Update sub-hardware too (refreshes SuperIO)
        private void UpdateWithSubHardware(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) UpdateWithSubHardware(sub);
        }

        private static bool IsGpu(IHardware hw)
        {
            return hw.HardwareType == HardwareType.GpuNvidia || 
                   hw.HardwareType == HardwareType.GpuAmd || 
                   hw.HardwareType == HardwareType.GpuIntel;
        }

        private bool ShouldUpdateGpuHardware(IHardware hw)
        {
            var activeGpu = _sensorMap.CachedGpu;
            if (activeGpu == null) return true;

            // A preferred GPU: update only that one (as before).
            if (_sensorMap.HasExplicitGpuSelection)
            {
                return ReferenceEquals(hw, activeGpu);
            }

            // Automatic: update the default GPU and any other GPU that provides GPU.* values.
            return ReferenceEquals(hw, activeGpu) || _sensorMap.IsMappedGpuSensorHardware(hw);
        }

        private bool ShouldRepairGpuSensorMap()
        {
            if (_gpuMapRepairAttempted || !_cfg.IsAnyEnabled("GPU")) return false;

            if (IsEnabledGpuSensorMissing("GPU.Load")) return true;
            if (IsEnabledGpuSensorMissing("GPU.Temp")) return true;
            if (IsEnabledGpuSensorMissing("GPU.Fan")) return true;

            if (_cfg.IsAnyEnabled("GPU.VRAM") &&
                !HasMappedSensor("GPU.VRAM.Used") &&
                !HasMappedSensor("GPU.VRAM.Total") &&
                !HasMappedSensor("GPU.VRAM.Load"))
            {
                return true;
            }

            return false;
        }

        private bool IsEnabledGpuSensorMissing(string key)
        {
            return _cfg.IsAnyEnabled(key) && !HasMappedSensor(key);
        }

        private bool HasMappedSensor(string key)
        {
            return _sensorMap.TryGetSensor(key, out var sensor) && sensor != null;
        }

        private void RepairGpuSensorMap()
        {
            if (_gpuMapRepairAttempted) return;

            _gpuMapRepairAttempted = true;
            _sensorMap.Rebuild(_computer, _cfg);
            _valueProvider.PreCacheAllSensors(_sensorMap);
        }

        private static bool IsMoboOrCooler(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard || 
                   hw.HardwareType == HardwareType.SuperIO || 
                   hw.HardwareType == HardwareType.Cooler;
        }

        private static bool IsMotherboardSensorHardware(IHardware hw)
        {
            return hw.HardwareType == HardwareType.Motherboard ||
                   hw.HardwareType == HardwareType.SuperIO;
        }
        #endregion

        #region Static UI Helpers (Delegated to HardwareScanner)
        public static string GenerateSmartName(ISensor sensor, IHardware hardware) => 
            HardwareScanner.GenerateSmartName(sensor, hardware, Instance!._computer);

        public static List<string> ListAllNetworks() => HardwareScanner.ListAllNetworks(Instance!._computer);

        public static List<string> ListAllDisks() => HardwareScanner.ListAllDisks(Instance!._computer);

        public static List<string> ListAllGpus()
        {
            lock (Instance!._lock)
                return HardwareScanner.ListAllGpus(Instance!._computer);
        }

        public static List<HardwareScanner.GpuOption> ListAllGpuOptions()
        {
            lock (Instance!._lock)
                return HardwareScanner.ListAllGpuOptions(Instance!._computer);
        }

        public static List<string> ListAllFans() => HardwareScanner.ListAllFans(Instance!._computer, Instance!._lock);

        public static List<string> ListAllMoboTemps() => HardwareScanner.ListAllMoboTemps(Instance!._computer, Instance!._lock);
        #endregion

        #region Inner Visitors
        // Turn off sensor history
        /// <summary>
        /// Sets every sensor's ValuesTimeWindow to 0
        /// </summary>
        private class DisableHistoryVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
                foreach (var sensor in hardware.Sensors) VisitSensor(sensor);
            }
            public void VisitSensor(ISensor sensor)
            {
                // ValuesTimeWindow = TimeSpan.Zero, via reflection
                // (ISensor does not expose it; it is on the concrete Sensor class)
                try
                {
                    var prop = sensor.GetType().GetProperty("ValuesTimeWindow");
                    if (prop != null && prop.CanWrite) prop.SetValue(sensor, TimeSpan.Zero);
                }
                catch { }
            }
            public void VisitParameter(IParameter parameter) { }
        }

        // Visitor that applies it
        private class HardwareVisitor : IVisitor
        {
            private readonly Action<IHardware> _action;
            public HardwareVisitor(Action<IHardware> action) { _action = action; }
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                _action(hardware);
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
        #endregion
    }
}
