using PhoneBox.Agent;
using Xunit;

public sealed class BatteryHidProtocolTests
{
    [Fact]
    public void RapooReplyPercentSitsInByteSix()
    {
        // Captured reply: BB B0 51 40 06 01 2C -> 44%.
        var frame = new byte[] { 0xBB, 0xB0, 0x51, 0x40, 0x06, 0x01, 44 };
        Assert.Equal(44, BatteryMonitor.ParseRapooBattery(frame));

        frame[6] = 100;
        Assert.Equal(100, BatteryMonitor.ParseRapooBattery(frame));

        Assert.Null(BatteryMonitor.ParseRapooBattery(new byte[] { 0xBB, 0, 0, 0, 0, 0, 101 }));
        Assert.Null(BatteryMonitor.ParseRapooBattery(new byte[] { 0xBB, 0, 0, 0, 0, 0 }));
        Assert.Null(BatteryMonitor.ParseRapooBattery(new byte[] { 0xBA, 0, 0, 0, 0, 0, 44 }));
    }

    [Fact]
    public void RapooRequestIsTheCapturedThirtyTwoByteFrame()
    {
        var request = BatteryMonitor.BuildRapooRequest();
        Assert.Equal(32, request.Length);
        Assert.Equal(0xBA, request[0]);
        Assert.All(request[1..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void BytechReplyReportsPercentAndCharging()
    {
        var frame = new byte[65];
        frame[1] = 0x55;
        frame[2] = 0x10;
        frame[3] = 0x38;
        frame[26] = 100;

        Assert.Equal((100, false), BatteryMonitor.ParseBytechBattery(frame));

        frame[26] = 44;
        frame[27] = 1;
        Assert.Equal((44, true), BatteryMonitor.ParseBytechBattery(frame));

        frame[1] = 0x54;
        Assert.Null(BatteryMonitor.ParseBytechBattery(frame));

        frame[1] = 0x55;
        frame[26] = 101;
        Assert.Null(BatteryMonitor.ParseBytechBattery(frame));

        Assert.Null(BatteryMonitor.ParseBytechBattery(new byte[27]));
    }

    [Fact]
    public void BytechCapturedReplyReports37PercentAndNotCharging()
    {
        // Live capture from this machine after the receiver was re-plugged: 00 55 10 38 ..., 0x25 at byte 26.
        var frame = new byte[]
        {
            0x00,0x55,0x10,0x38,0x00,0x00,0x00,0x00,0x00,0x40,0x00,0x00,0x0C,0xA6,0x38,0x08,
            0x29,0x31,0x01,0x00,0x00,0x11,0x00,0x31,0x00,0x02,0x25,0x00,0x00,0x00,0x00,0x00,
            0x00,0xC8,0x00,0x00,0x00,0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00
        };
        Assert.Equal(65, frame.Length);
        Assert.Equal((37, false), BatteryMonitor.ParseBytechBattery(frame));
    }

    [Fact]
    public void BytechRequestMatchesTheCapturedFrame()
    {
        var request = BatteryMonitor.BuildBytechRequest();
        Assert.Equal(65, request.Length);
        Assert.Equal(0x00, request[0]);
        Assert.Equal(new byte[] { 0xAA, 0x10, 0x38, 0x00, 0x00, 0x00, 0x01, 0x00 }, request[1..9]);
        Assert.All(request[9..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void OnlyTheMeasuredCollectionsAreAccepted()
    {
        Assert.True(BatteryMonitor.IsRapooCommandCollection(0xFF00, 0x000E, 32));
        Assert.False(BatteryMonitor.IsRapooCommandCollection(0xFF00, 0x0002, 32));
        Assert.False(BatteryMonitor.IsRapooCommandCollection(0xFF00, 0x000E, 33));
        Assert.False(BatteryMonitor.IsRapooCommandCollection(0xFF60, 0x000E, 32));

        Assert.True(BatteryMonitor.IsRapooReplyCollection(0xFF00, 0x0002, 7));
        Assert.False(BatteryMonitor.IsRapooReplyCollection(0xFF00, 0x0002, 10));

        Assert.True(BatteryMonitor.IsBytechCollection(0xFF60, 0x0061, 65));
        Assert.False(BatteryMonitor.IsBytechCollection(0xFF60, 0x0071, 65));
        Assert.False(BatteryMonitor.IsBytechCollection(0xFF60, 0x0061, 64));
    }

    [Fact]
    public void SilentReceiverKeepsItsLastGoodReadingWhileItStaysPresent()
    {
        var cache = new Dictionary<string, (DeviceBattery Device, long At)>();
        var first = BatteryMonitor.ResolveHidSamples(
            [new BatteryMonitor.HidSample("Rapoo VT3PRO", "mouse", 44, null)],
            cache, now: 1_000);
        Assert.Equal([new DeviceBattery("Rapoo VT3PRO", 44, "mouse", null)], first);

        // The mouse is asleep: the receiver is still there but answers nothing.
        var second = BatteryMonitor.ResolveHidSamples(
            [new BatteryMonitor.HidSample("Rapoo VT3PRO", "mouse", null, null)],
            cache, now: 1_500);
        Assert.Equal([new DeviceBattery("Rapoo VT3PRO", 44, "mouse", null)], second);

        // Still plugged in a day later: the receiver is there, so the row keeps the last reading.
        var still = BatteryMonitor.ResolveHidSamples(
            [new BatteryMonitor.HidSample("Rapoo VT3PRO", "mouse", null, null)],
            cache, now: 86_400_000);
        Assert.Equal([new DeviceBattery("Rapoo VT3PRO", 44, "mouse", null)], still);
    }

    [Fact]
    public void UnpluggedReceiverDisappearsImmediately()
    {
        var cache = new Dictionary<string, (DeviceBattery Device, long At)>
        {
            ["mouse|Rapoo VT3PRO"] = (new DeviceBattery("Rapoo VT3PRO", 44, "mouse", null), 1_000)
        };

        // The scan did not see that receiver at all, so no sample arrives for it.
        var devices = BatteryMonitor.ResolveHidSamples(
            [new BatteryMonitor.HidSample("AULA F87S 8K", "keyboard", 80, false)],
            cache, now: 1_010);

        Assert.Equal([new DeviceBattery("AULA F87S 8K", 80, "keyboard", false)], devices);
    }

    [Fact]
    public void WiredAndWirelessReadingsOfOneRapooCollapseIntoOneRow()
    {
        var cache = new Dictionary<string, (DeviceBattery Device, long At)>();
        var devices = BatteryMonitor.ResolveHidSamples(
            [
                new BatteryMonitor.HidSample("Rapoo VT3PRO", "mouse", 39, null),
                new BatteryMonitor.HidSample("Rapoo VT3PRO", "mouse", 41, null)
            ],
            cache, now: 1_000);

        Assert.Equal([new DeviceBattery("Rapoo VT3PRO", 41, "mouse", null)], devices);
    }

    [Fact]
    public void ChargingWinsWhenTheSameDeviceIsReportedTwice()
    {
        var devices = BatteryMonitor.MergeHidIntoBluetooth(
            [new DeviceBattery("AULA F87S 8K", 80, "keyboard", null)],
            [
                new DeviceBattery("AULA F87S 8K", 81, "keyboard", null),
                new DeviceBattery("AULA F87S 8K", 79, "keyboard", true)
            ]);

        Assert.Equal([new DeviceBattery("AULA F87S 8K", 79, "keyboard", true)], devices);
    }

    [Fact]
    public void BatteryCacheRoundTripsAndExpiresOldReadings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "phonebox-battery-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var cache = new Dictionary<string, (DeviceBattery Device, long At)>
            {
                ["keyboard|AULA F87S 8K"] = (new DeviceBattery("AULA F87S 8K", 37, "keyboard", false), 1_000_000),
                ["mouse|Rapoo VT3PRO"] = (new DeviceBattery("Rapoo VT3PRO", 62, "mouse", null), 1_000_000)
            };
            BatteryMonitor.WriteBatteryCache(path, cache);

            var loaded = BatteryMonitor.ReadBatteryCache(path, 1_060_000, 3_600_000);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(new DeviceBattery("AULA F87S 8K", 37, "keyboard", false), loaded["keyboard|AULA F87S 8K"].Device);
            Assert.Equal(62, loaded["mouse|Rapoo VT3PRO"].Device.Level);

            // Older than the allowed age: a restart must not resurrect an ancient value.
            Assert.Empty(BatteryMonitor.ReadBatteryCache(path, 4_600_001, 3_600_000));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void CorruptBatteryCacheIsIgnoredAndAFreshReadingStillWins()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "phonebox-battery-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{ not json");
            var cache = BatteryMonitor.ReadBatteryCache(path, 1_000_000, 3_600_000);
            Assert.Empty(cache);

            var devices = BatteryMonitor.ResolveHidSamples(
                [new BatteryMonitor.HidSample("AULA F87S 8K", "keyboard", 35, false)],
                cache, 1_000_000);
            Assert.Equal([new DeviceBattery("AULA F87S 8K", 35, "keyboard", false)], devices);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void UnchangedReadingKeepsItsCacheTimestampSoTheFileIsNotRewritten()
    {
        var cache = new Dictionary<string, (DeviceBattery Device, long At)>
        {
            ["mouse|Rapoo VT3PRO"] = (new DeviceBattery("Rapoo VT3PRO", 62, "mouse", null), 1_000)
        };

        BatteryMonitor.ResolveHidSamples(
            [new BatteryMonitor.HidSample("Rapoo VT3PRO", "mouse", 62, null)],
            cache, 9_999);

        Assert.Equal(1_000, cache["mouse|Rapoo VT3PRO"].At);
    }
}
