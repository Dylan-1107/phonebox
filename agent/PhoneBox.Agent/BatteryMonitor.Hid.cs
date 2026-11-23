using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    /// <summary>
    /// One device a vendor HID scan saw this round. <paramref name="Level"/> is null when the
    /// receiver is present but did not answer (the peripheral is asleep or out of range).
    /// </summary>
    internal sealed record HidSample(string Name, string Kind, int? Level, bool? Charging)
    {
        internal string Key => Kind + "|" + Name;
    }

    /// <summary>Budget for one round of vendor HID scans (Razer, Rapoo, bytech).</summary>
    internal const long VendorHidBudgetMs = 6_000;

    /// <summary>
    /// Resolves one round of vendor samples. A device that answered gets its fresh reading and
    /// refreshes the cache; a receiver that is present but silent (the peripheral is asleep) keeps
    /// its last good reading, because the battery barely moves while it sleeps and a vanishing row
    /// is worse than a value from an hour ago. A receiver that is gone drops its row and its cache
    /// entry immediately, so unplugging is honest.
    /// </summary>
    internal static List<DeviceBattery> ResolveHidSamples(
        IEnumerable<HidSample> present,
        IDictionary<string, (DeviceBattery Device, long At)> cache,
        long now)
    {
        var devices = new Dictionary<string, DeviceBattery>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in present)
        {
            seen.Add(sample.Key);
            DeviceBattery device;
            if (sample.Level is { } level)
            {
                device = new DeviceBattery(sample.Name, Math.Clamp(level, 0, 100), sample.Kind, sample.Charging);
                cache[sample.Key] = (device, now);
            }
            else if (cache.TryGetValue(sample.Key, out var last))
            {
                device = last.Device;
            }
            else continue;

            // The wired body and the 2.4 GHz receiver of one mouse report the same battery.
            if (!devices.TryGetValue(sample.Key, out var existing) || PreferDevice(device, existing))
                devices[sample.Key] = device;
        }

        foreach (var key in cache.Keys.Where(k => !seen.Contains(k)).ToList())
            cache.Remove(key);
        return devices.Values.ToList();
    }

#if WINDOWS
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    internal readonly record struct HidInterface(string Path, string PhysicalId);

    /// <summary>Enumerates the present HID device interfaces of one vendor, newest enumeration each scan.</summary>
    internal static IEnumerable<HidInterface> EnumerateHidInterfaces(ushort vendorId, long deadline)
    {
        var guid = HidInterfaceGuid;
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;

        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; Environment.TickCount64 < deadline && SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out var req, IntPtr.Zero);
                if (req == 0) continue;

                string? path = null;
                uint devInst = 0;
                var mem = Marshal.AllocHGlobal((int)req);
                try
                {
                    Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref ifData, mem, req, out _, ref devData))
                    {
                        path = Marshal.PtrToStringUni(IntPtr.Add(mem, 4));
                        devInst = devData.DevInst;
                    }
                }
                finally { Marshal.FreeHGlobal(mem); }

                if (string.IsNullOrEmpty(path) || !path.Contains($"vid_{vendorId:x4}", StringComparison.OrdinalIgnoreCase)) continue;
                yield return new HidInterface(path, GetPhysicalDeviceId(path, devInst));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static bool TryGetHidAttributes(SafeFileHandle handle, out ushort vendorId, out ushort productId)
    {
        var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        var ok = HidD_GetAttributes(handle, ref attrs);
        vendorId = attrs.VendorID;
        productId = attrs.ProductID;
        return ok;
    }

    private static string GetPhysicalDeviceId(string path, uint devInst)
    {
        // Walk up from HID\... to the parent USB device node so multiple interfaces on one receiver share one ID,
        // while two identical receivers on different USB ports stay distinct.
        uint current = devInst;
        for (int hop = 0; hop < 3; hop++)
        {
            if (CM_Get_Parent(out var parent, current, 0) != 0) break;
            current = parent;
            var buf = new char[256];
            if (CM_Get_Device_IDW(current, buf, buf.Length, 0) == 0)
            {
                var id = new string(buf).TrimEnd('\0');
                if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
                    && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
        return devInst != 0 ? $"devinst:{devInst}" : path;
    }

    private static bool TryGetHidCaps(SafeFileHandle handle, out HIDP_CAPS caps)
    {
        caps = default;
        if (!HidD_GetPreparsedData(handle, out var pp)) return false;
        try { return HidP_GetCaps(pp, out caps) >= 0; }
        finally { HidD_FreePreparsedData(pp); }
    }

    private static bool WriteHidReport(SafeFileHandle handle, byte[] data, int timeoutMs)
        => HidOverlappedIo(isWrite: true, handle, data, timeoutMs) == data.Length;

    private static (byte[]? Data, bool FatalError) ReadHidReport(SafeFileHandle handle, int len, int timeoutMs)
    {
        var buf = new byte[len];
        var read = HidOverlappedIo(isWrite: false, handle, buf, timeoutMs);
        if (read == -2) return (null, true);
        if (read <= 0) return (null, false);
        if (read < len) Array.Resize(ref buf, read);
        return (buf, false);
    }

    /// <summary>Reads one input report, or null when the device stayed silent for the timeout.</summary>
    private static byte[]? ReadHidFrame(SafeFileHandle handle, int len, int timeoutMs)
    {
        var (data, fatal) = ReadHidReport(handle, len, timeoutMs);
        return fatal ? null : data;
    }

    private static int HidOverlappedIo(bool isWrite, SafeFileHandle handle, byte[] buf, int timeoutMs)
    {
        var ev = CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (ev == IntPtr.Zero) return -2;
        var pinBuf = GCHandle.Alloc(buf, GCHandleType.Pinned);
        var ovMem = Marshal.AllocHGlobal(Marshal.SizeOf<OVERLAPPED>());
        var ioPending = false;
        try
        {
            Marshal.StructureToPtr(new OVERLAPPED { hEvent = ev }, ovMem, false);
            var ok = isWrite
                ? WriteFile(handle, pinBuf.AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, ovMem)
                : ReadFile(handle, pinBuf.AddrOfPinnedObject(), (uint)buf.Length, IntPtr.Zero, ovMem);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != 997) return -2; // immediate error (e.g. unplugged)
                ioPending = true;
                if (WaitForSingleObject(ev, (uint)timeoutMs) == 0)
                {
                    ioPending = false;
                }
                else
                {
                    CancelIoEx(handle, ovMem);
                    if (WaitForSingleObject(ev, 200) != 0)
                        return -2; // driver did not complete cancellation in time; keep buffer pinned in finally
                    ioPending = false;
                    // If the operation completed just before CancelIoEx took effect, preserve the completion.
                    if (GetOverlappedResult(handle, ovMem, out var lateTransferred, false))
                        return (int)lateTransferred;
                    return -1; // timeout
                }
            }
            if (!GetOverlappedResult(handle, ovMem, out var transferred, false)) return -2;
            return (int)transferred;
        }
        finally
        {
            if (!ioPending)
            {
                Marshal.FreeHGlobal(ovMem);
                pinBuf.Free();
                CloseHandle(ev);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage; public ushort UsagePage;
        public ushort InputReportByteLength; public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps; public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices; public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps; public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED { public IntPtr Internal; public IntPtr InternalHigh; public int Offset; public int OffsetHigh; public IntPtr hEvent; }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr data, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA ifData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint size, out uint required, IntPtr data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint size, out uint required, ref SP_DEVINFO_DATA data);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint devInst, char[] buffer, int bufferLen, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buf, uint len, IntPtr bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buf, uint len, IntPtr bytesWritten, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr sec, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attrs);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll", SetLastError = true)] private static extern int HidP_GetCaps(IntPtr pp, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] buf, uint len);
#endif
}