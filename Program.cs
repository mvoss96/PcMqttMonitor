using MQTTnet;
using System.Windows.Forms;

class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var configPath = File.Exists("config.json")
            ? "config.json"
            : Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ConfigLoader.Load(configPath);

        Log($"Broker:   {config.BrokerHost}:{config.BrokerPort}");
        Log($"Topic:    {config.Topic}");
        Log($"Interval: {config.PublishIntervalSeconds}s");

        using var shutdown = new CancellationTokenSource();
        var tray = new TrayApp(shutdown, configPath, config);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
            Application.Exit();
        };

        // Run the MQTT + sensor loop on a background thread so the UI stays responsive.
        var mqttTask = Task.Run(() => RunMqttLoopAsync(config, tray, shutdown.Token));

        // Blocks here until the user clicks Exit in the tray (or Ctrl+C).
        Application.Run(tray);

        // Tray was closed — cancel the loop and wait for it to finish cleanly.
        shutdown.Cancel();
        try { mqttTask.Wait(); } catch { }
    }

    static async Task RunMqttLoopAsync(AppConfig config, TrayApp tray, CancellationToken cancellationToken)
    {
        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();

        var mqttOptions = mqttFactory.CreateClientOptionsBuilder()
            .WithTcpServer(config.BrokerHost, config.BrokerPort)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{Environment.MachineName.ToLowerInvariant()}")
            .Build();

        Log("Opening sensors (this may take a few seconds)...");
        tray.SetStatus("Opening sensors...");

        using var sensors = new SensorService(
            config.Sensors,
            config.DebugEnabled ? message => LogDebug(message) : null);
        sensors.Open();

        Log("Sensors ready.");

        try
        {
            Log($"Connecting to {config.BrokerHost}:{config.BrokerPort}...");
            tray.SetStatus($"Connecting to {config.BrokerHost}:{config.BrokerPort}...");

            await MqttPublisher.EnsureConnectedAsync(mqttClient, mqttOptions, cancellationToken);

            Log("MQTT connected.");
            tray.SetStatus($"Connected — {config.BrokerHost}:{config.BrokerPort}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = sensors.ReadSnapshot();
                    if (!string.IsNullOrWhiteSpace(snapshot.Summary))
                        Console.WriteLine(snapshot.Summary);
                    tray.UpdateSnapshot(snapshot);

                    await MqttPublisher.PublishAsync(
                        mqttClient,
                        mqttOptions,
                        config.Topic,
                        snapshot.Metrics,
                        cancellationToken);

                    LogDebug("Publish complete.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"[error] {ex.Message}");
                    tray.SetStatus($"Error: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(config.PublishIntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Shutting down.");
        }
        finally
        {
            if (mqttClient.IsConnected)
            {
                var disconnectOptions = mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                await mqttClient.DisconnectAsync(disconnectOptions, CancellationToken.None);
                Log("MQTT disconnected.");
            }
        }
    }

    static void Log(string message)
        => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

    static void LogDebug(string message)
        => Console.WriteLine($"[debug] {DateTime.Now:HH:mm:ss} {message}");
}
