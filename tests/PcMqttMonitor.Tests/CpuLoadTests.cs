using Xunit;

// CPU load from two GetSystemTimes samples (the fallback without LHM CPU data).
public class CpuLoadTests
{
    [Theory]
    [InlineData(100ul, 100ul, 175ul, 125ul, 25f)]    // 75 idle, 25 busy
    [InlineData(0ul, 0ul, 0ul, 50ul, 100f)]          // busy only
    [InlineData(0ul, 0ul, 80ul, 0ul, 0f)]            // idle only
    public void FromSamples(ulong idle0, ulong busy0, ulong idle1, ulong busy1, float load) =>
        Assert.Equal(load, SensorService.CpuLoad(idle0, busy0, idle1, busy1));

    // No time passed between the samples: no value instead of a division by zero.
    [Fact]
    public void NoElapsedTime() => Assert.Null(SensorService.CpuLoad(5, 5, 5, 5));
}
