using Microsoft.Win32.SafeHandles;

namespace PhoneBox.Agent;

public sealed partial class BatteryMonitor
{
    // Rapoo VT3PRO on its 2.4 GHz receiver (24AE:1215) and on its USB cable (24AE:4415).
    // Reverse engineered on this machine (see DatesYuen/xiaotang-daozai#1): write a 32-byte
    // report to the FF00/000E command collection, then read the 7-byte reply
    // `BB B0 51 40 06 01 <percent>` from the FF00/0002 input collection.
    internal const int RapooVendorId = 0x24AE;
    internal const int RapooReceiverPid = 0x1215;
    internal const int RapooWiredPid = 0x4415;
    internal const ushort RapooUsagePage = 0xFF00;
    internal const ushort RapooCommandUsage = 0x000E;
    internal const ushort RapooReplyUsage = 0x0002;
    internal const int RapooCommandReportLength = 32; // 1-byte report id 0xBA + 31 zero bytes
    internal const int RapooReplyReportLength = 7;
    internal const string RapooName = "Rapoo VT3PRO";

    internal static bool IsRapooCommandCollection(ushort usagePage, ushort usage, int outputReportLength)
        => usagePage == RapooUsagePage && usage == RapooCommandUsage && outputReportLength == RapooCommandReportLength;

    internal static bool IsRapooReplyCollection(ushort usagePage, ushort usage, int inputReportLength)
        => usagePage == RapooUsagePage && usage == RapooReplyUsage && inputReportLength == RapooReplyReportLength;

    internal static byte[] BuildRapooRequest()
    {
        var buf = new byte[RapooCommandReportLength];
        buf[0] = 0xBA;
        return buf;
    }

    /// <summary>Percent from a 7-byte reply, or null when the frame is not a battery answer.</summary>
    internal static int? ParseRapooBattery(ReadOnlySpan<byte> report)
    {
        // The receiver pushes other frames on this pipe too; BB starts the battery answer.
        if (report.Length < RapooReplyReportLength || report[0] != 0xBB || report[6] > 100) return null;
        return report[6];
    }

#if WINDOWS
    private sealed class RapooGroup
    {
        public int Pid;
        public string? CommandPath;
        public string? ReplyPath;
    }

    private static (List<HidSample> Samples, string? Error) WindowsRapoo(long deadline)
    {
        var samples = new List<HidSample>();
        string? lastError = null;
        try
        {
            var groups = new Dictionary<string, RapooGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var iface in EnumerateHidInterfaces(RapooVendorId, deadline))
            {
                if (Environment.TickCount64 >= deadline) break;
                using var info = CreateFileW(iface.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (info.IsInvalid) continue;
                if (!TryGetHidAttributes(info, out var vid, out var pid) || vid != RapooVendorId) continue;
                if (pid is not (RapooReceiverPid or RapooWiredPid)) continue;
                if (!TryGetHidCaps(info, out var caps)) continue;

                if (!groups.TryGetValue(iface.PhysicalId, out var group))
                    groups[iface.PhysicalId] = group = new RapooGroup { Pid = pid };
                if (IsRapooCommandCollection(caps.UsagePage, caps.Usage, caps.OutputReportByteLength)) group.CommandPath = iface.Path;
                else if (IsRapooReplyCollection(caps.UsagePage, caps.Usage, caps.InputReportByteLength)) group.ReplyPath = iface.Path;
            }

            foreach (var group in groups.Values)
            {
                int? level = null;
                if (group.CommandPath is not null && group.ReplyPath is not null)
                {
                    try { level = ReadRapooDevice(group.CommandPath, group.ReplyPath, deadline); }
                    catch (Exception ex) { lastError ??= ex.Message; }
                }
                // One row per receiver/body: both are the same mouse, so the fixed name and kind
                // make the wired and wireless readings collapse into one entry.
                samples.Add(new HidSample(RapooName, "mouse", level, null));
            }
        }
        catch (Exception ex) { lastError ??= ex.Message; }
        return (samples, lastError);
    }

    private static int? ReadRapooDevice(string commandPath, string replyPath, long deadline)
    {
        // The reply is delivered only to the FF00/0002 input pipe, and the receiver drops it
        // unless that handle is already open when the query is written.
        using var reply = CreateFileW(replyPath, 0x80000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (reply.IsInvalid) return null;
        using var command = CreateFileW(commandPath, 0x40000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero);
        if (command.IsInvalid) return null;

        // Drop replies left over from an earlier round so they cannot be mistaken for this one.
        for (int i = 0; i < 8 && ReadHidFrame(reply, RapooReplyReportLength, 10) is not null; i++) { }

        var request = BuildRapooRequest();
        // About one query in forty goes unanswered, and a sleeping mouse never answers at all.
        for (int attempt = 0; attempt < 3 && Environment.TickCount64 < deadline; attempt++)
        {
            if (!WriteHidReport(command, request, 200)) continue;
            var until = Math.Min(deadline, Environment.TickCount64 + 700);
            while (Environment.TickCount64 < until)
            {
                var frame = ReadHidFrame(reply, RapooReplyReportLength, 80);
                if (frame is null) break;
                if (frame.Length > 0 && frame[0] == 0xBB)
                {
                    var level = ParseRapooBattery(frame);
                    if (level is not null) return level;
                }
            }
        }
        return null;
    }
#endif
}