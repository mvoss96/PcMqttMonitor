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

        Application.ThreadException += (_, e) =>
        {
            WriteCrashLog(e.Exception.ToString());
            MessageBox.Show(
                $"{e.Exception.Message}\n\nDetails gespeichert in:\n{CrashLogPath}",
                "PC MQTT Monitor — Fehler",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        // Crashes on LibreHardwareMonitor background threads (storage device-change events,
        // GPU driver updates, …) cannot be caught with try/catch — they arrive here.
        // For known hardware/driver crashes: log, wait briefly, then restart automatically.
        // For everything else: show a dialog with the crash log path.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject.ToString() ?? "";
            WriteCrashLog(msg);

            if (IsHardwareDriverCrash(msg))
            {
                Log("Hardware/driver crash detected — restarting in 10 s...");
                Task.Delay(10_000).Wait();
                Application.Restart();
                return;
            }

            MessageBox.Show(
                $"{msg}\n\nDetails gespeichert in:\n{CrashLogPath}",
                "PC MQTT Monitor — Kritischer Fehler",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Config lives next to the exe — which is %LocalAppData%\PcMqttMonitor\ when
        // installed, or bin\Debug\ during development. Survives upgrades because the
        // installer only replaces the exe, never config.json.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ConfigLoader.Load(configPath);

        Log($"=== PC MQTT Monitor v{typeof(Program).Assembly.GetName().Version?.ToString(3)} starting ===");
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
        var mqttTask = Task.Run(async () =>
        {
            try { await RunMqttLoopAsync(config, tray, shutdown.Token); }
            catch (Exception ex) { Log($"[fatal] Background task crashed: {ex}"); }
        });

        // Blocks here until the user clicks Exit in the tray (or Ctrl+C).
        Application.Run(tray);

        // Tray was closed — cancel the loop and wait for it to finish cleanly.
        shutdown.Cancel();
        try { mqttTask.Wait(); } catch { }
    }

    // Builds MQTT client options from the current config — called on connect and reconnect.
    static MQTTnet.MqttClientOptions BuildMqttOptions(MqttClientFactory factory, AppConfig config)
    {
        var builder = factory.CreateClientOptionsBuilder()
            .WithTcpServer(config.BrokerHost, config.BrokerPort)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{Environment.MachineName.ToLowerInvariant()}")
            // LWT: if the connection dies without a clean disconnect (crash, power
            // loss), the broker publishes "offline" on our behalf once the
            // keep-alive times out — subscribers always learn we are gone.
            .WithWillTopic(MqttPublisher.AvailabilityTopic(config.TopicRoot, Environment.MachineName.ToLowerInvariant()))
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
        if (config.UseTls)
            builder = builder.WithTlsOptions(o => o.UseTls());
        return builder.Build();
    }

    static async Task RunMqttLoopAsync(AppConfig config, TrayApp tray, CancellationToken cancellationToken)
    {
        Log("Background task started.");

        // On a fresh boot, storage drivers may not be ready yet.
        // Wait until the system has been up for at least 30 seconds.
        var uptimeSec = Environment.TickCount64 / 1000.0;
        if (uptimeSec < 30)
        {
            var waitSec = (int)Math.Ceiling(30 - uptimeSec);
            Log($"System just booted — waiting {waitSec}s for drivers to settle...");
            tray.SetStatus($"Waiting for system drivers ({waitSec}s)...");
            await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken);
        }

        Log("Opening sensors (this may take a few seconds)...");
        tray.SetStatus("Opening sensors...");
        using var sensors = new SensorService(
            config.Sensors,
            message => { if (config.DebugEnabled) LogDebug(message); });
        sensors.Open();
        Log("Sensors ready.");

        // MQTT is optional — skip entirely if no broker is configured.
        bool hasBroker = !string.IsNullOrWhiteSpace(config.BrokerHost);
        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();
        MqttClientOptions? mqttOptions = null;
        if (hasBroker)
        {
            try { mqttOptions = BuildMqttOptions(mqttFactory, config); }
            catch (Exception ex)
            {
                Log($"[error] Invalid MQTT config: {ex.Message}");
                hasBroker = false;
            }
        }

        if (!hasBroker)
        {
            Log("No broker configured — sensor-only mode.");
            tray.SetStatus("No broker configured — sensors only");
        }

        var host = Environment.MachineName.ToLowerInvariant();
        bool wasPaused = false;
        // Discovery state: republish when the toggle or the advertised component
        // set changes; sync once after every (re)connect so the retained config
        // on the broker always matches the current setting.
        bool discoveryActive = false;
        string discoveryFingerprint = "";
        bool needsDiscoverySync = true;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Always read sensors and update the UI — regardless of MQTT status.
                SensorSnapshot snapshot;
                try
                {
                    snapshot = sensors.ReadSnapshot();
                    if (!string.IsNullOrWhiteSpace(snapshot.Summary))
                        Console.WriteLine(snapshot.Summary);
                    tray.UpdateSnapshot(snapshot);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log($"[error] sensor read: {ex.Message}");
                    snapshot = new SensorSnapshot("", new MqttMetrics { Host = Environment.MachineName });
                }

                bool paused = tray.IsPaused;

                if (hasBroker && mqttOptions != null)
                {
                    // Reflect pause transitions on the availability topic while the
                    // connection is still up, so HA shows the PC as unavailable.
                    if (paused != wasPaused && mqttClient.IsConnected)
                    {
                        try
                        {
                            await MqttPublisher.PublishAvailabilityAsync(
                                mqttClient, config.TopicRoot, host, online: !paused, cancellationToken);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Log($"[error] availability publish: {ex.Message}"); }
                    }
                    wasPaused = paused;
                }

                if (!paused && hasBroker && mqttOptions != null)
                {
                    // Connect/reconnect if needed — one attempt per cycle, non-blocking.
                    if (!mqttClient.IsConnected)
                    {
                        try
                        {
                            Log($"Connecting to {config.BrokerHost}:{config.BrokerPort}...");
                            tray.SetStatus($"Connecting to {config.BrokerHost}:{config.BrokerPort}...");
                            await mqttClient.ConnectAsync(mqttOptions, cancellationToken);
                            Log("MQTT connected.");
                            tray.SetConnectionStatus(true, $"{config.BrokerHost}:{config.BrokerPort}");
                            await MqttPublisher.PublishAvailabilityAsync(
                                mqttClient, config.TopicRoot, host, online: true, cancellationToken);
                            needsDiscoverySync = true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log($"[error] Connection failed: {ex.Message}");
                            tray.SetStatus("Disconnected — open Settings to configure MQTT broker");
                            tray.SetConnectionStatus(false, "");
                        }
                    }

                    if (mqttClient.IsConnected)
                    {
                        // Keep the HA discovery config on the broker in sync with the
                        // (live-editable) setting and the advertised component set.
                        try
                        {
                            bool want = config.HaDiscoveryEnabled;
                            var fingerprint = HaDiscovery.Fingerprint(snapshot.Metrics);
                            if (needsDiscoverySync || want != discoveryActive
                                || (want && fingerprint != discoveryFingerprint))
                            {
                                if (want)
                                {
                                    await HaDiscovery.PublishConfigAsync(
                                        mqttClient, config.TopicRoot, host, snapshot.Metrics,
                                        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                                        config.BrokerHost, config.BrokerPort,
                                        cancellationToken);
                                    Log("HA discovery config published.");
                                }
                                else
                                {
                                    await HaDiscovery.RemoveAsync(mqttClient, host, cancellationToken);
                                    if (discoveryActive) Log("HA discovery config removed.");
                                }
                                discoveryActive = want;
                                discoveryFingerprint = fingerprint;
                                needsDiscoverySync = false;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Log($"[error] discovery publish: {ex.Message}"); }

                        try
                        {
                            await MqttPublisher.PublishAsync(
                                mqttClient,
                                config.TopicRoot,
                                host,
                                snapshot.Metrics,
                                cancellationToken);
                            LogDebug("Publish complete.");
                            tray.SetStatus($"Connected — {config.BrokerHost}:{config.BrokerPort}");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Log($"[error] publish: {ex.Message}");
                            tray.SetStatus($"Error: {ex.Message}");
                            if (!mqttClient.IsConnected)
                                tray.SetConnectionStatus(false, "");
                        }
                    }
                }
                else if (paused)
                {
                    tray.SetStatus("Paused");
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
                // Graceful goodbye: mark ourselves offline before disconnecting —
                // a clean disconnect does NOT fire the LWT, so we say it ourselves.
                try
                {
                    await MqttPublisher.PublishAvailabilityAsync(
                        mqttClient, config.TopicRoot, host, online: false, CancellationToken.None);
                }
                catch { }
                var disconnectOptions = mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                await mqttClient.DisconnectAsync(disconnectOptions, CancellationToken.None);
                Log("MQTT disconnected.");
            }
        }
    }

    static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        Console.WriteLine(line);
        AppLog.Write(message);
    }

    static void LogDebug(string message)
    {
        var line = $"[debug] {DateTime.Now:HH:mm:ss} {message}";
        Console.WriteLine(line);
        AppLog.Write($"[debug] {message}");
    }

    // ── Crash logging ─────────────────────────────────────────────────────────────

    static string CrashLogPath =>
        Path.Combine(AppContext.BaseDirectory, "crash.log");

    static void WriteCrashLog(string message)
    {
        try
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{message}\n\n";
            File.AppendAllText(CrashLogPath, entry);
        }
        catch { }
        AppLog.Write($"[crash] {message.Split('\n')[0]}"); // first line only — full detail in crash.log
    }

    // Hardware/driver crashes originate on LibreHardwareMonitor's own threads and
    // cannot be caught with try/catch in our code. Restarting is the right response:
    // the driver is usually ready again within seconds (update finished, device re-enumerated).
    static bool IsHardwareDriverCrash(string msg) =>
        msg.Contains("DiskInfoToolkit")                              ||  // storage at boot
        msg.Contains("StorageManager")                               ||  // storage device change
        msg.Contains("NvApi",      StringComparison.OrdinalIgnoreCase) || // NVIDIA driver
        msg.Contains("NvidiaGpu",  StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("AmdGpu",     StringComparison.OrdinalIgnoreCase) || // AMD driver
        msg.Contains("IntelGpu",   StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("LibreHardwareMonitor.Hardware.Gpu")            ||  // generic GPU LHM crash
        msg.Contains("AccessViolationException");                         // native driver fault
}
