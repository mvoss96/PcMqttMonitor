using System.Globalization;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

// The MQTT output path: connection management (one reconnect attempt per cycle),
// availability topic with LWT, the scalar subtopics + JSON status topic, and
// keeping the Home Assistant discovery config on the broker in sync.
sealed class MqttSink : IMetricsSink
{
    readonly MqttConfig _config;
    readonly Action<string> _log;
    readonly Action<string> _setStatus;               // tray context-menu status line
    readonly Action<bool, string> _setConnectionStatus; // status dot in the Settings tab
    readonly MqttClientFactory _factory = new();
    readonly IMqttClient _client;
    readonly MqttClientOptions _options;

    // Discovery state: republish when the toggle or the advertised component
    // set changes; sync once after every (re)connect so the retained config
    // on the broker always matches the current setting.
    bool _discoveryActive;
    string _discoveryFingerprint = "";
    bool _needsDiscoverySync = true;

    public string Name => "MQTT";

    // Retained "online"/"offline" marker. The LWT sets it to "offline" if we
    // die without saying goodbye; HA uses it as availability_topic.
    public static string AvailabilityTopic(string topicRoot, string host)
        => $"{topicRoot}/{host}/availability";

    public MqttSink(MqttConfig config, Action<string> log,
        Action<string> setStatus, Action<bool, string> setConnectionStatus)
    {
        _config = config;
        _log = log;
        _setStatus = setStatus;
        _setConnectionStatus = setConnectionStatus;

        _client = _factory.CreateMqttClient();
        _options = BuildOptions(_factory, config);
    }

    static MqttClientOptions BuildOptions(MqttClientFactory factory, MqttConfig config)
    {
        var builder = factory.CreateClientOptionsBuilder()
            .WithTcpServer(config.Host, config.Port)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{HostInfo.Id}")
            // LWT: if the connection dies without a clean disconnect (crash, power
            // loss), the broker publishes "offline" on our behalf once the
            // keep-alive times out — subscribers always learn we are gone.
            .WithWillTopic(AvailabilityTopic(config.TopicRoot, HostInfo.Id))
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (config.UseTls)
            builder = builder.WithTlsOptions(o => o.UseTls());
        return builder.Build();
    }

    public async Task PublishAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        // Connect/reconnect if needed — one attempt per cycle, non-blocking.
        if (!_client.IsConnected)
        {
            try
            {
                _log($"Connecting to {_config.Host}:{_config.Port}...");
                _setStatus(string.Format(L.T.StatusConnectingTo, $"{_config.Host}:{_config.Port}"));
                await _client.ConnectAsync(_options, ct);
                _log("MQTT connected.");
                _setConnectionStatus(true, $"{_config.Host}:{_config.Port}");
                await PublishAvailabilityAsync(online: true, ct);
                _needsDiscoverySync = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"[error] Connection failed: {ex.Message}");
                _setStatus(L.T.StatusDisconnected);
                _setConnectionStatus(false, "");
                return;
            }
        }

        await SyncDiscoveryAsync(metrics, ct);

        try
        {
            await PublishMetricsAsync(metrics, ct);
            _setStatus(string.Format(L.T.StatusConnected, $"{_config.Host}:{_config.Port}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _setStatus(string.Format(L.T.StatusError, ex.Message));
            if (!_client.IsConnected)
                _setConnectionStatus(false, "");
            throw;   // the main loop logs it as "[error] MQTT publish: ..."
        }
    }

    // Pause/resume: reflect the transition on the availability topic while the
    // connection is up, so HA shows the PC as unavailable while paused.
    public async Task SetOnlineAsync(bool online, CancellationToken ct)
    {
        if (_client.IsConnected)
            await PublishAvailabilityAsync(online, ct);
    }

    async Task SyncDiscoveryAsync(MetricsSnapshot metrics, CancellationToken ct)
    {
        try
        {
            bool want = _config.HaDiscoveryEnabled;
            var fingerprint = HaDiscovery.Fingerprint(metrics);
            if (_needsDiscoverySync || want != _discoveryActive
                || (want && fingerprint != _discoveryFingerprint))
            {
                if (want)
                {
                    await HaDiscovery.PublishConfigAsync(
                        _client, _config.TopicRoot, HostInfo.Id, metrics,
                        UpdateChecker.CurrentVersion.ToString(3),
                        _config.Host, _config.Port, ct);
                    _log("HA discovery config published.");
                }
                else
                {
                    await HaDiscovery.RemoveAsync(_client, HostInfo.Id, ct);
                    if (_discoveryActive) _log("HA discovery config removed.");
                }
                _discoveryActive = want;
                _discoveryFingerprint = fingerprint;
                _needsDiscoverySync = false;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log($"[error] discovery publish: {ex.Message}"); }
    }

    Task PublishAvailabilityAsync(bool online, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(AvailabilityTopic(_config.TopicRoot, HostInfo.Id))
            .WithPayload(online ? "online" : "offline")
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        return _client.PublishAsync(message, ct);
    }

    async Task PublishMetricsAsync(MetricsSnapshot m, CancellationToken ct)
    {
        var baseTopic = $"{_config.TopicRoot}/{HostInfo.Id}";
        var messages = new List<(string Topic, string Payload)>
        {
            // Full JSON snapshot on a dedicated sub-topic so MQTT clients that
            // auto-expand JSON (e.g. MQTT Explorer) don't create virtual nodes
            // that collide with the real scalar subtopics under the same prefix.
            ($"{baseTopic}/status", JsonSerializer.Serialize(m, MetricsJson.Options))
        };

        // Scalar subtopics come straight from the metric table.
        foreach (var def in MetricTable.All)
        {
            var value = def.Get(m);
            if (value == null || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) continue;
            messages.Add(($"{baseTopic}/{def.SubTopic}",
                value.Value.ToString(def.Format, CultureInfo.InvariantCulture)));
        }

        // Drives: dynamic count and a string payload — deliberately not in the table.
        if (m.Drives != null)
        {
            for (int i = 0; i < m.Drives.Count; i++)
            {
                var d = m.Drives[i];
                var prefix = $"{baseTopic}/drives/{i}";
                if (!string.IsNullOrEmpty(d.Name)) messages.Add(($"{prefix}/name", d.Name));
                AddFloat(messages, $"{prefix}/used",  d.UsedGb);
                AddFloat(messages, $"{prefix}/free",  d.FreeGb);
                AddFloat(messages, $"{prefix}/total", d.TotalGb);
                if (d.UsedPercent != null)
                    messages.Add(($"{prefix}/percent", d.UsedPercent.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }

        // Network: one subtree per active physical adapter — same dynamic-count
        // reasoning as drives. IP/MAC go out retained on purpose (Marcus' call).
        if (m.Network != null)
        {
            for (int i = 0; i < m.Network.Count; i++)
            {
                var a = m.Network[i];
                var prefix = $"{baseTopic}/net/{i}";
                if (!string.IsNullOrEmpty(a.Name)) messages.Add(($"{prefix}/name", a.Name));
                AddFloat(messages, $"{prefix}/up",   a.UploadKbps);
                AddFloat(messages, $"{prefix}/down", a.DownloadKbps);
                if (!string.IsNullOrEmpty(a.IpAddress)) messages.Add(($"{prefix}/ip",  a.IpAddress));
                if (!string.IsNullOrEmpty(a.Mac))       messages.Add(($"{prefix}/mac", a.Mac));
            }
        }

        foreach (var (topic, payload) in messages)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithPayloadFormatIndicator(MqttPayloadFormatIndicator.CharacterData)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                // Retained: the broker keeps the last value, so subscribers (e.g. HA
                // after a restart) get current readings immediately instead of
                // "unknown" until the next publish cycle. Combined with the
                // availability topic, stale values are shown as "unavailable".
                .WithRetainFlag(true)
                .Build();
            await _client.PublishAsync(message, ct);
        }
    }

    static void AddFloat(List<(string, string)> messages, string topic, float? value)
    {
        if (value == null || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) return;
        // "0.#" (max 1 decimal) keeps output consistent — 2-decimal payloads like
        // "51.88" confuse some MQTT clients and display as {}.
        messages.Add((topic, value.Value.ToString("0.#", CultureInfo.InvariantCulture)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            // Graceful goodbye: a clean disconnect does NOT fire the LWT,
            // so mark ourselves offline explicitly first.
            try { await PublishAvailabilityAsync(online: false, CancellationToken.None); } catch { }
            try
            {
                await _client.DisconnectAsync(
                    _factory.CreateClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
                _log("MQTT disconnected.");
            }
            catch { }
        }
        _client.Dispose();
    }
}
