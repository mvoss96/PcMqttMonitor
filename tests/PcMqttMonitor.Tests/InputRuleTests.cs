using Xunit;

// Field rules of the Outputs page: what may be saved and what turns the field red.
public class InputRuleTests
{
    [Theory]
    [InlineData("192.168.2.41", true)]
    [InlineData("192.168.2.255", true)]     // subnet broadcast is a valid UDP target
    [InlineData(" 10.0.0.1 ", true)]
    [InlineData("", true)]                  // empty: the card shows "set a host" instead
    [InlineData("192.168.2.411", false)]
    [InlineData("192.168.2", false)]
    [InlineData("192.168.2.255eeee", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("homeassistant.local", true)]
    [InlineData("my-pc", true)]
    [InlineData("broker.example.com", true)]
    [InlineData("mqtt broker", false)]
    [InlineData("bad..host", false)]
    [InlineData("-pc", false)]
    [InlineData("pc-", false)]
    [InlineData("hä.local", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fe80::zz", false)]
    public void Host(string text, bool valid) => Assert.Equal(valid, OutputsPage.ValidHost(text));

    [Theory]
    [InlineData("1", true)]
    [InlineData("5556", true)]
    [InlineData("65535", true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("9871111111", false)]
    [InlineData("12a", false)]
    [InlineData("", false)]
    public void Port(string text, bool valid) => Assert.Equal(valid, OutputsPage.ValidPort(text));

    [Theory]
    [InlineData("115200", true)]
    [InlineData("0", false)]
    [InlineData("abc", false)]
    public void Baud(string text, bool valid) => Assert.Equal(valid, OutputsPage.ValidBaud(text));
}

// Progressive shortening of CPU/GPU names for the dashboard card titles.
public class NameStageTests
{
    [Fact]
    public void AmdCpu() => Assert.Equal(
        ["AMD Ryzen 7 9800X3D", "AMD 9800X3D", "9800X3D"],
        DashboardView.NameStages("AMD Ryzen 7 9800X3D 8-Core Processor"));

    [Fact]
    public void NvidiaGpu() => Assert.Equal(
        ["NVIDIA GeForce RTX 5070", "NVIDIA RTX 5070", "RTX 5070"],
        DashboardView.NameStages("NVIDIA GeForce RTX 5070"));

    [Fact]
    public void IntelCpu() => Assert.Equal(
        ["12th Gen Intel Core i7-12700K", "Intel Core i7-12700K", "Intel i7-12700K", "i7-12700K"],
        DashboardView.NameStages("12th Gen Intel(R) Core(TM) i7-12700K"));
}
