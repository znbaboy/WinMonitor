// Ported from Halo Battery providers/bluetooth.py (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
//
// Bluetooth devices whose battery Windows itself knows (Settings > Bluetooth & devices):
//   battery:   DEVPKEY_Bluetooth_Battery {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2
//   connected: {83DA6326-97A6-4088-9453-A1923F573B29} 15 (Halo's PnP fallback; Halo prefers WinRT)
// The nodes of one device are matched by the MAC address in their instance ids.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WinMonitor.Services.Halo
{
    public sealed class BluetoothProvider : IBatteryProvider
    {
        public string Name => "bluetooth";

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
            public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
        }

        private static readonly DEVPROPKEY KeyBattery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        private static readonly DEVPROPKEY KeyConnected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);
        private static readonly DEVPROPKEY KeyFriendlyName = new("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        private static readonly DEVPROPKEY KeyName = new("B725F130-47EF-101A-A5F1-02608C9EEBAC", 10);
        private static readonly DEVPROPKEY KeyClassOfDevice = new("2BD67D8B-8BEB-48D5-87E0-6CDA3428040A", 10);  // classic only
        // audio service nodes (A2DP sink, HFP, HSP, HSP HS) mark a headset
        private static readonly string[] AudioServices = { "0000110b-", "0000111e-", "00001108-", "00001131-" };
        private const uint DEVPROP_TYPE_UINT32 = 0x7;

        /// <summary>Halo kind_from_class() for a classic Class of Device value.</summary>
        internal static string KindFromClass(uint raw)
        {
            uint major = (raw >> 8) & 0x1F, minor = (raw >> 2) & 0x3F;
            if (major == 4) return minor == 1 || minor == 2 || minor == 6 ? "headset" : "";
            if (major == 5)
            {
                if ((minor & 0x0F) == 1 || (minor & 0x0F) == 2) return "gamepad";
                return (minor >> 4) switch { 1 => "keyboard", 2 => "mouse", 3 => "keyboard", _ => "" };
            }
            return "";
        }

        private static readonly Regex DevMac = new(@"DEV_([0-9A-F]{12})(?![0-9A-F])", RegexOptions.Compiled);
        private static readonly Regex Hex12 = new(@"(?<![0-9A-F])([0-9A-F]{12})(?![0-9A-F])", RegexOptions.Compiled);
        private static readonly Regex Root = new(@"^(BTHENUM|BTHLE)\\DEV_[0-9A-F]{12}", RegexOptions.Compiled);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List_Size(out int len, string filter, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_Device_ID_List(string filter, char[] buffer, int len, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Locate_DevNode(out uint devInst, string deviceId, int flags);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);

        private const uint DN_STARTED = 0x00000008;

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Get_DevNode_Property(uint devInst, ref DEVPROPKEY key, out uint type,
                                                          byte[]? buffer, ref int size, int flags);

        private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1, CM_GETIDLIST_FILTER_PRESENT = 0x100;
        private const uint DEVPROP_TYPE_BYTE = 0x3, DEVPROP_TYPE_BOOLEAN = 0x11, DEVPROP_TYPE_STRING = 0x12;

        internal static string? MacOf(string id)
        {
            string s = (id ?? "").ToUpperInvariant();
            var m = DevMac.Match(s);
            if (m.Success) return m.Groups[1].Value;
            var all = Hex12.Matches(s);
            return all.Count > 0 ? all[all.Count - 1].Groups[1].Value : null;
        }

        private static List<string> DeviceIds(string enumerator)
        {
            var list = new List<string>();
            int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (CM_Get_Device_ID_List_Size(out int len, enumerator, flags) != 0 || len <= 1) return list;
                var buf = new char[len];
                int cr = CM_Get_Device_ID_List(enumerator, buf, len, flags);
                if (cr == 0x1A) continue;
                if (cr != 0) return list;
                int start = 0;
                for (int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] != '\0') continue;
                    if (i > start) list.Add(new string(buf, start, i - start));
                    start = i + 1;
                }
                return list;
            }
            return list;
        }

        private static byte[]? Prop(uint inst, DEVPROPKEY key, out uint type)
        {
            int size = 0;
            type = 0;
            CM_Get_DevNode_Property(inst, ref key, out type, null, ref size, 0);
            if (size <= 0) return null;
            var buf = new byte[size];
            return CM_Get_DevNode_Property(inst, ref key, out type, buf, ref size, 0) == 0 ? buf : null;
        }

        private static string? StringProp(uint inst, DEVPROPKEY key)
        {
            var b = Prop(inst, key, out uint type);
            if (b == null || type != DEVPROP_TYPE_STRING) return null;
            string s = Encoding.Unicode.GetString(b);
            int nul = s.IndexOf('\0');
            return (nul >= 0 ? s.Substring(0, nul) : s).Trim();
        }

        internal readonly List<string> Diag = new();

        private sealed class Group
        {
            public int? Level;
            public bool? Connected;
            public bool Started;
            public string Kind = "";
            public bool Audio;
            public string? Name, RootName;
        }

        public List<DeviceStatus> Poll()
        {
            var byMac = new Dictionary<string, Group>();
            foreach (var enumerator in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
            {
                foreach (var id in DeviceIds(enumerator))
                {
                    string? mac = MacOf(id);
                    if (mac == null) continue;
                    if (CM_Locate_DevNode(out uint inst, id, 0) != 0) continue;   // not present
                    if (!byMac.TryGetValue(mac, out var g)) byMac[mac] = g = new Group();

                    string? name = StringProp(inst, KeyFriendlyName) ?? StringProp(inst, KeyName);
                    string lower = id.ToLowerInvariant();
                    foreach (var svc in AudioServices) if (lower.Contains(svc)) g.Audio = true;
                    if (g.Kind == "")
                    {
                        var cod = Prop(inst, KeyClassOfDevice, out uint codType);
                        if (cod != null && codType == DEVPROP_TYPE_UINT32 && cod.Length >= 4) g.Kind = KindFromClass(BitConverter.ToUInt32(cod, 0));
                    }
                    if (Root.IsMatch(id.ToUpperInvariant()) && !string.IsNullOrEmpty(name)) g.RootName = name;

                    var bat = Prop(inst, KeyBattery, out uint bt);
                    if (bat != null && bt == DEVPROP_TYPE_BYTE && bat.Length >= 1)
                    {
                        g.Level = bat[0];
                        g.Name ??= name;
                        if (CM_Get_DevNode_Status(out uint st, out _, inst, 0) == 0 && (st & DN_STARTED) != 0) g.Started = true;
                    }
                    var conn = Prop(inst, KeyConnected, out uint ct);
                    if (conn != null && ct == DEVPROP_TYPE_BOOLEAN && conn.Length >= 1)
                        g.Connected = (g.Connected ?? false) || conn[0] != 0;
                }
            }

            var output = new List<DeviceStatus>();
            Diag.Clear();
            foreach (var (mac, g) in byMac)
            {
                if (g.Level != null || g.RootName != null)
                    Diag.Add($"  {g.RootName ?? g.Name ?? mac} ({mac}): battery {(g.Level?.ToString() ?? "none")}, " +
                             $"connected flag {(g.Connected?.ToString() ?? "none")}, started {g.Started}, kind '{g.Kind}', audio {g.Audio}");
                if (g.Level == null || g.Level < 0 || g.Level > 100) continue;
                // paired but not connected: Windows keeps the old value. No connection flag: Halo's
                // fallback, the battery node is running.
                bool shown = g.Connected ?? g.Started;
                if (!shown) continue;
                string name = g.RootName ?? g.Name ?? $"Bluetooth {mac}";
                string kind = g.Kind != "" ? g.Kind : (g.Audio ? "headset" : "");
                output.Add(new DeviceStatus($"bt:{mac}", name, g.Level, false, true, "bluetooth", kind: kind));
            }
            return output;
        }
    }
}
