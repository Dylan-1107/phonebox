using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>表示物理设备，而非逻辑卷。Temp 的单位为摄氏度，Power 的单位为瓦；缺少对应传感器时为 null。</summary>
public sealed record TemperatureInfo(string Id, string Name, double? Temp, string Source, double? Power = null);

public sealed record TemperatureSnapshot(IReadOnlyList<TemperatureInfo> Ram, IReadOnlyList<TemperatureInfo> Storage,
    bool LhmConnected);

internal static class HardwareTemperatures
{
    // 可靠性计数器关联到各个物理磁盘；在某些 Windows 版本中，单独枚举计数器类
    // 不会返回实例。传感器读取失败或不受支持时保持 null，绝不当作零摄氏度。
    internal const string StorageScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        $rows = @(Get-CimInstance -Namespace root/Microsoft/Windows/Storage -ClassName MSFT_PhysicalDisk | ForEach-Object {
            $disk = $_
            $temp = $null
            try {
                $counter = Get-CimAssociatedInstance -InputObject $disk -ResultClassName MSFT_StorageReliabilityCounter | Select-Object -First 1
                if ($null -ne $counter.Temperature -and $counter.Temperature -gt 0 -and $counter.Temperature -lt 150) { $temp = [double]$counter.Temperature }
            } catch {}
            [pscustomobject]@{ id = [string]$disk.DeviceId; name = [string]$disk.FriendlyName; temp = $temp }
        })
        ConvertTo-Json -InputObject $rows -Compress
        """;

    public static async Task<IReadOnlyList<TemperatureInfo>> ReadWindowsStorage(CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"));
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(StorageScript)) })
            start.ArgumentList.Add(arg);
        start.StandardOutputEncoding = Encoding.UTF8;
        var result = await ToolProcess.RunAsync(start, TimeSpan.FromSeconds(10), ct);
        if (result.ExitCode != 0) throw new IOException("Storage temperature query failed");
        using var doc = JsonDocument.Parse(result.Output);
        return ParseWindowsStorage(doc.RootElement);
    }

    internal static IReadOnlyList<TemperatureInfo> ParseWindowsStorage(JsonElement root)
    {
        var rows = new List<TemperatureInfo>();
        if (root.ValueKind != JsonValueKind.Array) return rows;
        foreach (var node in root.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object || Text(node, "id") is not { Length: > 0 } id
                || Text(node, "name") is not { Length: > 0 } name) continue;
            double? temp = node.TryGetProperty("temp", out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out var t) && double.IsFinite(t) && t > 0 && t < 150 ? t : null;
            rows.Add(new TemperatureInfo("disk/" + id, name.Trim(), temp, "windows-storage"));
        }
        return rows;
    }

    /// <summary>操作系统会列出每块物理磁盘的名称，包括不受支持的磁盘。仅当两个来源的名称都唯一对应同一块磁盘时才合并。</summary>
    internal static IReadOnlyList<TemperatureInfo> Storage(IReadOnlyList<TemperatureInfo> windows, IReadOnlyList<TemperatureInfo> lhm)
    {
        var rows = windows.ToList();
        var osNames = windows.Select((row, index) => (row, index))
            .GroupBy(x => x.row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => x.index).ToArray(), StringComparer.OrdinalIgnoreCase);
        var lhmNames = lhm.GroupBy(row => row.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var reading in lhm)
        {
            var name = reading.Name.Trim();
            // 相同型号的设备需要共同的物理标识，不能根据枚举顺序猜测对应关系。
            if (!osNames.TryGetValue(name, out var matches) || matches.Length != 1 || lhmNames[name] != 1)
                rows.Add(reading);
            else
            {
                var native = rows[matches[0]];
                var usesLhm = (native.Temp is null && reading.Temp is not null)
                    || (native.Power is null && reading.Power is not null);
                if (usesLhm)
                    rows[matches[0]] = native with
                    {
                        Temp = native.Temp ?? reading.Temp,
                        Power = native.Power ?? reading.Power,
                        Source = native.Temp is not null || native.Power is not null
                            ? native.Source + "+" + reading.Source : reading.Source,
                    };
            }
        }
        return rows;
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
