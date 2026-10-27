using Xunit;

// Battery values from GetSystemPowerStatus and what they publish to HA.
public class BatteryTests
{
    [Fact]
    public void NoSystemBattery() => Assert.Null(SensorService.Battery(1, 128, 255));

    [Fact]
    public void PluggedIn()
    {
        var b = SensorService.Battery(1, 8, 87)!;
        Assert.Equal(87, b.Level);
        Assert.True(b.OnAcPower);
    }

    [Fact]
    public void OnBattery() => Assert.False(SensorService.Battery(0, 1, 40)!.OnAcPower);

    // 255 is "unknown" for both fields.
    [Fact]
    public void UnknownValues()
    {
        var b = SensorService.Battery(255, 0, 60)!;
        Assert.Equal(60, b.Level);
        Assert.Null(b.OnAcPower);
        Assert.Null(SensorService.Battery(255, 255, 255));
    }

    [Fact]
    public void HaComponents()
    {
        var m = new MetricsSnapshot { Battery = new BatteryMetrics { Level = 87, OnAcPower = true } };
        var topics = HaDiscovery.ComponentTopics(m, false, "pc", "host");
        Assert.Equal("pc/host/battery/level", topics["battery_level"]);
        Assert.Equal("pc/host/battery/ac", topics["battery_ac"]);

        var desktop = new MetricsSnapshot();
        Assert.DoesNotContain("battery_ac", HaDiscovery.ComponentTopics(desktop, false, "pc", "host").Keys);
        Assert.NotEqual(HaDiscovery.Fingerprint(m, false), HaDiscovery.Fingerprint(desktop, false));
    }
}
