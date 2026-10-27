using Xunit;

// Idle time from the 32-bit tick counts of GetLastInputInfo.
public class IdleTests
{
    [Theory]
    [InlineData(10_000u, 10_000u, 0)]
    [InlineData(75_500u, 10_000u, 65)]
    // The tick counter wrapped between the last input and now.
    [InlineData(4_000u, uint.MaxValue - 5_999u, 10)]
    public void Seconds(uint now, uint lastInput, int expected) =>
        Assert.Equal(expected, SensorService.IdleSeconds(now, lastInput));
}
