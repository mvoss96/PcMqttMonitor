using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MQTTnet;
using MQTTnet.Protocol;

// Publishes metrics to the configured MQTT broker.
static class MqttPublisher
{
    public static async Task PublishAsync(
        IMqttClient mqttClient,
        MqttClientOptions mqttOptions,
        string topicRoot,
        string host,
        MqttMetrics metrics,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(mqttClient, mqttOptions, cancellationToken);

        var baseTopic = $"{topicRoot}/{host}";
        var messages = new List<(string topic, string payload)>
        {
            // Full JSON snapshot on the base topic.
            (baseTopic, JsonSerializer.Serialize(metrics, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }))
        };

        // Individual subtopics — plain scalar values, easy to consume in HA etc.
        if (metrics.Cpu != null)
        {
            var c = metrics.Cpu;
            AddIfSet(messages, baseTopic, "cpu/load",     c.Load);
            AddIfSet(messages, baseTopic, "cpu/temp",     c.TempC);
            AddIfSet(messages, baseTopic, "cpu/power",    c.PackagePowerW);
            AddIfSet(messages, baseTopic, "cpu/voltage",  c.CoreVoltageV);
        }

        if (metrics.Gpu != null)
        {
            var g = metrics.Gpu;
            AddIfSet(messages, baseTopic, "gpu/load",       g.Load);
            AddIfSet(messages, baseTopic, "gpu/temp",       g.TempC);
            AddIfSet(messages, baseTopic, "gpu/power",      g.BoardPowerW);
            AddIfSet(messages, baseTopic, "gpu/fan",        g.FanRpm);
            AddIfSet(messages, baseTopic, "gpu/vram_used",  g.MemoryUsedMb);
            AddIfSet(messages, baseTopic, "gpu/vram_total", g.MemoryTotalMb);
        }

        if (metrics.Ram != null)
        {
            var r = metrics.Ram;
            AddIfSet(messages, baseTopic, "ram/load",  r.Load);
            AddIfSet(messages, baseTopic, "ram/used",  r.UsedGb);
            AddIfSet(messages, baseTopic, "ram/total", r.TotalGb);
        }

        if (metrics.Drives != null)
        {
            for (int i = 0; i < metrics.Drives.Count; i++)
            {
                var d = metrics.Drives[i];
                var prefix = $"drives/{i}";
                AddIfSet(messages, baseTopic, $"{prefix}/name",    d.Name);
                AddIfSet(messages, baseTopic, $"{prefix}/used",    d.UsedGb);
                AddIfSet(messages, baseTopic, $"{prefix}/free",    d.FreeGb);
                AddIfSet(messages, baseTopic, $"{prefix}/total",   d.TotalGb);
                AddIfSet(messages, baseTopic, $"{prefix}/percent", d.UsedPercent);
            }
        }

        foreach (var (topic, payload) in messages)
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

            await mqttClient.PublishAsync(message, cancellationToken);
        }
    }

    // Typed overloads — no boxing, no ambiguity about how values get formatted.
    static void AddIfSet(List<(string, string)> messages, string baseTopic, string subTopic, int? value)
    {
        if (value == null) return;
        messages.Add(($"{baseTopic}/{subTopic}", value.Value.ToString(CultureInfo.InvariantCulture)));
    }

    static void AddIfSet(List<(string, string)> messages, string baseTopic, string subTopic, float? value)
    {
        if (value == null) return;
        messages.Add(($"{baseTopic}/{subTopic}", value.Value.ToString("0.##", CultureInfo.InvariantCulture)));
    }

    static void AddIfSet(List<(string, string)> messages, string baseTopic, string subTopic, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        messages.Add(($"{baseTopic}/{subTopic}", value));
    }

    public static async Task EnsureConnectedAsync(
        IMqttClient mqttClient,
        MqttClientOptions mqttOptions,
        CancellationToken cancellationToken)
    {
        if (mqttClient.IsConnected) return;

        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await mqttClient.ConnectAsync(mqttOptions, cancellationToken);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
        }
    }
}
