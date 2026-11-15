// Built-in Halo Battery support for LiteMonitor.
// Device protocols ported from Halo Battery (https://github.com/HeyOkay/HaloBattery),
// MIT License, Copyright (c) 2026 Halo Battery contributors.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WinMonitor.Services.Halo
{
    /// <summary>One HID collection (what hidapi calls a device info entry).</summary>
    public sealed class HidInfo
    {
        public string Path = "";
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public int InterfaceNumber = -1;
        public string Serial = "";
        public string Product = "";
        public int InputLength;
        public int OutputLength;
        public int FeatureLength;

        /// <summary>Device instance from the path, without the collection number:
        /// \\?\hid#vid_054c&amp;pid_0ce6&amp;mi_03#8&amp;1234abcd&amp;0&amp;0000#{...} -> 8&amp;1234abcd&amp;0</summary>
        public string Instance
        {
            get
            {
                var parts = Path.Split('#');
                if (parts.Length < 3) return "";
                string inst = parts[2].ToLowerInvariant();
                int amp = inst.LastIndexOf('&');
                return amp > 0 ? inst.Substring(0, amp) : inst;
            }
        }

        internal string InterfacePerhaps() => InterfaceNumber < 0 ? "-" : InterfaceNumber.ToString();

        public bool IsBluetooth
        {
            get
            {
                string s = Path.ToLowerInvariant();
                return s.Contains("vid&") || s.Contains("{00001124-0000-1000-8000-00805f9b34fb}") ||
                       s.Contains("{00001812-0000-1000-8000-00805f9b34fb}");
            }
        }
    }

    /// <summary>Windows HID access with the same semantics as hidapi on Windows.</summary>
    public static class Hid
    {
        private static readonly Regex VidRe = new Regex(@"vid[_&]([0-9a-f]{4,8})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MiRe = new Regex(@"&mi_([0-9a-f]{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, HidInfo> InfoCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Paths of the present HID interfaces, without opening any device.</summary>
        public static List<string> InterfacePaths()
        {
            var result = new List<string>();
            Native.HidD_GetHidGuid(out Guid guid);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Native.CM_Get_Device_Interface_List_Size(out int len, ref guid, null, 0) != 0 || len <= 1) return result;
                var buf = new char[len];
                int cr = Native.CM_Get_Device_Interface_List(ref guid, null, buf, len, 0);
                if (cr == 0x1A) continue; // CR_BUFFER_SMALL: the list changed, ask again
                if (cr != 0) return result;
                int start = 0;
                for (int i = 0; i < buf.Length; i++)
                {
                    if (buf[i] != '\0') continue;
                    if (i > start) result.Add(new string(buf, start, i - start));
                    start = i + 1;
                }
                return result;
            }
            return result;
        }

        /// <summary>All HID collections of a vendor (hid.enumerate(vid)). Attributes are read once per path.</summary>
        public static List<HidInfo> Enumerate(ushort vid)
        {
            var list = new List<HidInfo>();
            var paths = InterfacePaths();
            lock (CacheLock)
            {
                // forget unplugged devices
                var present = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                foreach (var stale in new List<string>(InfoCache.Keys))
                    if (!present.Contains(stale)) InfoCache.Remove(stale);

                foreach (var p in paths)
                {
                    if (!PathHasVid(p, vid)) continue;
                    if (!InfoCache.TryGetValue(p, out var info))
                    {
                        info = ReadInfo(p);
                        if (info == null) continue; // not openable right now; try again next time
                        InfoCache[p] = info;
                    }
                    if (info.VendorId == vid) list.Add(info);
                }
            }
            return list;
        }

        private static bool PathHasVid(string path, ushort vid)
        {
            foreach (Match m in VidRe.Matches(path))
            {
                string hex = m.Groups[1].Value;
                if (hex.Length > 4) hex = hex.Substring(hex.Length - 4);
                if (Convert.ToUInt16(hex, 16) == vid) return true;
            }
            return false;
        }

        private static HidInfo? ReadInfo(string path)
        {
            using var h = Native.CreateFile(path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                                            Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid) return null;

            var attr = new Native.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<Native.HIDD_ATTRIBUTES>() };
            if (!Native.HidD_GetAttributes(h, ref attr)) return null;

            var info = new HidInfo { Path = path, VendorId = attr.VendorID, ProductId = attr.ProductID };

            if (Native.HidD_GetPreparsedData(h, out IntPtr pp))
            {
                IntPtr caps = Marshal.AllocHGlobal(64);
                try
                {
                    if (Native.HidP_GetCaps(pp, caps) == Native.HIDP_STATUS_SUCCESS)
                    {
                        info.Usage = (ushort)Marshal.ReadInt16(caps, 0);
                        info.UsagePage = (ushort)Marshal.ReadInt16(caps, 2);
                        info.InputLength = (ushort)Marshal.ReadInt16(caps, 4);
                        info.OutputLength = (ushort)Marshal.ReadInt16(caps, 6);
                        info.FeatureLength = (ushort)Marshal.ReadInt16(caps, 8);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(caps);
                    Native.HidD_FreePreparsedData(pp);
                }
            }

            info.Serial = ReadString(h, Native.HidD_GetSerialNumberString);
            info.Product = ReadString(h, Native.HidD_GetProductString);

            var mi = MiRe.Match(path);
            if (mi.Success) info.InterfaceNumber = Convert.ToInt32(mi.Groups[1].Value, 16);
            else info.InterfaceNumber = -1;
            return info;
        }

        private delegate bool StringGetter(SafeFileHandle h, byte[] buffer, int length);

        private static string ReadString(SafeFileHandle h, StringGetter getter)
        {
            var buf = new byte[512];
            try
            {
                if (!getter(h, buf, buf.Length)) return "";
            }
            catch { return ""; }
            string s = Encoding.Unicode.GetString(buf);
            int nul = s.IndexOf('\0');
            return (nul >= 0 ? s.Substring(0, nul) : s).Trim();
        }

        /// <summary>Opens a collection for I/O. Like hidapi, falls back to no access rights
        /// (feature reports still work then) when Windows reserves the collection.</summary>
        public static HidDevice? Open(HidInfo info)
        {
            var h = Native.CreateFile(info.Path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                                      Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                                      Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (h.IsInvalid)
            {
                h.Dispose();
                h = Native.CreateFile(info.Path, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero,
                                      Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); return null; }
            }
            return new HidDevice(h, info);
        }
    }

    /// <summary>An open HID collection. Not thread-safe; used by one poll at a time.</summary>
    public sealed class HidDevice : IDisposable
    {
        private readonly SafeFileHandle _h;
        public HidInfo Info { get; }

        // pending overlapped read, kept across calls like hidapi's non-blocking read
        private IntPtr _readBuf = IntPtr.Zero;
        private IntPtr _readOv = IntPtr.Zero;
        private IntPtr _readEvent = IntPtr.Zero;
        private bool _readPending;
        private int _readLen;

        internal HidDevice(SafeFileHandle h, HidInfo info)
        {
            _h = h;
            Info = info;
        }

        /// <summary>hid_send_feature_report: data[0] is the report id; padded to the feature length.</summary>
        public bool SendFeature(byte[] data)
        {
            int len = Math.Max(data.Length, Info.FeatureLength);
            var buf = new byte[len];
            Array.Copy(data, buf, data.Length);
            return Native.HidD_SetFeature(_h, buf, len);
        }

        /// <summary>hid_get_feature_report: the reply including the report id at [0], or null.</summary>
        public byte[]? GetFeature(byte reportId, int length)
        {
            int len = Math.Max(length, Info.FeatureLength);
            var buf = new byte[len];
            buf[0] = reportId;
            return Native.HidD_GetFeature(_h, buf, len) ? buf : null;
        }

        /// <summary>hid_write: data[0] is the report id; padded to the output length.</summary>
        public bool Write(byte[] data)
        {
            int len = Math.Max(data.Length, Info.OutputLength);
            IntPtr buf = Marshal.AllocHGlobal(len);
            IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
            IntPtr evt = Native.CreateEvent(IntPtr.Zero, true, false, null);
            try
            {
                var bytes = new byte[len];
                Array.Copy(data, bytes, data.Length);
                Marshal.Copy(bytes, 0, buf, len);
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = evt }, ov, false);

                if (!Native.WriteFile(_h, buf, len, IntPtr.Zero, ov))
                {
                    if (Marshal.GetLastWin32Error() != Native.ERROR_IO_PENDING) return false;
                    if (Native.WaitForSingleObject(evt, 1000) != 0)
                    {
                        Native.CancelIoEx(_h, ov);
                        Native.GetOverlappedResult(_h, ov, out _, true);
                        return false;
                    }
                }
                return Native.GetOverlappedResult(_h, ov, out int written, false) && written > 0;
            }
            finally
            {
                Native.CloseHandle(evt);
                Marshal.FreeHGlobal(ov);
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>hid_read_timeout. Returns null when nothing arrived in time (the read stays
        /// pending for the next call). Like hidapi, a report id of 0 is stripped from the result.</summary>
        public byte[]? Read(int timeoutMs)
        {
            if (Info.InputLength <= 0) return null;
            if (_readBuf == IntPtr.Zero)
            {
                _readLen = Info.InputLength;
                _readBuf = Marshal.AllocHGlobal(_readLen);
                _readOv = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
                _readEvent = Native.CreateEvent(IntPtr.Zero, true, false, null);
            }

            if (!_readPending)
            {
                Native.ResetEvent(_readEvent);
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _readEvent }, _readOv, false);
                if (!Native.ReadFile(_h, _readBuf, _readLen, IntPtr.Zero, _readOv))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING) return null;
                }
                _readPending = true;
            }

            if (Native.WaitForSingleObject(_readEvent, (uint)Math.Max(0, timeoutMs)) != 0) return null;

            _readPending = false;
            if (!Native.GetOverlappedResult(_h, _readOv, out int n, false) || n <= 0) return null;

            var data = new byte[n];
            Marshal.Copy(_readBuf, data, 0, n);
            if (data[0] == 0)
            {
                var stripped = new byte[n - 1];
                Array.Copy(data, 1, stripped, 0, n - 1);
                return stripped;
            }
            return data;
        }

        public void Dispose()
        {
            if (_readPending)
            {
                Native.CancelIoEx(_h, _readOv);
                Native.GetOverlappedResult(_h, _readOv, out _, true);
                _readPending = false;
            }
            if (_readEvent != IntPtr.Zero) { Native.CloseHandle(_readEvent); _readEvent = IntPtr.Zero; }
            if (_readOv != IntPtr.Zero) { Marshal.FreeHGlobal(_readOv); _readOv = IntPtr.Zero; }
            if (_readBuf != IntPtr.Zero) { Marshal.FreeHGlobal(_readBuf); _readBuf = IntPtr.Zero; }
            _h.Dispose();
        }
    }

    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int ERROR_IO_PENDING = 997;
        public const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                       uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int toRead, IntPtr read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int toWrite, IntPtr written, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("hid.dll")]
        public static extern void HidD_GetHidGuid(out Guid guid);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DllImport("hid.dll")]
        public static extern int HidP_GetCaps(IntPtr preparsed, IntPtr caps);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_Size(out int length, ref Guid guid, string? deviceId, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);
    }
}
