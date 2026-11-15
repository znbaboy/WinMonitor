// Ported from Halo Battery providers/xinput.py (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
//
// Xbox-protocol controllers through XInputGetBatteryInformation: four coarse levels
// (empty / low / medium / full), shown as an approximate level. Wired controllers have no reading.
// Not ported: Halo's Windows.Gaming.Input source for exact percentages.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WinMonitor.Services.Halo
{
    public sealed class XInputProvider : IBatteryProvider
    {
        public string Name => "xinput";

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_BATTERY_INFORMATION
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int GetState(int user, out XINPUT_STATE state);

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int GetBattery(int user, byte devType, out XINPUT_BATTERY_INFORMATION info);

        private const byte TypeDisconnected = 0x00, TypeWired = 0x01;
        private static readonly (int Level, string Word)[] Levels = { (5, "empty"), (20, "low"), (55, "medium"), (100, "full") };

        private bool _unavailable;
        internal readonly List<string> Diag = new();

        public List<DeviceStatus> Poll()
        {
            var output = new List<DeviceStatus>();
            Diag.Clear();
            if (_unavailable) { Diag.Add("  XInput not available"); return output; }
            try
            {
                for (int user = 0; user < 4; user++)
                {
                    if (GetState(user, out _) != 0) continue;                       // not connected
                    if (GetBattery(user, 0, out var bat) != 0) { Diag.Add($"  slot {user}: connected, battery query failed"); continue; }
                    Diag.Add($"  slot {user}: connected, battery type 0x{bat.BatteryType:x2}, level {bat.BatteryLevel}");
                    if (bat.BatteryType == TypeDisconnected || bat.BatteryType == TypeWired) continue;
                    if (bat.BatteryLevel > 3) continue;
                    var (level, word) = Levels[bat.BatteryLevel];
                    output.Add(new DeviceStatus($"xinput:{user}", "Xbox controller", level, false, true, "xinput",
                                                $"about {level}% ({word})", "gamepad"));
                }
            }
            catch (DllNotFoundException) { _unavailable = true; }
            catch (EntryPointNotFoundException) { _unavailable = true; }
            return output;
        }
    }
}
