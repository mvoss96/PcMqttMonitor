using System.Net.Sockets;

// State shown on an output's card: Off = grey/neutral, Good = green, Error = red.
enum StatusKind { Off, Good, Error }

// Reports one sink's state to the Outputs page and logs its failures. The UI
// gets an update only when the state changes, and the log gets the first
// failure of a streak instead of one line per publish cycle. Thread-safe:
// TcpSink reports from its accept loop as well as from the publish loop.
sealed class SinkStatus(string sink, Action<string> log, Action<string, StatusKind?, string> report)
{
    readonly object _lock = new();
    string? _last;

    // True from a failure until the next Ok — sinks use it to log a recovery
    // or to keep quiet on retries.
    public bool Failing { get; private set; }

    public void Ok(string text)
    {
        Failing = false;
        Set(StatusKind.Good, text);
    }

    // Neutral state, e.g. "Connecting to …".
    public void Pending(string text) => Set(StatusKind.Off, text);

    // target names what failed in the card text: a host, "COM9" or "Port 5556".
    public void Fail(Exception ex, string target, string action)
    {
        if (!Failing)
            log($"[error] {sink} {action}: {ex.Message} — retrying, further failures not logged.");
        Failing = true;
        Set(StatusKind.Error, Describe(ex, target));
    }

    public void Error(string text) => Set(StatusKind.Error, text);

    // The sink is gone; the card falls back to what the settings say.
    public void Clear()
    {
        lock (_lock) _last = null;
        report(sink, null, "");
    }

    void Set(StatusKind kind, string text)
    {
        lock (_lock)
        {
            var key = $"{kind}|{text}";
            if (key == _last) return;
            _last = key;
        }
        report(sink, kind, text);
    }

    // One set of texts for all sinks. MQTTnet 5 reports a rejected CONNECT
    // only in the message text, which contains the result code name.
    static string Describe(Exception ex, string target)
    {
        if (ex is OperationCanceledException)   // MqttSink's connect timeout
            return string.Format(L.T.OutUnreachable, target);
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException se:
                    return se.SocketErrorCode switch
                    {
                        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                            => string.Format(L.T.OutNotFound, target),
                        SocketError.ConnectionRefused => L.T.OutRefused,
                        SocketError.AddressAlreadyInUse or SocketError.AccessDenied
                            => string.Format(L.T.OutInUse, target),
                        _ => string.Format(L.T.OutUnreachable, target),
                    };
                case UnauthorizedAccessException:   // serial port opened by another program
                    return string.Format(L.T.OutInUse, target);
                case FileNotFoundException or ArgumentException:   // serial port missing or invalid
                    return string.Format(L.T.OutNotFound, target);
            }
        }
        if (ex.Message.Contains("BadUserNameOrPassword") || ex.Message.Contains("NotAuthorized"))
            return L.T.OutAuthFailed;
        return string.Format(L.T.OutError, ex.Message);
    }
}
