using System.IO.Ports;

// Writes each snapshot as one JSON line to a serial port (e.g. an ESP32 status
// display). The port is opened lazily and reopened after errors — an unplugged
// adapter is retried every cycle but logged only once, so app.log stays quiet
// while nothing is connected.
sealed class SerialSink : IMetricsSink
{
    readonly SerialConfig _config;
    readonly Action<string> _log;
    readonly SinkStatus _status;
    SerialPort? _port;

    public string Name => "Serial";

    public SerialSink(SerialConfig config, Action<string> log, Action<string, StatusKind?, string> report)
    {
        _config = config.Clone();
        _log = log;
        _status = new SinkStatus(Name, log, report);
    }

    // Never throws: open/write failures are part of normal operation here
    // (device unplugged) and handled by the lazy reopen above.
    public Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        if (_port == null)
        {
            try
            {
                var port = new SerialPort(_config.Port, _config.Baud) { WriteTimeout = 2000 };
                port.Open();
                _port = port;
                _log($"Serial port {_config.Port} opened @ {_config.Baud} baud.");
                _status.Ok(string.Format(L.T.OutSendingCom, _config.Port, _config.Baud));
            }
            catch (Exception ex)
            {
                _status.Fail(ex, _config.Port, "open");
                return Task.CompletedTask;
            }
        }

        try
        {
            var json = MetricsJson.SerializeToUtf8Bytes(metrics);
            _port.Write(json, 0, json.Length);
            _port.Write("\n");
        }
        catch (Exception ex)
        {
            // Unplugged mid-run — close and fall back to the lazy reopen path.
            try { _port.Dispose(); } catch { }
            _port = null;
            _status.Fail(ex, _config.Port, "write");
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _port?.Dispose(); } catch { }
        _status.Clear();
        return ValueTask.CompletedTask;
    }
}
