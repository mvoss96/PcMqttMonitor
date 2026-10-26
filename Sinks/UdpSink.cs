using System.Net.Sockets;

// Sends each snapshot as a single JSON datagram to a fixed host:port.
// Fire-and-forget: UDP has no acknowledgement, so a missing receiver goes
// unnoticed; only local send failures (unknown host, no route) are reported.
sealed class UdpSink : IMetricsSink
{
    readonly UdpClient _client = new();
    readonly UdpConfig _config;
    readonly Action<string> _log;
    readonly SinkStatus _status;

    public string Name => "UDP";

    public UdpSink(UdpConfig config, Action<string> log, Action<string, StatusKind?, string> report)
    {
        _config = config.Clone();
        _log = log;
        _status = new SinkStatus(Name, log, report);
    }

    // Never throws: a failed send is reported on the card and logged once.
    public async Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        try
        {
            await _client.SendAsync(MetricsJson.SerializeToUtf8Bytes(metrics), _config.Host, _config.Port, ct);
            if (_status.Failing) _log($"UDP sending to {_config.Host}:{_config.Port} again.");
            _status.Ok(string.Format(L.T.OutSendingTo, _config.Host, _config.Port));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _status.Fail(ex, _config.Host, "send");
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        _status.Clear();
        return ValueTask.CompletedTask;
    }
}
