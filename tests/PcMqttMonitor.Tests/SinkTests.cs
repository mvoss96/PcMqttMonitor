using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Xunit;

// Collects what a sink reports to the Outputs page and writes to the log.
// Thread-safe: TcpSink reports from its accept loop.
sealed class Recorder
{
    readonly object _lock = new();
    readonly List<(StatusKind? Kind, string Text)> _reports = new();
    readonly List<string> _logs = new();

    public void Report(string sink, StatusKind? kind, string text) { lock (_lock) _reports.Add((kind, text)); }
    public void Log(string line) { lock (_lock) _logs.Add(line); }

    public (StatusKind? Kind, string Text) Last { get { lock (_lock) return _reports[^1]; } }
    public int ErrorLines { get { lock (_lock) return _logs.Count(l => l.StartsWith("[error]")); } }

    // Waits for a report from another thread (TCP accept loop).
    public async Task<bool> WaitFor(Func<(StatusKind? Kind, string Text), bool> match, int timeoutMs = 3000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 50)
        {
            lock (_lock) if (_reports.Count > 0 && match(_reports[^1])) return true;
            await Task.Delay(50);
        }
        return false;
    }
}

public class SinkStatusTests
{
    [Fact]
    public void Texts()
    {
        Assert.Equal(string.Format(L.T.OutNotFound, "h"), SinkStatus.Describe(new SocketException((int)SocketError.HostNotFound), "h"));
        Assert.Equal(L.T.OutRefused, SinkStatus.Describe(new SocketException((int)SocketError.ConnectionRefused), "h"));
        Assert.Equal(string.Format(L.T.OutInUse, "Port 1"), SinkStatus.Describe(new SocketException((int)SocketError.AddressAlreadyInUse), "Port 1"));
        Assert.Equal(string.Format(L.T.OutUnreachable, "h"), SinkStatus.Describe(new SocketException((int)SocketError.TimedOut), "h"));
        Assert.Equal(string.Format(L.T.OutUnreachable, "h"), SinkStatus.Describe(new OperationCanceledException(), "h"));
        Assert.Equal(string.Format(L.T.OutInUse, "COM3"), SinkStatus.Describe(new UnauthorizedAccessException(), "COM3"));
        Assert.Equal(string.Format(L.T.OutNotFound, "COM3"), SinkStatus.Describe(new FileNotFoundException(), "COM3"));
        Assert.Equal(L.T.OutAuthFailed, SinkStatus.Describe(new Exception("Connecting failed (BadUserNameOrPassword)."), "h"));
        Assert.Equal(string.Format(L.T.OutError, "boom"), SinkStatus.Describe(new Exception("boom"), "h"));
        // The socket error is found inside a wrapping exception (as MQTTnet does it).
        Assert.Equal(L.T.OutRefused, SinkStatus.Describe(
            new Exception("outer", new SocketException((int)SocketError.ConnectionRefused)), "h"));
    }

    [Fact]
    public void ReportsOnlyChangesAndLogsFirstFailureOnce()
    {
        var r = new Recorder();
        int reports = 0;
        var status = new SinkStatus("X", r.Log, (s, k, t) => { reports++; r.Report(s, k, t); });
        status.Ok("fine");
        status.Ok("fine");
        Assert.Equal(1, reports);
        for (int i = 0; i < 3; i++) status.Fail(new Exception("boom"), "h", "send");
        Assert.Equal(2, reports);
        Assert.Equal(1, r.ErrorLines);
        Assert.True(status.Failing);
        status.Ok("fine");
        Assert.False(status.Failing);
        status.Clear();
        Assert.Null(r.Last.Kind);
    }
}

public class UdpSinkTests
{
    static UdpSink Create(Recorder r, string host, int port) =>
        new(new UdpConfig { Enabled = true, Host = host, Port = port }, r.Log, r.Report);

    [Fact]
    public async Task LoopbackIsReportedAsSending()
    {
        var r = new Recorder();
        await using (var sink = Create(r, "127.0.0.1", 55555))
        {
            await sink.PublishAsync(new MetricsSnapshot(), CancellationToken.None);
            Assert.Equal((StatusKind.Good, string.Format(L.T.OutSendingTo, "127.0.0.1", 55555)), r.Last);
        }
        Assert.Null(r.Last.Kind);   // cleared on dispose
    }

    [Fact]
    public async Task UnknownHostIsRedAndLoggedOnce()
    {
        var r = new Recorder();
        await using var sink = Create(r, "gibtsnicht.invalid", 55555);
        for (int i = 0; i < 3; i++)
            await sink.PublishAsync(new MetricsSnapshot(), CancellationToken.None);   // must not throw
        Assert.Equal((StatusKind.Error, string.Format(L.T.OutNotFound, "gibtsnicht.invalid")), r.Last);
        Assert.Equal(1, r.ErrorLines);
    }

    [Fact]
    public async Task DatagramArrives()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var r = new Recorder();
        await using var sink = Create(r, "127.0.0.1", port);
        var receive = receiver.ReceiveAsync();
        await sink.PublishAsync(new MetricsSnapshot { Host = "test-pc" }, CancellationToken.None);
        Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(3000)));
        Assert.Contains("\"Host\":\"test-pc\"", System.Text.Encoding.UTF8.GetString(receive.Result.Buffer));
    }
}

public class TcpSinkTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task TakenPortIsRedThenRecoversAndCountsClients()
    {
        int port = FreePort();
        var blocker = new TcpListener(IPAddress.Any, port);
        blocker.Start();
        var r = new Recorder();
        await using var sink = new TcpSink(new TcpConfig { Enabled = true, ListenPort = port }, r.Log, r.Report);
        Assert.Equal((StatusKind.Error, string.Format(L.T.OutInUse, $"Port {port}")), r.Last);

        blocker.Stop();
        await sink.PublishAsync(new MetricsSnapshot(), CancellationToken.None);   // retries listening
        Assert.Equal(StatusKind.Good, r.Last.Kind);
        Assert.Contains(string.Format(L.T.OutConnectedCount, 0), r.Last.Text);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        Assert.True(await r.WaitFor(s => s.Text.Contains(string.Format(L.T.OutConnectedCount, 1))));

        // The client receives one JSON line per publish.
        await sink.PublishAsync(new MetricsSnapshot { Host = "test-pc" }, CancellationToken.None);
        using var reader = new StreamReader(client.GetStream());
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Contains("\"Host\":\"test-pc\"", line);
    }
}

public class SerialSinkTests
{
    [Fact]
    public async Task MissingPortIsRedAndLoggedOnce()
    {
        // A port number no machine here has; the CI runner has no COM ports at all.
        string port = "COM250";
        Assert.DoesNotContain(port, SerialPort.GetPortNames());
        var r = new Recorder();
        await using var sink = new SerialSink(new SerialConfig { Enabled = true, Port = port, Baud = 115200 }, r.Log, r.Report);
        for (int i = 0; i < 3; i++)
            await sink.PublishAsync(new MetricsSnapshot(), CancellationToken.None);
        Assert.Equal((StatusKind.Error, string.Format(L.T.OutNotFound, port)), r.Last);
        Assert.Equal(1, r.ErrorLines);
    }
}
