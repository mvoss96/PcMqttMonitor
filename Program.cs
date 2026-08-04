using System.Runtime.InteropServices;
using System.Windows.Forms;

class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        // Pass --console to see log output in the terminal that launched the app.
        // Without it no console is allocated (WinExe) so output is silenced.
        bool consoleAttached = args.Contains("--console");
        if (consoleAttached)
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

        // Config lives next to the exe — Program Files when installed (works because
        // the app always runs elevated), or bin\Debug during development. Survives
        // upgrades because the installer only replaces the exe, never config.json.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ConfigLoader.Load(configPath);

        Log($"=== PC MQTT Monitor v{UpdateChecker.CurrentVersion.ToString(3)} starting ===");
        Log($"MQTT:     {(config.Mqtt.Enabled && !string.IsNullOrWhiteSpace(config.Mqtt.Host) ? $"{config.Mqtt.Host}:{config.Mqtt.Port} (topic root '{config.Mqtt.TopicRoot}')" : "disabled")}");
        Log($"UDP:      {(config.Udp.Enabled ? $"{config.Udp.Host}:{config.Udp.Port}" : "disabled")}");
        Log($"TCP:      {(config.Tcp.Enabled ? $"listening on {config.Tcp.ListenPort}" : "disabled")}");
        Log($"Interval: {config.General.PublishIntervalSeconds}s");

        using var shutdown = new CancellationTokenSource();
        var tray = new TrayApp(shutdown, configPath, config);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
            Application.Exit();
        };

        // Run the sensor + publish loop on a background thread so the UI stays responsive.
        var publishTask = Task.Run(async () =>
        {
            try { await RunPublishLoopAsync(config, tray, consoleAttached, shutdown.Token); }
            catch (Exception ex) { Log($"[fatal] Background task crashed: {ex}"); }
        });

        // Daily update check against GitHub releases (fire-and-forget; notify-only).
        _ = Task.Run(() => RunUpdateCheckLoopAsync(config, tray, shutdown.Token));

        // Blocks here until the user clicks Exit in the tray (or Ctrl+C).
        Application.Run(tray);

        // Tray was closed — cancel the loop and wait for it to finish cleanly.
        shutdown.Cancel();
        try { publishTask.Wait(); } catch { }
    }

    // Builds the active sinks from config. A sink whose constructor throws
    // (bad MQTT options, TCP port taken) is logged and skipped — the others run.
    static List<IMetricsSink> CreateSinks(AppConfig config, TrayApp tray)
    {
        var sinks = new List<IMetricsSink>();

        if (config.Mqtt.Enabled && !string.IsNullOrWhiteSpace(config.Mqtt.Host))
        {
            try { sinks.Add(new MqttSink(config.Mqtt, Log, tray.SetStatus, tray.SetConnectionStatus)); }
            catch (Exception ex) { Log($"[error] Invalid MQTT config: {ex.Message}"); }
        }

        if (config.Udp.Enabled && !string.IsNullOrWhiteSpace(config.Udp.Host))
        {
            try { sinks.Add(new UdpSink(config.Udp)); }
            catch (Exception ex) { Log($"[error] UDP sink: {ex.Message}"); }
        }

        if (config.Tcp.Enabled)
        {
            try { sinks.Add(new TcpSink(config.Tcp, Log)); }
            catch (Exception ex) { Log($"[error] TCP sink: {ex.Message}"); }
        }

        return sinks;
    }

    static async Task RunPublishLoopAsync(AppConfig config, TrayApp tray, bool consoleAttached, CancellationToken cancellationToken)
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
            message => { if (config.General.DebugEnabled) LogDebug(message); },
            buildSummary: consoleAttached);
        sensors.Open();
        Log("Sensors ready.");

        var sinks = CreateSinks(config, tray);
        if (sinks.Count == 0)
        {
            Log("No outputs configured — sensor-only mode.");
            tray.SetStatus("No outputs configured — sensors only");
        }
        else if (!sinks.Any(s => s.Name == "MQTT"))
        {
            // MqttSink maintains the tray status itself; without it, say once what runs.
            tray.SetStatus("Publishing to " + string.Join(" + ", sinks.Select(s => s.Name)));
        }

        bool wasPaused = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Always read sensors and update the UI — regardless of sink status.
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
                    snapshot = new SensorSnapshot("", new MetricsSnapshot { Host = Environment.MachineName });
                }

                bool paused = tray.IsPaused;

                if (paused != wasPaused)
                {
                    foreach (var sink in sinks)
                    {
                        try { await sink.SetOnlineAsync(!paused, cancellationToken); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Log($"[error] {sink.Name} availability: {ex.Message}"); }
                    }
                    wasPaused = paused;
                }

                if (!paused)
                {
                    foreach (var sink in sinks)
                    {
                        try { await sink.PublishAsync(snapshot.Metrics, cancellationToken); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Log($"[error] {sink.Name} publish: {ex.Message}"); }
                    }
                }
                else
                {
                    tray.SetStatus("Paused");
                }

                // Poll more frequently while paused so resume feels instant.
                var delay = tray.IsPaused
                    ? TimeSpan.FromSeconds(1)
                    : TimeSpan.FromSeconds(config.General.PublishIntervalSeconds);
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
            foreach (var sink in sinks)
            {
                try { await sink.DisposeAsync(); } catch { }
            }
        }
    }

    // Checks GitHub for a newer release: once shortly after startup, then daily.
    // The config flag is re-read every cycle so the Settings checkbox applies
    // without a restart. Failures (offline, rate limit) are logged and retried
    // the next day.
    static async Task RunUpdateCheckLoopAsync(AppConfig config, TrayApp tray, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMinutes(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromHours(24);
                if (!config.General.UpdateCheckEnabled) continue;

                try
                {
                    var newer = await UpdateChecker.CheckAsync(cancellationToken);
                    if (newer != null)
                    {
                        Log($"Update available: v{newer} (running v{UpdateChecker.CurrentVersion})");
                        tray.NotifyUpdateAvailable(newer);
                    }
                    else
                    {
                        LogDebug($"Update check: v{UpdateChecker.CurrentVersion} is up to date.");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log($"[update] check failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
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
