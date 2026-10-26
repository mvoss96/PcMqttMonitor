using System.Net;
using System.Net.Sockets;

// Line-delimited JSON server: listens on a port and pushes one JSON object
// per publish cycle (terminated by '\n') to every connected client. If the
// port is taken, listening is retried every cycle like a serial reopen.
sealed class TcpSink : IMetricsSink
{
    readonly int _listenPort;
    readonly List<TcpClient> _clients = new();
    readonly Action<string> _log;
    readonly SinkStatus _status;
    readonly CancellationTokenSource _stop = new();
    TcpListener? _listener;
    Task? _acceptLoop;

    public string Name => "TCP";

    public TcpSink(TcpConfig config, Action<string> log, Action<string, StatusKind?, string> report)
    {
        _listenPort = config.ListenPort;
        _log = log;
        _status = new SinkStatus(Name, log, report);
        TryListen();   // report the state right away instead of after the first cycle
    }

    bool TryListen()
    {
        if (_listener != null) return true;
        try
        {
            var listener = new TcpListener(IPAddress.Any, _listenPort);
            listener.Start();
            _listener = listener;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
            _log($"TCP stream listening on port {_listenPort}.");
            ReportClients();
            return true;
        }
        catch (Exception ex)
        {
            _status.Fail(ex, $"Port {_listenPort}", "listen");
            return false;
        }
    }

    async Task AcceptLoopAsync(TcpListener listener)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stop.Token);
                client.NoDelay = true;
                lock (_clients) _clients.Add(client);
                _log($"TCP client connected: {client.Client.RemoteEndPoint} ({ClientCount} total)");
                ReportClients();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested) _log($"[error] TCP accept: {ex.Message}");
        }
    }

    // "Listening on port 5556 · 2 connected"
    void ReportClients() => _status.Ok(
        $"{string.Format(L.T.OutListeningOn, _listenPort)} · {string.Format(L.T.OutConnectedCount, ClientCount)}");

    public async Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        if (!TryListen()) return;

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
                ReportClients();
            }
        }
    }

    int ClientCount { get { lock (_clients) return _clients.Count; } }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener?.Stop();
        if (_acceptLoop != null)
            try { await _acceptLoop; } catch { }
        lock (_clients)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        _stop.Dispose();
        _status.Clear();
    }
}
