using System.Net.Sockets;

// Sends each snapshot as a single JSON datagram to a fixed host:port.
// Fire-and-forget: no connection state, a missing receiver costs nothing.
sealed class UdpSink : IMetricsSink
{
    readonly UdpClient _client = new();
    readonly UdpConfig _config;

    public string Name => "UDP";

    public UdpSink(UdpConfig config) => _config = config.Clone();

    public async Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
        => await _client.SendAsync(MetricsJson.SerializeToUtf8Bytes(metrics), _config.Host, _config.Port, ct);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
