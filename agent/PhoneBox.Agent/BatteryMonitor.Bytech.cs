namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    // AULA F87S 8K on its 2.4 GHz receiver (0C45:FEF9), bytech firmware. Reverse engineered on
    // this machine (see DatesYuen/xiaotang-daozai#1): one FF60/0061 collection both takes the
    // 65-byte query and returns `00 55 10 38 ...`, with the percent at byte 26 and the charging
    // flag at byte 27. The first zero byte is how Windows writes a report without a report id.
    internal const int BytechVendorId = 0x0C45;
    internal const int BytechPid = 0xFEF9;
    internal const ushort BytechUsagePage = 0xFF60;
    internal const ushort BytechUsage = 0x0061;
    internal const int BytechReportLength = 65;
    internal const string BytechName = "AULA F87S 8K";

    internal static bool IsBytechCollection(ushort usagePage, ushort usage, int outputReportLength)
        => usagePage == BytechUsagePage && usage == BytechUsage && outputReportLength == BytechReportLength;

    internal static byte[] BuildBytechRequest()
    {
        var buf = new byte[BytechReportLength];
        buf[1] = 0xAA;
        buf[2] = 0x10;
        buf[3] = 0x38;
        buf[7] = 0x01;
        return buf;
    }

    /// <summary>Percent and charging state from a 65-byte reply, or null for any other frame.</summary>
    internal static (int Level, bool Charging)? ParseBytechBattery(ReadOnlySpan<byte> report)
    {
        if (report.Length <= 27) return null;
        if (report[0] != 0x00 || report[1] != 0x55 || report[2] != 0x10 || report[3] != 0x38) return null;
        if (report[26] > 100) return null;
        return (report[26], report[27] > 0);
    }

#if WINDOWS
    /// <summary>
    /// A sleeping keyboard does not consume output reports: the write stays pending until it is
    /// cancelled, and the receiver answers nothing. Back off for half a minute instead of poking it
    /// every scan; the last good reading stays on screen through the cooldown.
    /// </summary>
    private const long BytechQuietMs = 30_000;
    private static long _bytechRetryAfter;

    private static (List<HidSample> Samples, string? Error) WindowsBytech(long deadline)
    {
        var samples = new List<HidSample>();
        string? lastError = null;
        try
        {
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var iface in EnumerateHidInterfaces(BytechVendorId, deadline))
            {
                if (Environment.TickCount64 >= deadline) break;
                using var info = CreateFileW(iface.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (info.IsInvalid) continue;
                if (!TryGetHidAttributes(info, out var vid, out var pid) || vid != BytechVendorId || pid != BytechPid) continue;
                if (!TryGetHidCaps(info, out var caps)) continue;
                if (!IsBytechCollection(caps.UsagePage, caps.Usage, caps.OutputReportByteLength)) continue;
                paths[iface.PhysicalId] = iface.Path;
            }

            foreach (var path in paths.Values)
            {
                (int Level, bool Charging)? reading = null;
                if (Environment.TickCount64 >= Volatile.Read(ref _bytechRetryAfter))
                {
                    try
                    {
                        reading = ReadBytechDevice(path, deadline);
                        Volatile.Write(ref _bytechRetryAfter, reading is null ? Environment.TickCount64 + BytechQuietMs : 0);
                    }
                    catch (Exception ex)
                    {
                        lastError ??= ex.Message;
                        Volatile.Write(ref _bytechRetryAfter, Environment.TickCount64 + BytechQuietMs);
                    }
                }
                samples.Add(new HidSample(BytechName, "keyboard", reading?.Level, reading?.Charging));
            }
        }
        catch (Exception ex) { lastError ??= ex.Message; }
        return (samples, lastError);
    }

    private static (int Level, bool Charging)? ReadBytechDevice(string path, long deadline)
    {
        // GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_FLAG_OVERLAPPED.
        // If the vendor's Aula Hub owns this collection exclusively the open fails; the row then
        // keeps its last good reading instead of showing a made-up number.
        using var handle = CreateFileW(path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        for (int i = 0; i < 16 && ReadHidFrame(handle, BytechReportLength, 10) is not null; i++) { }

        var request = BuildBytechRequest();
        for (int attempt = 0; attempt < 2 && Environment.TickCount64 < deadline; attempt++)
        {
            // WriteFile on this vendor collection stays pending until it times out, while the HID
            // output-report API returns at once; the firmware answers either way only when the
            // keyboard is awake and bound to the receiver.
            if (!HidD_SetOutputReport(handle, request, (uint)request.Length)) continue;
            var until = Math.Min(deadline, Environment.TickCount64 + 700);
            while (Environment.TickCount64 < until)
            {
                var frame = ReadHidFrame(handle, BytechReportLength, 80);
                if (frame is null) break;
                if (frame.Length > 3 && frame[0] == 0x00 && frame[1] == 0x55 && frame[2] == 0x10 && frame[3] == 0x38)
                {
                    var parsed = ParseBytechBattery(frame);
                    if (parsed is not null) return parsed;
                }
            }
        }
        return null;
    }
#endif
}