using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    // Set by the UI after saving changed output settings: the publish loop
    // disposes all sinks and rebuilds them from the (shared, already mutated)
    // config on its next cycle, without an app restart.
    static volatile bool _sinksReloadRequested;
    public static void RequestSinksReload() => _sinksReloadRequested = true;

    // Start a new instance and end this one (language change). The new
    // instance waits until this one has shut down its sinks and released
    // the single-instance lock.
    const string RestartedArg = "--restarted";

    public static void Restart()
    {
        try { System.Diagnostics.Process.Start(Environment.ProcessPath!, RestartedArg); }
        catch (Exception ex) { Log($"[error] restart failed: {ex.Message}"); return; }
        Application.Current.Shutdown();
    }

    [STAThread]
    static void Main(string[] args)
    {
        // Called by the installer: register the autostart task and exit. Keeps
        // the task definition in one place (AutoStart.cs).
        if (args.Contains("--register-autostart"))
            Environment.Exit(AutoStart.Apply(true) ? 0 : 1);

        // Pass --console to see log output in the terminal that launched the app.
        // Without it no console is allocated (WinExe) so output is silenced.
        bool consoleAttached = args.Contains("--console");
        if (consoleAttached)
            AttachConsole(-1); // -1 = attach to parent process' console
        else
            Console.SetOut(TextWriter.Null);

        // Managed crashes reach this handler and are logged before Windows
        // restarts the process. Native driver faults may bypass this handler.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var msg = e.ExceptionObject.ToString() ?? "";
            WriteCrashLog(msg);

            if (IsHardwareDriverCrash(msg))
                return;

            MessageBox.Show(
                $"{msg}\n\n{L.T.CrashDetails}\n{CrashLogPath}",
                L.T.CrashTitleFatal, MessageBoxButton.OK, MessageBoxImage.Error);
        };

        // Config lives next to the exe — Program Files when installed (works because
        // the app always runs elevated), or bin\Release during development. Survives
        // upgrades because the installer only replaces the exe, never config.json.
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = ConfigLoader.Load(configPath);

        // Language before any UI is built; changing it restarts the app.
        L.Init(config.General.Language);

        // The WPF application: Fluent theme first, the app styles build on it.
        // The app runs until Exit in the tray menu, not until a window closes.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainWindow.ApplyTheme(config.General.Theme);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PcMqttMonitor;component/Ui/Styles.xaml"),
        });
        // An exception in a UI event handler: log it, tell the user, keep running.
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog(e.Exception.ToString());
            MessageBox.Show(
                $"{e.Exception.Message}\n\n{L.T.CrashDetails}\n{CrashLogPath}",
                L.T.CrashTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        // Hidden dev mode: demo window or screenshots, no sensors and no sinks.
        if (DemoMode.TryRun(args, app))
            return;

        // One instance per installation. A second start only brings up the
        // running instance's window. After a language-change restart the new
        // instance waits until the old one has closed its sinks.
        using var instance = SingleInstance.Acquire(args.Contains(RestartedArg));
        if (instance == null)
            return;

        ApplicationRestart.RegisterForCrashes();

        Log($"=== PC MQTT Monitor v{UpdateChecker.CurrentVersion.ToString(3)} starting ===");
        Log($"MQTT:     {(config.Mqtt.Enabled && !string.IsNullOrWhiteSpace(config.Mqtt.Host) ? $"{config.Mqtt.Host}:{config.Mqtt.Port} (topic root '{config.Mqtt.TopicRoot}')" : "disabled")}");
        Log($"UDP:      {(config.Udp.Enabled ? $"{config.Udp.Host}:{config.Udp.Port}" : "disabled")}");
        Log($"TCP:      {(config.Tcp.Enabled ? $"listening on {config.Tcp.ListenPort}" : "disabled")}");
        Log($"Serial:   {(config.Serial.Enabled && !string.IsNullOrWhiteSpace(config.Serial.Port) ? $"{config.Serial.Port} @ {config.Serial.Baud} baud" : "disabled")}");
        Log($"Interval: {config.General.PublishIntervalSeconds}s");

        using var shutdown = new CancellationTokenSource();
        var tray = new TrayApp(shutdown, configPath, config);
        instance.OnShowRequested(() => app.Dispatcher.BeginInvoke(tray.ShowWindow));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
            app.Dispatcher.BeginInvoke(() => app.Shutdown());
        };

        // Run the sensor + publish loop on a background thread so the UI stays responsive.
        var publishTask = Task.Run(async () =>
        {
            try { await RunPublishLoopAsync(config, configPath, tray, consoleAttached, shutdown.Token); }
            catch (Exception ex) { Log($"[fatal] Background task crashed: {ex}"); }
        });

        // Logoff/shutdown kills the process without ending the app loop.
        // Stop the loop here so the sinks are disposed (MQTT publishes "offline")
        // instead of HA waiting for the LWT after the keep-alive timeout.
        // Blocking the handler keeps Windows waiting; the timeout bounds it.
        SystemEvents.SessionEnded += (_, _) =>
        {
            try { shutdown.Cancel(); publishTask.Wait(TimeSpan.FromSeconds(3)); } catch { }
        };

        // Daily update check against GitHub releases (fire-and-forget; notify-only).
        _ = Task.Run(() => RunUpdateCheckLoopAsync(config, tray, shutdown.Token));

        // Blocks here until the user clicks Exit in the tray (or Ctrl+C).
        app.Run();
        tray.Dispose();

        // Tray was closed — cancel the loop and wait for it to finish cleanly.
        shutdown.Cancel();
        try { publishTask.Wait(); } catch { }
        instance.Release();   // lets a restarting instance take over
    }

    // Builds the active sinks from config. A sink whose constructor throws
    // (e.g. bad MQTT options) is logged and skipped — the others run.
    static List<IMetricsSink> CreateSinks(AppConfig config, TrayApp tray)
    {
        var sinks = new List<IMetricsSink>();

        if (config.Mqtt.Enabled && !string.IsNullOrWhiteSpace(config.Mqtt.Host))
        {
            try { sinks.Add(new MqttSink(config.Mqtt, Log, tray.SetStatus, tray.SetSinkStatus, tray.ShowNotification)); }
            catch (Exception ex) { Log($"[error] Invalid MQTT config: {ex.Message}"); }
        }

        if (config.Udp.Enabled && !string.IsNullOrWhiteSpace(config.Udp.Host))
        {
            try { sinks.Add(new UdpSink(config.Udp, Log, tray.SetSinkStatus)); }
            catch (Exception ex) { Log($"[error] UDP sink: {ex.Message}"); }
        }

        if (config.Tcp.Enabled)
        {
            try { sinks.Add(new TcpSink(config.Tcp, Log, tray.SetSinkStatus)); }
            catch (Exception ex) { Log($"[error] TCP sink: {ex.Message}"); }
        }

        if (config.Serial.Enabled && !string.IsNullOrWhiteSpace(config.Serial.Port))
        {
            try { sinks.Add(new SerialSink(config.Serial, Log, tray.SetSinkStatus)); }
            catch (Exception ex) { Log($"[error] Serial sink: {ex.Message}"); }
        }

        return sinks;
    }

    static async Task RunPublishLoopAsync(AppConfig config, string configPath, TrayApp tray, bool consoleAttached, CancellationToken cancellationToken)
    {
        Log("Background task started.");

        // On a fresh boot, storage drivers may not be ready yet.
        // Wait until the system has been up for at least 30 seconds.
        var uptimeSec = Environment.TickCount64 / 1000.0;
        if (uptimeSec < 30)
        {
            var waitSec = (int)Math.Ceiling(30 - uptimeSec);
            Log($"System just booted — waiting {waitSec}s for drivers to settle...");
            tray.SetStatus(string.Format(L.T.StatusWaitingDrivers, waitSec));
            await Task.Delay(TimeSpan.FromSeconds(waitSec), cancellationToken);
        }

        Log("Opening sensors (this may take a few seconds)...");
        tray.SetStatus(L.T.StatusOpeningSensors);
        using var sensors = new SensorService(
            config.Sensors,
            message => { if (config.General.DebugEnabled) LogDebug(message); },
            buildSummary: consoleAttached);
        sensors.Open();
        Log("Sensors ready.");

        // Newly detected fan channels got their enabled-default assigned in
        // Open — persist it right away so the choice is made exactly once
        // (first start), not re-rolled from whatever spins at every boot.
        if (sensors.NewFanChannelsDetected)
        {
            try { ConfigLoader.Save(configPath, config); Log("Fan channel defaults saved to config."); }
            catch (Exception ex) { Log($"[error] saving fan channel defaults: {ex.Message}"); }
        }

        void AnnounceSinks(List<IMetricsSink> active)
        {
            if (active.Count == 0)
            {
                Log("No outputs configured — sensor-only mode.");
                tray.SetStatus(L.T.StatusNoOutputs);
            }
            else if (!active.Any(s => s.Name == "MQTT"))
            {
                // MqttSink maintains the tray status itself; without it, say once what runs.
                tray.SetStatus(string.Format(L.T.StatusPublishingTo, string.Join(" + ", active.Select(s => s.Name))));
            }
        }

        var sinks = CreateSinks(config, tray);
        AnnounceSinks(sinks);

        bool wasPaused = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // The UI saved changed output settings — rebuild all sinks from
                // the updated config (live apply, no app restart).
                if (_sinksReloadRequested)
                {
                    _sinksReloadRequested = false;
                    Log("Output settings changed — reloading sinks...");
                    foreach (var sink in sinks)
                    {
                        try { await sink.DisposeAsync(); } catch { }
                    }
                    sinks = CreateSinks(config, tray);
                    AnnounceSinks(sinks);
                }

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
                    // Skip this cycle instead of publishing an empty snapshot: the
                    // MQTT sink would treat every HA entity as gone and delete it.
                    Log($"[error] sensor read: {ex.Message} — skipping this cycle.");
                    await Task.Delay(TimeSpan.FromSeconds(config.General.PublishIntervalSeconds), cancellationToken);
                    continue;
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
                    // Resuming: re-announce the sink status — while paused the
                    // status line says "Paused", and without an MQTT sink (which
                    // rewrites it on every publish) nothing else would clear it.
                    if (!paused)
                        AnnounceSinks(sinks);
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
                    tray.SetStatus(L.T.StatusPaused);
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

    // Suppress the fatal-error dialog for known driver crashes. Windows restarts
    // the registered process after the runtime terminates it.
    static bool IsHardwareDriverCrash(string msg) =>
        msg.Contains("NvApi",      StringComparison.OrdinalIgnoreCase) || // NVIDIA driver
        msg.Contains("NvidiaGpu",  StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("AmdGpu",     StringComparison.OrdinalIgnoreCase) || // AMD driver
        msg.Contains("IntelGpu",   StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("LibreHardwareMonitor.Hardware.Gpu")            ||  // generic GPU LHM crash
        msg.Contains("AccessViolationException");                         // native driver fault
}
