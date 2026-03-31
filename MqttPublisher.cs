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
        string topic,
        MqttMetrics metrics,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(mqttClient, mqttOptions, cancellationToken);

        var payload = JsonSerializer.Serialize(metrics, new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await mqttClient.PublishAsync(message, cancellationToken);
    }

    public static async Task EnsureConnectedAsync(
        IMqttClient mqttClient,
        MqttClientOptions mqttOptions,
        CancellationToken cancellationToken)
    {
        if (mqttClient.IsConnected)
        {
            return;
        }

        // Retry up to 5 times with increasing delays (2s, 4s, 6s, 8s, then throw).
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
