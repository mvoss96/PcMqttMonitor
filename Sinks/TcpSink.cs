using System.Net;
using System.Net.Sockets;

// Line-delimited JSON server: listens on a port and pushes one JSON object
// per publish cycle (terminated by '\n') to every connected client.
sealed class TcpSink : IMetricsSink
{
    readonly TcpListener _listener;
    readonly List<TcpClient> _clients = new();
    readonly Action<string> _log;
    readonly CancellationTokenSource _stop = new();
    readonly Task _acceptLoop;

    public string Name => "TCP";

    public TcpSink(TcpConfig config, Action<string> log)
    {
        _log = log;
        _listener = new TcpListener(IPAddress.Any, config.ListenPort);
        _listener.Start();   // throws if the port is taken — caller logs and skips the sink
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _log($"TCP stream listening on port {config.ListenPort}.");
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                client.NoDelay = true;
                lock (_clients) _clients.Add(client);
                _log($"TCP client connected: {client.Client.RemoteEndPoint} ({ClientCount} total)");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested) _log($"[error] TCP accept: {ex.Message}");
        }
    }

    public async Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        TcpClient[] clients;
        lock (_clients) clients = _clients.ToArray();
        if (clients.Length == 0) return;

        var json = MetricsJson.SerializeToUtf8Bytes(metrics);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';

        foreach (var client in clients)
        {
            try
            {
                // A client that stopped reading fills its send buffer and would
                // stall the whole publish cycle — cap the write, then drop it.
                await client.GetStream().WriteAsync(line, ct).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                lock (_clients) _clients.Remove(client);
                client.Dispose();
                _log($"TCP client disconnected ({ClientCount} remaining)");
            }
        }
    }

    int ClientCount { get { lock (_clients) return _clients.Count; } }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        _stop.Dispose();
    }
}
