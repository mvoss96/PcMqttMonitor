using Xunit;

// The number shown in the tray icon for each mode.
public class TrayIconTests
{
    static MetricsSnapshot Snapshot() => new()
    {
        Cpu = new CpuMetrics { Load = 100, TempC = 79.6f },
        Gpu = new GpuMetrics { Load = 7, TempC = 81.2f },
        Ram = new RamMetrics { Load = 64 },
    };

    [Theory]
    [InlineData("cpu_load", "99", false)]   // two digits at most
    [InlineData("cpu_temp", "80", true)]    // 79.6 °C shows as 80, so it is red
    [InlineData("gpu_load", "7", false)]
    [InlineData("gpu_temp", "81", true)]
    [InlineData("ram_load", "64", false)]
    public void Value(string mode, string text, bool hot) =>
        Assert.Equal((text, hot), TrayIcon.Value(Snapshot(), mode));

    [Theory]
    [InlineData("logo")]
    [InlineData("load_bars")]
    [InlineData("unknown")]
    public void NoNumber(string mode) => Assert.Null(TrayIcon.Value(Snapshot(), mode));

    [Theory]
    [InlineData(20f, 0f)]      // below the scale: empty
    [InlineData(65f, 0.5f)]
    [InlineData(100f, 1f)]
    [InlineData(110f, 1f)]     // above the scale: full
    public void TempBarHeight(float tempC, float fraction) =>
        Assert.Equal(fraction, TrayIcon.TempFraction(tempC), 3);

    // 79.6 and 80.4 °C draw the same red bar, so they must give the same key;
    // 59.6 °C rounds to 60 and is yellow, 59.4 °C rounds to 59 and is green.
    [Theory]
    [InlineData(79.6f, 80.4f, true)]
    [InlineData(59.4f, 59.6f, false)]
    public void TempBarKeyFollowsTheDrawing(float a, float b, bool same)
    {
        var ma = Snapshot(); ma.Cpu!.TempC = a;
        var mb = Snapshot(); mb.Cpu!.TempC = b;
        Assert.Equal(same, TrayIcon.Key(ma, "temp_bars") == TrayIcon.Key(mb, "temp_bars"));
    }

    [Fact]
    public void MissingValueKeepsTheLogo() =>
        Assert.Null(TrayIcon.Value(new MetricsSnapshot(), "cpu_temp"));
}
