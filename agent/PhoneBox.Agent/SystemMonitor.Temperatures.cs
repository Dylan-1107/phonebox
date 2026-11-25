using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

public sealed partial class SystemMonitor
{
    private volatile TemperatureSnapshot _lhmTemps = new([], [], false);
    private volatile IReadOnlyList<TemperatureInfo> _windowsStorage = [];

    private async Task StorageTempLoop(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _windowsStorage = await HardwareTemperatures.ReadWindowsStorage(ct);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (failures++ == 0) log.LogDebug("storage temperature: {Msg}", ex.Message);
                _windowsStorage = [];
            }
            await Task.Delay(_windowsStorage.Count > 0 ? 15_000 : 60_000, ct);
        }
    }

    /// <summary>读取物理内存和存储设备的温度及功耗传感器，绝不使用显存或逻辑卷使用量。</summary>
    internal static TemperatureSnapshot FindHardwareTemps(JsonElement root)
    {
        var ram = new Dictionary<string, TemperatureInfo>();
        var storage = new Dictionary<string, (TemperatureInfo Reading, bool Primary)>();
        var powers = new Dictionary<string, (TemperatureInfo Reading, string Kind, int Priority)>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Children", out var rootChildren)
            || rootChildren.ValueKind != JsonValueKind.Array) return new([], [], false);
        void Visit(JsonElement node, string? kind, string? hardwareName, string? hardwareId, string path)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            var id = StrProp(node, "HardwareId");
            var image = StrProp(node, "ImageURL") ?? "";
            var name = StrProp(node, "Text");
            if (id is { Length: > 0 } || IsHardwareIcon(image))
            {
                // 嵌套硬件节点开启新的作用域，不能让父节点的内存或存储设备类型延续到 GPU。
                kind = HardwareKind(id, image);
                hardwareName = name;
                hardwareId = string.IsNullOrEmpty(id) ? "lhm/" + path : id;
            }
            var deviceId = hardwareId ?? hardwareName ?? kind ?? "lhm/" + path;
            if (kind is not null && IsLiveTemperature(node, kind)
                && ReadCelsius(node) is { } temp && double.IsFinite(temp) && temp > -50 && temp < 150)
            {
                var sensorId = StrProp(node, "SensorId");
                var reading = new TemperatureInfo(sensorId ?? deviceId + "/" + name,
                    string.IsNullOrEmpty(hardwareName) ? name ?? kind : hardwareName + (name is null or "Temperature" ? "" : " · " + name),
                    temp, "lhm");
                if (kind == "ram") ram[deviceId] = reading;
                else
                {
                    var primary = sensorId?.EndsWith("/temperature/0", StringComparison.OrdinalIgnoreCase) == true
                        || name is "Temperature" or "Composite" or "Composite Temperature";
                    var disk = reading with { Id = deviceId, Name = hardwareName ?? name ?? "Storage" };
                    if (!storage.TryGetValue(deviceId, out var previous) || (primary && !previous.Primary)
                        || (primary == previous.Primary && temp > previous.Reading.Temp))
                        storage[deviceId] = (disk, primary);
                }
            }
            if (kind is not null && ReadWatts(node) is { } watts)
            {
                // 优先使用设备提供的总功耗，否则使用主功耗传感器。绝不累加各供电线路或组件的读数。
                var priority = DevicePowerPriority(node);
                if (!powers.TryGetValue(deviceId, out var previous) || priority < previous.Priority)
                    powers[deviceId] = (new(deviceId, hardwareName ?? kind, null, "lhm", watts), kind, priority);
            }
            if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in children.EnumerateArray()) Visit(child, kind, hardwareName, hardwareId, path + "/" + index++);
            }
        }
        Visit(root, null, null, null, "0");
        foreach (var (id, power) in powers)
        {
            if (power.Kind == "ram")
                ram[id] = ram.TryGetValue(id, out var reading) ? reading with { Power = power.Reading.Power } : power.Reading;
            else if (storage.TryGetValue(id, out var disk))
                storage[id] = (disk.Reading with { Power = power.Reading.Power }, disk.Primary);
            else storage[id] = (power.Reading, false);
        }
        return new(ram.Values.ToList(), storage.Values.Select(s => s.Reading).ToList(), true);
    }

    /// <summary>仅使用实际的 CPU 封装功耗传感器，不使用 TDP、功耗上限、DRAM 功耗或核心读数之和。</summary>
    internal static double? FindCpuPower(JsonElement root)
    {
        string[] names = ["CPU Package", "Package", "CPU Package Power", "Package Power"];
        double? watts = null;
        var priority = int.MaxValue;
        void Visit(JsonElement node, bool inCpu)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            if (StrProp(node, "HardwareId") is { Length: > 0 } || IsHardwareIcon(StrProp(node, "ImageURL") ?? ""))
                inCpu = IsCpuNode(node); // 嵌套的 GPU、内存条或其他硬件会结束 CPU 作用域。
            var rank = Array.FindIndex(names, n => string.Equals(n, StrProp(node, "Text")?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (inCpu && rank >= 0 && rank < priority && ReadWatts(node) is { } value)
                (watts, priority) = (value, rank);
            if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray()) Visit(child, inCpu);
        }
        Visit(root, false);
        return watts;
    }

    private static int DevicePowerPriority(JsonElement node)
    {
        var name = StrProp(node, "Text")?.Trim();
        if (new[] { "Total", "Total Power", "Package", "Package Power", "Module Power", "Drive Power" }
            .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return 0;
        return StrProp(node, "SensorId")?.EndsWith("/power/0", StringComparison.OrdinalIgnoreCase) == true ? 1 : 2;
    }

    /// <summary>新版带类型的数值单位为瓦；旧版字符串必须明确带有 W/mW/kW 单位。</summary>
    private static double? ReadWatts(JsonElement node)
    {
        var name = StrProp(node, "Text") ?? "";
        if (new[] { "limit", "tdp", "threshold", "budget", "maximum", "minimum", "target", "rated", "design", "cap", "warning", "critical", "resolution" }
            .Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)) || StaticPowerName().IsMatch(name)) return null;
        var typed = false;
        if (node.TryGetProperty("Type", out var type) && type.ValueKind != JsonValueKind.Null)
        {
            if (type.ValueKind != JsonValueKind.String || type.GetString() != "Power") return null;
            typed = true;
        }
        if (!node.TryGetProperty("Value", out var value)) return null;
        double? watts = null;
        if (typed && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var raw)) watts = raw;
        else if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text)
        {
            if (Gpus.ParseLhmValue(text, "Power") is { Kind: "power" } parsed) watts = parsed.Value;
            else if (ScaledPowerValue().Match(text) is { Success: true } scaled
                && Gpus.ParseLhmValue(scaled.Groups[1].Value + " W", "Power") is { Kind: "power" } scaledValue)
                watts = scaledValue.Value * (scaled.Groups[2].Value == "mW" ? 0.001 : 1000);
            else if (typed && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out raw)) watts = raw;
        }
        return watts is { } w && double.IsFinite(w) && w >= 0 ? w : null;
    }

    [GeneratedRegex(@"\b(?:max|min|PL[124])\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaticPowerName();

    [GeneratedRegex(@"^\s*(-?\d+(?:[.,]\d+)?)\s*(mW|kW)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ScaledPowerValue();

    // LHM 还提供温度类型的元数据：DIMM 分辨率和限值（索引 1–5），以及磁盘警告和临界
    // 阈值（索引 10–11）。这些值虽以摄氏度为单位，但只是常量，并非实时温度。
    private static bool IsLiveTemperature(JsonElement node, string kind)
    {
        var name = StrProp(node, "Text") ?? "";
        if (new[] { "resolution", "limit", "warning", "critical", "threshold" }.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) return false;
        var id = StrProp(node, "SensorId");
        if (id is not null && id.Contains("/temperature/", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id[(id.LastIndexOf('/') + 1)..], out var index))
            return kind == "ram" ? index == 0 : index is >= 0 and <= 8;
        return true;
    }

    private static bool IsHardwareIcon(string image) =>
        new[] { "/ram.png", "/hdd.png", "/ssd.png", "/cpu.png", "/nvidia.png", "/ati.png", "/intel.png", "/mainboard.png", "/chip.png" }
            .Any(s => image.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static string? HardwareKind(string? id, string image)
    {
        if (id is not null)
        {
            if (new[] { "/ram/", "/memory/", "/dimm/", "/spd/" }.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return "ram";
            if (new[] { "/hdd/", "/ssd/", "/nvme/", "/ata/", "/scsi/", "/storage/" }.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return "storage";
            // GPU 的内存温度属于显存，而非系统内存。
            if (id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase) || IsCpuNodeId(id)) return null;
        }
        if (image.EndsWith("/ram.png", StringComparison.OrdinalIgnoreCase)) return "ram";
        if (image.EndsWith("/hdd.png", StringComparison.OrdinalIgnoreCase) || image.EndsWith("/ssd.png", StringComparison.OrdinalIgnoreCase)) return "storage";
        return null;
    }

    private static bool IsCpuNodeId(string id) => id.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase);
}
