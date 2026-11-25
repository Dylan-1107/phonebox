using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class HardwareTemperatureTests
{
    private static TemperatureSnapshot Lhm(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SystemMonitor.FindHardwareTemps(doc.RootElement);
    }

    private static IReadOnlyList<TemperatureInfo> Windows(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return HardwareTemperatures.ParseWindowsStorage(doc.RootElement);
    }

    private static double? CpuPower(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return SystemMonitor.FindCpuPower(doc.RootElement);
    }

    [Fact]
    public void RamModulesAreSeparateAndGpuMemoryDoesNotCount()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"DIMM A1","HardwareId":"/ram/0","Children":[
                {"Text":"Temperature","SensorId":"/ram/0/temperature/0","Value":"42,5 °C","Type":"Temperature"}]},
              {"Text":"DIMM B1","HardwareId":"/ram/1","Children":[
                {"Text":"Temperature","SensorId":"/ram/1/temperature/0","Value":"113 °F","Type":"Temperature"}]},
              {"Text":"NVIDIA GPU","HardwareId":"/gpu-nvidia/0","Children":[
                {"Text":"GPU Memory","Value":"92 °C","Type":"Temperature"}]}]}
            """);
        Assert.True(snapshot.LhmConnected);
        Assert.Equal(2, snapshot.Ram.Count);
        Assert.Equal("DIMM A1", snapshot.Ram[0].Name);
        Assert.Equal("/ram/0/temperature/0", snapshot.Ram[0].Id);
        Assert.Equal(42.5, snapshot.Ram[0].Temp);
        Assert.Equal(45.0, snapshot.Ram[1].Temp);
        Assert.Empty(snapshot.Storage);
    }

    [Fact]
    public void PhysicalDimmTsodUsesOnlyTheLiveReadingNotResolutionOrLimits()
    {
        var snapshot = Lhm("""
            {"Children":[{"Text":"SK Hynix DDR5","HardwareId":"/memory/dimm/1","Children":[
              {"Text":"DIMM #1","SensorId":"/memory/dimm/1/temperature/0","Type":"Temperature","Value":"44 °C"},
              {"Text":"Temperature Sensor Resolution","SensorId":"/memory/dimm/1/temperature/1","Value":"0.25 °C"},
              {"Text":"Thermal Sensor High Limit","SensorId":"/memory/dimm/1/temperature/3","Value":"85 °C"},
              {"Text":"Renamed metadata","SensorId":"/memory/dimm/1/temperature/5","Value":"95 °C"}]}]}
            """);
        Assert.Equal(44.0, Assert.Single(snapshot.Ram).Temp);
    }

    [Fact]
    public void StaticDiskThresholdsCannotBecomeLiveTemperatures()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"SATA SSD","HardwareId":"/ata/0","Children":[
                {"Text":"Temperature #1","SensorId":"/ata/0/temperature/1","Value":"40 °C"},
                {"Text":"Warning Temperature","SensorId":"/ata/0/temperature/10","Value":"70 °C"},
                {"Text":"Renamed threshold","SensorId":"/ata/0/temperature/11","Value":"90 °C"}]},
              {"Text":"SCSI SSD","HardwareId":"/scsi/1","Children":[
                {"Text":"Critical Temperature","Value":"90 °C"}]}]}
            """);
        Assert.Equal(40.0, Assert.Single(snapshot.Storage).Temp);
    }

    [Fact]
    public void StorageHasOneRowPerPhysicalDiskAndPrefersItsCompositeReading()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"Samsung SSD","HardwareId":"/nvme/0","Children":[
                {"Text":"Temperatures","Children":[
                  {"Text":"Temperature 1","Value":"67 °C"},
                  {"Text":"Temperature","Value":"39 °C"},
                  {"Text":"Temperature 2","Value":"74 °C"}]}]},
              {"Text":"Samsung SSD","HardwareId":"/nvme/1","Children":[
                {"Text":"Temperature","Value":"41 °C"}]}]}
            """);
        Assert.Equal(2, snapshot.Storage.Count);
        Assert.Equal("Samsung SSD", snapshot.Storage[0].Name);
        Assert.Equal(39.0, snapshot.Storage[0].Temp);
        Assert.Equal(41.0, snapshot.Storage[1].Temp);
        Assert.NotEqual(snapshot.Storage[0].Id, snapshot.Storage[1].Id);
    }

    [Fact]
    public void ReleasedLhmCompositeNameWinsOverHotterAuxiliarySensors()
    {
        var snapshot = Lhm("""
            {"Children":[{"Text":"NVMe","HardwareId":"/nvme/0","Children":[
              {"Text":"Composite Temperature","SensorId":"/nvme/0/temperature/0","Value":"40 °C"},
              {"Text":"Temperature #1","SensorId":"/nvme/0/temperature/1","Value":"62 °C"}]}]}
            """);
        Assert.Equal(40.0, Assert.Single(snapshot.Storage).Temp);
    }

    [Fact]
    public void LegacyIconsIdentifyRamAndDisksWithoutHardwareIds()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"DIMM 1","ImageURL":"images_icon/ram.png","Children":[{"Text":"Temperature","Value":"39 °C"}]},
              {"Text":"NVMe SSD","ImageURL":"images_icon/hdd.png","Children":[{"Text":"Temperature","Value":"46 °C"}]}]}
            """);
        Assert.Single(snapshot.Ram);
        Assert.Single(snapshot.Storage);
    }

    [Fact]
    public void LegacyDisksWithIdenticalNamesRetainSeparateIdentities()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"Same SSD","ImageURL":"images_icon/hdd.png","Children":[{"Text":"Temperature","Value":"39 °C"}]},
              {"Text":"Same SSD","ImageURL":"images_icon/hdd.png","Children":[{"Text":"Temperature","Value":"46 °C"}]}]}
            """);
        Assert.Equal(2, snapshot.Storage.Count);
        Assert.Equal(39.0, snapshot.Storage[0].Temp);
        Assert.Equal(46.0, snapshot.Storage[1].Temp);
        Assert.NotEqual(snapshot.Storage[0].Id, snapshot.Storage[1].Id);
    }

    [Fact]
    public void UnrelatedAndMalformedSensorsAreNotTemperatures()
    {
        var snapshot = Lhm("""
            {"Children":[null,4,"invalid",
              {"Text":"System RAM","HardwareId":"/ram/0","Children":[
                {"Text":"Used Memory","Value":"24.0 GB","Type":"Data"},
                {"Text":"Temperature","Value":"47 °C","Type":"Load"},
                {"Text":"Temperature","Value":"250 °C"},
                {"Text":"GPU","HardwareId":"/gpu-nvidia/0","Children":[{"Text":"Memory","Value":"83 °C"}]}]},
              {"Text":"CPU","HardwareId":"/intelcpu/0","Children":[{"Text":"CPU Package","Value":"75 °C"}]}]}
            """);
        Assert.Empty(snapshot.Ram);
        Assert.Empty(snapshot.Storage);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Children\":null}")]
    public void InvalidTreesDoNotClaimAnAvailableMonitor(string json)
    {
        var snapshot = Lhm(json);
        Assert.False(snapshot.LhmConnected);
        Assert.Empty(snapshot.Ram);
        Assert.Empty(snapshot.Storage);
    }

    [Fact]
    public void AConnectedMonitorWithoutThermalSensorsIsStillConnected()
    {
        var snapshot = Lhm("""{"Children":[{"Text":"Memory","HardwareId":"/ram","Children":[]}]}""");
        Assert.True(snapshot.LhmConnected);
        Assert.Empty(snapshot.Ram);
    }

    [Fact]
    public void WindowsKeepsEveryDiskEvenWhenTemperatureIsUnavailable()
    {
        var rows = Windows("""
            [{"id":"0","name":"Predator SSD","temp":42},
             {"id":"1","name":"Samsung SSD","temp":37},
             {"id":"2","name":"Unsupported drive","temp":null}]
            """);
        Assert.Equal(3, rows.Count);
        Assert.Equal("disk/0", rows[0].Id);
        Assert.Equal(42.0, rows[0].Temp);
        Assert.Equal(37.0, rows[1].Temp);
        Assert.Null(rows[2].Temp);
        Assert.Equal("windows-storage", rows[0].Source);
        Assert.All(rows, r => Assert.Null(r.Power));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("150")]
    [InlineData("null")]
    [InlineData("\"42\"")]
    public void WindowsUnsupportedValuesNeverBecomeZeroCelsius(string value)
    {
        var rows = Windows("[{\"id\":\"0\",\"name\":\"SSD\",\"temp\":" + value + "}]");
        Assert.Null(Assert.Single(rows).Temp);
    }

    [Fact]
    public void WindowsMalformedRowsAreSkippedWithoutLosingLaterDrives()
    {
        var rows = Windows("""[null,42,{"id":0,"name":"bad"},{"id":"1","name":"SSD","temp":44}]""");
        Assert.Equal(44.0, Assert.Single(rows).Temp);
        Assert.Empty(Windows("null"));
    }

    [Fact]
    public void WindowsReadingsWinAndLhmFillsUnambiguousGaps()
    {
        TemperatureInfo[] windows = [new("disk/0", "SSD A", 42, "windows-storage"), new("disk/1", "SSD B", null, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/1", " ssd b ", 45, "lhm"), new("/nvme/0", "SSD A", 43, "lhm")];
        var rows = HardwareTemperatures.Storage(windows, lhm);
        Assert.Equal(2, rows.Count);
        Assert.Equal(42.0, rows[0].Temp);
        Assert.Equal(45.0, rows[1].Temp);
        Assert.Equal("disk/1", rows[1].Id);
        Assert.Equal("SSD B", rows[1].Name);
        Assert.Equal("lhm", rows[1].Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalDiskModelsCannotBeJoinedByEnumerationOrder(bool reverse)
    {
        TemperatureInfo[] windows = [new("disk/0", "Same SSD", 42, "windows-storage"), new("disk/1", "Same SSD", null, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/0", "Same SSD", 43, "lhm"), new("/nvme/1", "Same SSD", 45, "lhm")];
        var rows = HardwareTemperatures.Storage(windows, reverse ? lhm.Reverse().ToArray() : lhm);
        Assert.Equal(4, rows.Count);
        Assert.Equal(windows[0], rows[0]);
        Assert.Equal(windows[1], rows[1]);
        Assert.Equal(43.0, Assert.Single(rows, r => r.Id == "/nvme/0").Temp);
        Assert.Equal(45.0, Assert.Single(rows, r => r.Id == "/nvme/1").Temp);
    }

    [Fact]
    public void AmbiguousLhmModelsCannotFillOneWindowsDrive()
    {
        TemperatureInfo[] windows = [new("disk/0", "Same SSD", null, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/0", "Same SSD", 43, "lhm"), new("/nvme/1", "Same SSD", 45, "lhm")];
        var rows = HardwareTemperatures.Storage(windows, lhm);
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].Temp);
    }

    [Fact]
    public void LhmStorageStillWorksWithoutWindowsCounters()
    {
        TemperatureInfo[] lhm = [new("/nvme/0", "SSD", 39, "lhm")];
        Assert.Equal(lhm, HardwareTemperatures.Storage([], lhm));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CpuPowerPrefersThePackageOverComponentsAndOtherPackageAliases(bool reverse)
    {
        string[] sensors = [
            """{"Text":"CPU Cores","Type":"Power","Value":120}""",
            """{"Text":"CPU DRAM","Type":"Power","Value":12}""",
            """{"Text":"Package","Type":"Power","Value":45}""",
            """{"Text":"CPU Package","Type":"Power","Value":36.5}""",
            """{"Text":"CPU Package Power Limit","Type":"Power","Value":180}"""
        ];
        var children = string.Join(",", reverse ? sensors.Reverse() : sensors);
        var json = "{\"Children\":[{\"HardwareId\":\"/intelcpu/0\",\"Children\":[" + children + "]}]}";
        Assert.Equal(36.5, CpuPower(json));
    }

    [Fact]
    public void AmdAndLegacyCpuNodesExposeOnlyActualPackageWatts()
    {
        Assert.Equal(72.5, CpuPower("""
            {"Children":[{"HardwareId":"/amdcpu/0","Children":[
              {"Text":"Package","Type":"Power","Value":72.5}]}]}
            """));
        Assert.Equal(75.0, CpuPower("""
            {"Children":[{"ImageURL":"images_icon/cpu.png","Children":[
              {"Text":"Powers","Children":[{"Text":"CPU Package","Value":"0.075 kW"}]}]}]}
            """));
    }

    [Fact]
    public void CpuPowerDoesNotUseCoresOnlyDramGpuOrUnscopedPackageSensors()
    {
        Assert.Null(CpuPower("""
            {"Children":[
              {"Text":"CPU Package","Type":"Power","Value":999},
              {"HardwareId":"/intelcpu/0","Children":[
                {"Text":"CPU Cores","Type":"Power","Value":60},
                {"Text":"CPU DRAM","Type":"Power","Value":8},
                {"Text":"CPU Package Power Limit","Type":"Power","Value":125},
                {"HardwareId":"/gpu-nvidia/0","Children":[{"Text":"CPU Package","Type":"Power","Value":300}]},
                {"ImageURL":"images_icon/ram.png","Children":[{"Text":"CPU Package","Value":"5 W"}]}]},
              {"HardwareId":"/gpu-amd/0","Children":[{"Text":"Package","Value":"250 W"}]},
              {"HardwareId":"/memory/dimm/0","Children":[{"Text":"Package","Value":"4 W"}]},
              {"HardwareId":"/nvme/0","Children":[{"Text":"Package","Value":"6 W"}]}]}
            """));
    }

    [Theory]
    [InlineData("0", "\"Power\"", 0.0)]
    [InlineData("1.75", "\"Power\"", 1.75)]
    [InlineData("\"32.5\"", "\"Power\"", 32.5)]
    [InlineData("\"32,5 W\"", "null", 32.5)]
    [InlineData("\"32500 mW\"", "\"Power\"", 32.5)]
    [InlineData("\"0.0325 kW\"", "null", 32.5)]
    [InlineData("\"0 W\"", "null", 0.0)]
    public void ActualPowerValuesAreNormalizedToWattsIncludingTrueZero(string value, string type, double expected)
    {
        var json = "{\"Children\":[{\"HardwareId\":\"/intelcpu/0\",\"Children\":[{\"Text\":\"CPU Package\",\"Value\":"
            + value + ",\"Type\":" + type + "}]}]}";
        Assert.Equal(expected, CpuPower(json));
    }

    [Theory]
    [InlineData("null", "\"Power\"")]
    [InlineData("true", "\"Power\"")]
    [InlineData("{}", "\"Power\"")]
    [InlineData("-1", "\"Power\"")]
    [InlineData("1e400", "\"Power\"")]
    [InlineData("\"NaN\"", "\"Power\"")]
    [InlineData("\"Infinity\"", "\"Power\"")]
    [InlineData("\"-2 W\"", "\"Power\"")]
    [InlineData("\"N/A\"", "\"Power\"")]
    [InlineData("\"45 °C\"", "\"Power\"")]
    [InlineData("\"40 %\"", "\"Power\"")]
    [InlineData("\"40 Wh\"", "\"Power\"")]
    [InlineData("40", "null")]
    [InlineData("\"40\"", "null")]
    [InlineData("\"40 W\"", "\"Temperature\"")]
    [InlineData("\"40 W\"", "\"Load\"")]
    [InlineData("40", "\"Energy\"")]
    [InlineData("40", "2")]
    public void MissingInvalidOrNonPowerValuesCannotBecomeWatts(string value, string type)
    {
        var json = "{\"Children\":[{\"HardwareId\":\"/intelcpu/0\",\"Children\":[{\"Text\":\"CPU Package\",\"Value\":"
            + value + ",\"Type\":" + type + "}]}]}";
        Assert.Null(CpuPower(json));
    }

    [Fact]
    public void AResponseWithoutPackagePowerDoesNotReuseAnEarlierReading()
    {
        Assert.Equal(48.0, CpuPower("""
            {"Children":[{"HardwareId":"/intelcpu/0","Children":[{"Text":"CPU Package","Value":"48 W"}]}]}
            """));
        Assert.Null(CpuPower("""
            {"Children":[{"HardwareId":"/intelcpu/0","Children":[{"Text":"CPU Package","Type":"Power"}]}]}
            """));
    }

    [Fact]
    public void RamPowerPairsByPhysicalHardwareNotIdenticalModuleNames()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"Same DIMM","HardwareId":"/memory/dimm/0","Children":[
                {"Text":"DIMM #0","SensorId":"/memory/dimm/0/temperature/0","Value":"42 °C"},
                {"Text":"Powers","Children":[{"Text":"Module Power","Type":"Power","Value":3.5}]}]},
              {"Text":"Same DIMM","HardwareId":"/memory/dimm/1","Children":[
                {"Text":"Powers","Children":[{"Text":"Module Power","Value":"4500 mW"}]},
                {"Text":"DIMM #1","SensorId":"/memory/dimm/1/temperature/0","Value":"44 °C"}]}]}
            """);
        Assert.Equal(2, snapshot.Ram.Count);
        var first = Assert.Single(snapshot.Ram, r => r.Id == "/memory/dimm/0/temperature/0");
        var second = Assert.Single(snapshot.Ram, r => r.Id == "/memory/dimm/1/temperature/0");
        Assert.Equal(42.0, first.Temp);
        Assert.Equal(3.5, first.Power);
        Assert.Equal(44.0, second.Temp);
        Assert.Equal(4.5, second.Power);
    }

    [Fact]
    public void StoragePowerPairsWithItsOwnCompositeTemperatureWithoutAddingSensors()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"Same SSD","HardwareId":"/nvme/0","Children":[
                {"Text":"Controller Power","SensorId":"/nvme/0/power/1","Type":"Power","Value":6},
                {"Text":"Power","SensorId":"/nvme/0/power/0","Value":"4 W"},
                {"Text":"Composite Temperature","SensorId":"/nvme/0/temperature/0","Value":"39 °C"},
                {"Text":"Temperature #1","SensorId":"/nvme/0/temperature/1","Value":"61 °C"}]},
              {"Text":"Same SSD","HardwareId":"/nvme/1","Children":[
                {"Text":"Temperature","SensorId":"/nvme/1/temperature/0","Value":"45 °C"},
                {"Text":"Total Power","Type":"Power","Value":7}]}]}
            """);
        Assert.Equal(2, snapshot.Storage.Count);
        var first = Assert.Single(snapshot.Storage, r => r.Id == "/nvme/0");
        var second = Assert.Single(snapshot.Storage, r => r.Id == "/nvme/1");
        Assert.Equal(39.0, first.Temp);
        Assert.Equal(4.0, first.Power);
        Assert.Equal(45.0, second.Temp);
        Assert.Equal(7.0, second.Power);
    }

    [Fact]
    public void PhysicalDevicesCanExposeOnlyPowerAndZeroIsNotMissing()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"DIMM","HardwareId":"/memory/dimm/0","Children":[{"Text":"Power","Type":"Power","Value":2}]},
              {"Text":"SSD","HardwareId":"/nvme/0","Children":[{"Text":"Power","Value":"0 W"}]}]}
            """);
        Assert.Null(Assert.Single(snapshot.Ram).Temp);
        Assert.Equal(2.0, snapshot.Ram[0].Power);
        Assert.Null(Assert.Single(snapshot.Storage).Temp);
        Assert.Equal(0.0, snapshot.Storage[0].Power);
    }

    [Theory]
    [InlineData("Power Limit")]
    [InlineData("TDP")]
    [InlineData("Rated Power")]
    [InlineData("Power Budget")]
    [InlineData("Power Threshold")]
    [InlineData("Maximum Power")]
    [InlineData("Power Max")]
    [InlineData("Power PL1")]
    public void StaticDevicePowerMetadataNeverBecomesALiveReading(string sensorName)
    {
        var node = "{\"Text\":\"" + sensorName + "\",\"Type\":\"Power\",\"Value\":90}";
        var snapshot = Lhm("{\"Children\":[{\"HardwareId\":\"/memory/dimm/0\",\"Children\":[" + node
            + "]},{\"HardwareId\":\"/nvme/0\",\"Children\":[" + node + "]}]}");
        Assert.Empty(snapshot.Ram);
        Assert.Empty(snapshot.Storage);
    }

    [Fact]
    public void CpuDramAndNestedGpuPowerDoNotBecomePhysicalRamOrDiskPower()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"HardwareId":"/intelcpu/0","Children":[{"Text":"CPU DRAM","Type":"Power","Value":20}]},
              {"HardwareId":"/gpu-nvidia/0","Children":[{"Text":"GPU Memory","Type":"Power","Value":25}]},
              {"Text":"DIMM","HardwareId":"/memory/dimm/0","Children":[
                {"Text":"Temperature","SensorId":"/memory/dimm/0/temperature/0","Value":"40 °C"},
                {"HardwareId":"/gpu-amd/0","Children":[{"Text":"Power","Value":"200 W"}]}]},
              {"Text":"SSD","HardwareId":"/nvme/0","Children":[
                {"Text":"Temperature","Value":"42 °C"},
                {"ImageURL":"images_icon/nvidia.png","Children":[{"Text":"Power","Value":"250 W"}]}]}]}
            """);
        Assert.Null(Assert.Single(snapshot.Ram).Power);
        Assert.Null(Assert.Single(snapshot.Storage).Power);
    }

    [Fact]
    public void LegacySameModelDevicesKeepTheirOwnPowerAndTemperature()
    {
        var snapshot = Lhm("""
            {"Children":[
              {"Text":"Same SSD","ImageURL":"images_icon/hdd.png","Children":[
                {"Text":"Temperature","Value":"39 °C"},{"Text":"Power","Value":"3 W"}]},
              {"Text":"Same SSD","ImageURL":"images_icon/hdd.png","Children":[
                {"Text":"Power","Value":"5 W"},{"Text":"Temperature","Value":"46 °C"}]},
              {"Text":"Same DIMM","ImageURL":"images_icon/ram.png","Children":[{"Text":"Power","Value":"2 W"}]},
              {"Text":"Same DIMM","ImageURL":"images_icon/ram.png","Children":[{"Text":"Power","Value":"4 W"}]}]}
            """);
        Assert.Equal(2, snapshot.Storage.Count);
        Assert.Equal(39.0, snapshot.Storage[0].Temp);
        Assert.Equal(3.0, snapshot.Storage[0].Power);
        Assert.Equal(46.0, snapshot.Storage[1].Temp);
        Assert.Equal(5.0, snapshot.Storage[1].Power);
        Assert.NotEqual(snapshot.Storage[0].Id, snapshot.Storage[1].Id);
        Assert.Equal(2, snapshot.Ram.Count);
        Assert.Equal(2.0, snapshot.Ram[0].Power);
        Assert.Equal(4.0, snapshot.Ram[1].Power);
        Assert.NotEqual(snapshot.Ram[0].Id, snapshot.Ram[1].Id);
    }

    [Fact]
    public void UniqueNativeDiskTemperatureKeepsLhmWattsAndBothSources()
    {
        TemperatureInfo[] windows = [new("disk/0", "SSD", 42, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/0", " ssd ", 45, "lhm", 5.5)];
        var reading = Assert.Single(HardwareTemperatures.Storage(windows, lhm));
        Assert.Equal("disk/0", reading.Id);
        Assert.Equal("SSD", reading.Name);
        Assert.Equal(42.0, reading.Temp);
        Assert.Equal(5.5, reading.Power);
        Assert.Equal("windows-storage+lhm", reading.Source);
    }

    [Fact]
    public void UniquePowerOnlyLhmDiskCanAugmentTheNativeTemperature()
    {
        TemperatureInfo[] windows = [new("disk/0", "SSD", 42, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/0", "SSD", null, "lhm", 0)];
        var reading = Assert.Single(HardwareTemperatures.Storage(windows, lhm));
        Assert.Equal(42.0, reading.Temp);
        Assert.Equal(0.0, reading.Power);
        Assert.Equal("windows-storage+lhm", reading.Source);
    }

    [Fact]
    public void LhmOnlyDiskMetricsKeepTheLhmSourceWhenNativeSensorsAreMissing()
    {
        TemperatureInfo[] windows = [new("disk/0", "SSD", null, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/0", "SSD", 44, "lhm", 4)];
        var reading = Assert.Single(HardwareTemperatures.Storage(windows, lhm));
        Assert.Equal("disk/0", reading.Id);
        Assert.Equal(44.0, reading.Temp);
        Assert.Equal(4.0, reading.Power);
        Assert.Equal("lhm", reading.Source);
    }

    [Fact]
    public void AmbiguousDiskModelsCannotAttachWattsToANativeDrive()
    {
        TemperatureInfo[] windows = [new("disk/0", "Same SSD", 42, "windows-storage"), new("disk/1", "Same SSD", null, "windows-storage")];
        TemperatureInfo[] lhm = [new("/nvme/0", "Same SSD", 44, "lhm", 5)];
        var rows = HardwareTemperatures.Storage(windows, lhm);
        Assert.Equal(3, rows.Count);
        Assert.Equal(windows[0], rows[0]);
        Assert.Equal(windows[1], rows[1]);
        Assert.Equal(5.0, Assert.Single(rows, r => r.Id == "/nvme/0").Power);
    }

    [Fact]
    public void PowerFieldsAreOptionalAndSerializeInTheExistingPcContract()
    {
        var reading = new TemperatureInfo("/memory/dimm/0", "DIMM", 42, "lhm");
        var pc = new PcSnapshot(null, null, "CPU", 0, 0, null, 0, 0, [], 0);
        Assert.Null(reading.Power);
        Assert.Null(pc.CpuPower);
        var wire = JsonSerializer.SerializeToElement(pc with
        {
            CpuPower = 0,
            Temperatures = new([reading with { Power = 3.5 }], [], true),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(0.0, wire.GetProperty("cpuPower").GetDouble());
        var module = wire.GetProperty("temperatures").GetProperty("ram")[0];
        Assert.Equal(42.0, module.GetProperty("temp").GetDouble());
        Assert.Equal(3.5, module.GetProperty("power").GetDouble());
    }
}
