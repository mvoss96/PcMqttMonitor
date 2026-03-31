using MQTTnet;

class Program
{
    static async Task Main()
    {
        // Resolve config.json relative to the exe, not the working directory.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ConfigLoader.Load(configPath);
        LogDebug(config.DebugEnabled, "Config loaded.");

        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();

        var mqttOptions = mqttFactory.CreateClientOptionsBuilder()
            .WithTcpServer(config.BrokerHost, config.BrokerPort)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{Environment.MachineName.ToLowerInvariant()}")
            .Build();
        LogDebug(config.DebugEnabled, "MQTT options built.");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        using var sensors = new SensorService(
            config.Sensors,
            config.DebugEnabled ? message => LogDebug(true, message) : null);
        sensors.Open();

        try
        {
            await MqttPublisher.EnsureConnectedAsync(mqttClient, mqttOptions, shutdown.Token);
            LogDebug(config.DebugEnabled, "MQTT connected.");

            while (!shutdown.IsCancellationRequested)
            {
                try
                {
                    var snapshot = sensors.ReadSnapshot();
                    if (!string.IsNullOrWhiteSpace(snapshot.Summary))
                    {
                        Console.WriteLine(snapshot.Summary);
                    }

                    await MqttPublisher.PublishAsync(
                        mqttClient,
                        mqttOptions,
                        config.Topic,
                        snapshot.Metrics,
                        shutdown.Token);

                    LogDebug(config.DebugEnabled, "MQTT publish complete.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Log the error but keep running — transient broker issues should not crash the app.
                    Console.WriteLine($"[error] {DateTime.Now:HH:mm:ss} {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(config.PublishIntervalSeconds), shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
            LogDebug(config.DebugEnabled, "Shutdown requested.");
        }
        finally
        {
            if (mqttClient.IsConnected)
            {
                var disconnectOptions = mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                await mqttClient.DisconnectAsync(disconnectOptions, CancellationToken.None);
                LogDebug(config.DebugEnabled, "MQTT disconnected.");
            }
        }
    }

    static void LogDebug(bool enabled, string message)
    {
        if (!enabled)
        {
            return;
        }

        Console.WriteLine($"[debug] {DateTime.Now:HH:mm:ss} {message}");
    }
}
