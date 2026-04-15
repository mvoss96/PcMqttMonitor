using System.Runtime.InteropServices;
using MQTTnet;
using System.Windows.Forms;

class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        // Pass --console to see log output in the terminal that launched the app.
        // Without it no console is allocated (WinExe) so output is silenced.
        if (args.Contains("--console"))
            AttachConsole(-1); // -1 = attach to parent process' console
        else
            Console.SetOut(TextWriter.Null);

        // Show a message box for any unhandled exception so crashes are never silent.
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.ToString(), "PC MQTT Monitor — Unhandled Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show(e.ExceptionObject.ToString(), "PC MQTT Monitor — Fatal Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Config always lives next to the exe — survives rebuilds (dotnet build never
        // deletes extra files), only lost on an explicit dotnet clean.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ConfigLoader.Load(configPath);

        Log($"Broker:   {config.BrokerHost}:{config.BrokerPort}");
        Log($"TopicRoot: {config.TopicRoot}");
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

    // Builds MQTT client options from the current config — called on connect and reconnect.
    static MQTTnet.MqttClientOptions BuildMqttOptions(MqttClientFactory factory, AppConfig config) =>
        factory.CreateClientOptionsBuilder()
            .WithTcpServer(config.BrokerHost, config.BrokerPort)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{Environment.MachineName.ToLowerInvariant()}")
            .Build();

    static async Task RunMqttLoopAsync(AppConfig config, TrayApp tray, CancellationToken cancellationToken)
    {
        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();
        var mqttOptions = BuildMqttOptions(mqttFactory, config);

        Log("Opening sensors (this may take a few seconds)...");
        tray.SetStatus("Opening sensors...");
        using var sensors = new SensorService(
            config.Sensors,
            message => { if (config.DebugEnabled) LogDebug(message); });
        sensors.Open();
        Log("Sensors ready.");

        try
        {
            // Connect — settings were validated before being saved, so this should succeed.
            // If it fails anyway (broker went away), keep retrying until cancelled.
            while (!mqttClient.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Log($"Connecting to {config.BrokerHost}:{config.BrokerPort}...");
                    tray.SetStatus($"Connecting to {config.BrokerHost}:{config.BrokerPort}...");
                    await MqttPublisher.EnsureConnectedAsync(mqttClient, mqttOptions, cancellationToken);
                    Log("MQTT connected.");
                    tray.SetStatus($"Connected — {config.BrokerHost}:{config.BrokerPort}");
                    tray.SetConnectionStatus(true, $"{config.BrokerHost}:{config.BrokerPort}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"[error] Connection failed: {ex.Message}");
                    tray.SetStatus("Connection failed — open Settings to fix MQTT broker");
                    tray.SetConnectionStatus(false, "");
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                }
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = sensors.ReadSnapshot();
                    if (!string.IsNullOrWhiteSpace(snapshot.Summary))
                        Console.WriteLine(snapshot.Summary);
                    tray.UpdateSnapshot(snapshot);  // always update UI and tooltip

                    if (!tray.IsPaused)
                    {
                        await MqttPublisher.PublishAsync(
                            mqttClient,
                            mqttOptions,
                            config.TopicRoot,
                            Environment.MachineName.ToLowerInvariant(),
                            snapshot.Metrics,
                            cancellationToken);

                        LogDebug("Publish complete.");
                        tray.SetStatus($"Connected — {config.BrokerHost}:{config.BrokerPort}");
                    }
                    else
                    {
                        tray.SetStatus("Paused");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"[error] {ex.Message}");
                    tray.SetStatus($"Error: {ex.Message}");
                    if (!mqttClient.IsConnected)
                        tray.SetConnectionStatus(false, "");
                }

                // Poll more frequently while paused so resume feels instant.
                var delay = tray.IsPaused
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(config.PublishIntervalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log("Shutting down.");
        }
        finally
        {
            tray.SetConnectionStatus(false, "");
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
