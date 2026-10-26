using System.IO.Ports;

// Writes each snapshot as one JSON line to a serial port (e.g. an ESP32 status
// display). The port is opened lazily and reopened after errors — an unplugged
// adapter is retried every cycle but logged only on state changes, so app.log
// stays quiet while nothing is connected.
sealed class SerialSink : IMetricsSink
{
    readonly SerialConfig _config;
    readonly Action<string> _log;
    // Outputs card status: (true, "") while the port is open, (false, reason)
    // when it cannot be used, (null, "") once the sink is gone.
    readonly Action<bool?, string> _setStatus;
    SerialPort? _port;
    bool _openFailureLogged;
    string? _lastStatus;

    public string Name => "Serial";

    public SerialSink(SerialConfig config, Action<string> log, Action<bool?, string> setStatus)
    {
        _config = config.Clone();
        _log = log;
        _setStatus = setStatus;
    }

    // Never throws: open/write failures are part of normal operation here
    // (device unplugged) and handled with the reopen state machine above.
    public Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        if (_port == null)
        {
            try
            {
                var port = new SerialPort(_config.Port, _config.Baud) { WriteTimeout = 2000 };
                port.Open();
                _port = port;
                _openFailureLogged = false;
                _log($"Serial port {_config.Port} opened @ {_config.Baud} baud.");
                Report(true, "");
            }
            catch (Exception ex)
            {
                if (!_openFailureLogged)
                {
                    _log($"[error] Serial open {_config.Port}: {ex.Message} — retrying quietly.");
                    _openFailureLogged = true;
                }
                Report(false, FailureText(ex));
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
            _log($"[error] Serial write {_config.Port}: {ex.Message} — port closed, will reopen.");
            try { _port.Dispose(); } catch { }
            _port = null;
            _openFailureLogged = true;   // the very next reopen attempt fails silently
            Report(false, FailureText(ex));
        }
        return Task.CompletedTask;
    }

    // Passes the state on only when it changes; this runs every publish cycle.
    void Report(bool ok, string text)
    {
        var key = $"{ok}|{text}";
        if (key == _lastStatus) return;
        _lastStatus = key;
        _setStatus(ok, text);
    }

    // "Access denied" means another program has the port open; the other
    // exceptions SerialPort throws for a bad or missing port all mean "not found".
    string FailureText(Exception ex) => ex switch
    {
        UnauthorizedAccessException => string.Format(L.T.OutSerialBusy, _config.Port),
        IOException or ArgumentException or InvalidOperationException
            => string.Format(L.T.OutSerialNotFound, _config.Port),
        _ => string.Format(L.T.OutConnectFailed, ex.Message),
    };

    public ValueTask DisposeAsync()
    {
        try { _port?.Dispose(); } catch { }
        _setStatus(null, "");
        return ValueTask.CompletedTask;
    }
}
