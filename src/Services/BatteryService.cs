using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using WinMonitor.Core;

namespace WinMonitor.Services
{
    /// <summary>
    /// Laptop battery values (and a simulation for testing the UI)
    /// </summary>
    public static class BatteryService
    {
        /// <summary>
        /// Battery value for a key
        /// </summary>
        public static float? GetBatteryValue(string key, Dictionary<string, ISensor> sensorCache)
        {
            // 1. Simulation (UI testing)
            bool simulateBattery = false; // off by default
            if (simulateBattery)
            {
                return GetSimulatedValue(key);
            }

            // 2. Real hardware
            if (sensorCache.TryGetValue(key, out var sensor) && sensor.Value.HasValue)
            {
                float val = sensor.Value.Value;

                // Sign: positive while charging, negative while discharging
                if (key == "BAT.Power" || key == "BAT.Current")
                {
                    var powerStatus = MetricUtils.GetPowerStatus();
                    
                    // Drivers disagree on the sign,
                    // so it is set from the power state alone:
                    
                    if (powerStatus.AcOnline)
                    {
                        // On AC power -> charging -> positive
                        val = Math.Abs(val);
                    }
                    else
                    {
                        // On battery -> discharging -> negative
                        // always negative
                        val = -Math.Abs(val);
                        

                    }
                }
                return val;
            }

            return null;
        }

        private static float? GetSimulatedValue(string key)
        {
            var now = DateTime.Now;
            int sec = now.Second;

            // First 30 s: heavy discharge, next 30 s: fast charge
            bool isCharging = sec >= 30;
            
            float voltage = isCharging 
                ? 15.5f + ((sec - 30) * 0.05f) 
                : 16.8f - (sec * 0.06f);

            float power = isCharging ? -65.0f - (sec % 5) * 4.0f : 25.0f + (sec % 3) * 5.0f;
            float current = power / voltage;
            float percent = isCharging ? (sec - 30) * (100.0f / 30.0f) : 100.0f - (sec * (100.0f / 30.0f));

            return key switch
            {
                "BAT.Percent" => Math.Clamp(percent, 0f, 100f),
                "BAT.Power" => power,
                "BAT.Voltage" => voltage,
                "BAT.Current" => current,
                _ => null
            };
        }
    }
}
