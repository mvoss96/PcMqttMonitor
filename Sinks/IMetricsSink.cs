// One output path for metric snapshots. Implementations manage their own
// connection state — the main loop just calls PublishAsync once per cycle
// (while not paused) and logs any exception per sink, so one failing output
// never blocks the others.
interface IMetricsSink : IAsyncDisposable
{
    string Name { get; }

    Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct);

    // Pause/resume transitions. Only MQTT cares (availability topic);
    // stateless stream sinks keep the default no-op.
    Task SetOnlineAsync(bool online, CancellationToken ct) => Task.CompletedTask;
}
