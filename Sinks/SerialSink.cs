using System.IO.Ports;

// Writes each snapshot as one JSON line to a serial port (e.g. an ESP32 status
// display). The port is opened lazily and reopened after errors — an unplugged
// adapter is retried every cycle but logged only on state changes, so app.log
// stays quiet while nothing is connected.
sealed class SerialSink : IMetricsSink
{
    readonly SerialConfig _config;
    readonly Action<string> _log;
    SerialPort? _port;
    bool _openFailureLogged;

    public string Name => "Serial";

    public SerialSink(SerialConfig config, Action<string> log)
    {
        _config = config;
        _log = log;
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
            }
            catch (Exception ex)
            {
                if (!_openFailureLogged)
                {
                    _log($"[error] Serial open {_config.Port}: {ex.Message} — retrying quietly.");
                    _openFailureLogged = true;
                }
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
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        try { _port?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }
}
