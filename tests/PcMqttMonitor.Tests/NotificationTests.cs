using Xunit;

// Payloads on the notify topic and the notify entity in HA discovery.
public class NotificationTests
{
    [Fact]
    public void PlainTextIsTheMessage() =>
        Assert.Equal(new Notification("Home Assistant", "Washing machine done"),
            Notification.Parse("  Washing machine done \n", "Home Assistant"));

    [Fact]
    public void JsonCarriesTitleAndMessage() =>
        Assert.Equal(new Notification("Door", "Someone rang"),
            Notification.Parse("""{"title": "Door", "message": "Someone rang"}""", "Home Assistant"));

    [Fact]
    public void JsonWithoutTitleKeepsTheDefault() =>
        Assert.Equal(new Notification("Home Assistant", "Hi"),
            Notification.Parse("""{"message": "Hi"}""", "Home Assistant"));

    // Text that only starts like JSON is shown as it is.
    [Fact]
    public void BrokenJsonIsText() =>
        Assert.Equal("{not json", Notification.Parse("{not json", "HA")!.Message);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"message": ""}""")]
    public void EmptyShowsNothing(string payload) => Assert.Null(Notification.Parse(payload, "HA"));

    [Fact]
    public void LongTextIsCut()
    {
        var n = Notification.Parse(new string('x', 400), "HA")!;
        Assert.Equal(255, n.Message.Length);
        Assert.EndsWith("…", n.Message);
    }

    // SUBACK codes: only the granted ones mean messages will arrive.
    [Theory]
    [InlineData(MQTTnet.MqttClientSubscribeResultCode.GrantedQoS1, true)]
    [InlineData(MQTTnet.MqttClientSubscribeResultCode.NotAuthorized, false)]
    [InlineData(MQTTnet.MqttClientSubscribeResultCode.UnspecifiedError, false)]
    public void SubscriptionGranted(MQTTnet.MqttClientSubscribeResultCode code, bool granted) =>
        Assert.Equal(granted, MqttSink.Granted(code));

    [Fact]
    public void DiscoveryAdvertisesTheNotifyEntityOnlyWhenEnabled()
    {
        var m = new MetricsSnapshot();
        Assert.Equal("pc/host/notify", HaDiscovery.ComponentTopics(m, true, "pc", "host")["notify"]);
        Assert.False(HaDiscovery.ComponentTopics(m, false, "pc", "host").ContainsKey("notify"));
        Assert.NotEqual(HaDiscovery.Fingerprint(m, true), HaDiscovery.Fingerprint(m, false));
    }
}
