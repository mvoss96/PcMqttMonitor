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
    [InlineData("cpu_temp", "80", false)]   // rounded for display, below the threshold
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

    [Fact]
    public void MissingValueKeepsTheLogo() =>
        Assert.Null(TrayIcon.Value(new MetricsSnapshot(), "cpu_temp"));
}
