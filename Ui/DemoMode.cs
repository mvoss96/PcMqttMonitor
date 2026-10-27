using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Hidden dev modes, both with deterministic demo data and without sensors,
// sinks or tray:
//   PcMqttMonitor.exe --demo [--lang en|de] [--theme system|light|dark]
//     Opens the window with demo metrics (one new snapshot per second).
//   PcMqttMonitor.exe --screenshots <dir> [--lang] [--theme] [--scale N] [--scenarios]
//     Writes one PNG per page (README images, CI) and exits. --scale 2
//     renders at twice the size; --scenarios adds extra UI states.
// Saving writes to a throwaway config in %TEMP%, never the real config.json.
static class DemoMode
{
    // True in both modes: Save must not register autostart or restart the app.
    public static bool Active { get; private set; }

    public static bool TryRun(string[] args, Application app)
    {
        bool demo = args.Contains("--demo");
        int idx = Array.IndexOf(args, "--screenshots");
        string? dir = idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
        if (!demo && dir == null) return false;
        Active = true;

        string Arg(string name)
        {
            int k = Array.IndexOf(args, name);
            return k >= 0 && k + 1 < args.Length ? args[k + 1] : "";
        }

        // Screenshots default to en/dark for identical output everywhere.
        L.Init(Arg("--lang") is { Length: > 0 } lang ? lang : dir != null ? "en" : "system");
        var config = DemoConfig();
        config.General.Theme = Arg("--theme") is { Length: > 0 } theme ? theme : dir != null ? "dark" : "system";
        MainWindow.ApplyTheme(config.General.Theme);

        // Fan channels normally come from hardware detection.
        SensorService.SeedDetectedFans(
        [
            new SensorService.DetectedFan("fan_1", "Fan #1", 1042, false),
            new SensorService.DetectedFan("fan_2", "Fan #2", 688, false),
            new SensorService.DetectedFan("fan_3", "Fan #3", 0, false),
            new SensorService.DetectedFan("fan_4", "Fan #4", 0, false),
            new SensorService.DetectedFan("gpu_fan_1", "GPU Fan 1", 0, true),
            new SensorService.DetectedFan("gpu_fan_2", "GPU Fan 2", 0, true),
        ]);

        var window = new MainWindow(Path.Combine(Path.GetTempPath(), "PcMqttMonitor-demo-config.json"), config)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.SetSinkStatus("MQTT", StatusKind.Good, string.Format(L.T.StatusConnected, $"{config.Mqtt.Host}:{config.Mqtt.Port}"));
        int t = 0;
        for (; t < 60; t++) window.DemoFeed(Snapshot(t));   // a full minute of sparkline

        if (dir == null)
        {
            var feed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            feed.Tick += (_, _) => window.SetLatestMetrics(Snapshot(t++));
            feed.Start();
            // Closing hides the window; in demo mode that ends the app.
            window.IsVisibleChanged += (_, _) => { if (!window.IsVisible) app.Shutdown(); };
            window.Show();
            app.Run();
            return true;
        }

        float scale = float.TryParse(Arg("--scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            && s is >= 1f and <= 4f ? s : 1f;
        Directory.CreateDirectory(dir);
        window.ContentRendered += async (_, _) =>
        {
            var pages = new[] { "dashboard", "outputs", "sensors", "settings", "about" };
            for (int p = 0; p < pages.Length; p++)
            {
                window.SelectPage(p);
                await Task.Delay(500);   // layout, and the About page's update check
                Capture(window, Path.Combine(dir, pages[p] + ".png"), scale);
            }
            if (args.Contains("--scenarios"))
                await RunScenarios(window, dir, scale);
            window.CloseForExit();
            app.Shutdown();
        };
        window.Show();
        app.Run();
        return true;
    }

    // ── scenarios ─────────────────────────────────────────────────────────────

    // --scenarios: extra states for checking UI changes by eye, written as
    // scenario-*.png next to the page shots.
    static async Task RunScenarios(MainWindow w, string dir, float scale)
    {
        var outputs = w.DemoOutputs;
        string[] sinks = ["MQTT", "UDP", "TCP", "Serial"];

        void OpenOnly(string sink)
        {
            foreach (var s in sinks) outputs.DemoCard(s).IsExpanded = s == sink;
        }

        async Task Shot(int page, string name)
        {
            w.SelectPage(page);
            await Task.Delay(400);
            Capture(w, Path.Combine(dir, $"scenario-{name}.png"), scale);
        }

        // Invalid input: red field, red card status, Save disabled.
        OpenOnly("UDP");
        outputs.DemoCard("UDP").Enabled.IsChecked = true;
        outputs.DemoSetText("udp.host", "192.168.2.255eeee");
        await Shot(1, "invalid-host");
        w.DemoRevert();

        OpenOnly("TCP");
        outputs.DemoCard("TCP").Enabled.IsChecked = true;
        outputs.DemoSetText("tcp.port", "9871111111");
        await Shot(1, "invalid-port");
        w.DemoRevert();

        // Save footer: edit, save (footer hides), edit again.
        OpenOnly("Serial");
        outputs.DemoSetText("serial.port", "COM8");
        w.DemoSave();
        await Task.Delay(300);
        outputs.DemoSetText("serial.port", "COM9");
        await Shot(1, "footer-after-save");
        w.DemoRevert();

        // What running sinks report: errors in red, the TCP client count.
        OpenOnly("");
        outputs.DemoSetText("udp.host", "gibtsnicht.local");
        foreach (var s in sinks) outputs.DemoCard(s).Enabled.IsChecked = true;
        w.SetSinkStatus("UDP", StatusKind.Error, string.Format(L.T.OutNotFound, "gibtsnicht.local"));
        w.SetSinkStatus("TCP", StatusKind.Good,
            $"{string.Format(L.T.OutListeningOn, 5556)} · {string.Format(L.T.OutConnectedCount, 2)}");
        w.SetSinkStatus("Serial", StatusKind.Error, string.Format(L.T.OutNotFound, "COM9"));
        await Shot(1, "sink-status");
        w.DemoRevert();

        // Pause notice on the dashboard, update notice in title and nav.
        w.SetPauseState(true);
        w.SetUpdateAvailable(new Version(9, 9, 9));
        await Shot(0, "paused-update");
        w.SetPauseState(false);

        // The tray path: paused while the window is hidden, then opened.
        w.Hide();
        w.SetPauseState(true);
        await Task.Delay(200);
        w.Show();
        await Shot(0, "paused-from-tray");
        w.SetPauseState(false);
    }

    // ── capture ───────────────────────────────────────────────────────────────

    const double CaptionH = 32;

    // Render the window into a PNG at the given scale: a drawn caption strip
    // (icon, title, close glyph) above the client area. Rendered from the
    // visual tree, so the result does not depend on screen size or DPI; the
    // Mica backdrop is replaced by the theme's solid base color.
    static void Capture(MainWindow w, string path, float scale)
    {
        var root = (FrameworkElement)w.Content;
        double width = root.ActualWidth, height = root.ActualHeight + CaptionH;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = w.TryFindResource("SolidBackgroundFillColorBaseBrush") as Brush ?? Brushes.Black;
            var fg = w.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));

            if (w.Icon != null)
                dc.DrawImage(w.Icon, new Rect(10, (CaptionH - 16) / 2, 16, 16));
            double dpi = VisualTreeHelper.GetDpi(w).PixelsPerDip;
            var title = new FormattedText(w.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(w.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, fg, dpi);
            dc.DrawText(title, new Point(34, (CaptionH - title.Height) / 2));
            var close = new FormattedText(char.ConvertFromUtf32(0xE8BB), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 10, fg, dpi);
            dc.DrawText(close, new Point(width - 23 - close.Width / 2, (CaptionH - close.Height) / 2));

            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, CaptionH, root.ActualWidth, root.ActualHeight));
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path);
        png.Save(file);
    }

    // ── demo data ─────────────────────────────────────────────────────────────

    internal static AppConfig DemoConfig() => new()
    {
        Mqtt = new MqttConfig
        {
            Enabled = true, Host = "192.168.1.10", Port = 1883,
            Username = "homeassistant", Password = "demo1234",
            TopicRoot = "pc", HaDiscoveryEnabled = true,
        },
        Sensors = new SensorConfig
        {
            FanChannels = new()
            {
                ["fan_1"] = true, ["fan_2"] = true, ["fan_3"] = false, ["fan_4"] = false,
                ["gpu_fan_1"] = true, ["gpu_fan_2"] = true,
            },
        },
    };

    // Deterministic pseudo-noise: reproducible screenshots, no Random.
    static float Wave(int t, float baseV, float amp, float period) =>
        baseV + amp * MathF.Sin(t / period) + (t * 31 % 7) - 3;

    // The sparklines must look alive: plain low-amplitude waves render as flat
    // lines on the 0–100 scale. CPU gets load bursts, the GPU a "game
    // launched" ramp in the second half, RAM a slow climb.
    static int CpuLoad(int t) =>
        Math.Clamp((int)(Wave(t, 34, 14, 4.5f) + (t is > 14 and < 22 ? 38 : 0) + (t is > 44 and < 50 ? 26 : 0)), 3, 98);

    static int GpuLoad(int t) =>
        Math.Clamp(t < 30 ? (int)Wave(t, 8, 5, 6f) : (int)Wave(t, 64, 12, 5f), 2, 99);

    static int RamLoad(int t) =>
        Math.Clamp((int)(46 + t * 0.35f + Wave(t, 0, 4, 7f)), 5, 95);

    internal static MetricsSnapshot Snapshot(int t) => new()
    {
        Host = "GAMING-PC",
        Cpu = new CpuMetrics
        {
            Name = "AMD Ryzen 7 9800X3D",
            Load = CpuLoad(t),
            TempC = Math.Clamp(Wave(t, 64, 4, 8f), 35, 95),
            PackagePowerW = Math.Clamp(Wave(t, 62, 14, 6f), 15, 170),
            CoreVoltageV = 1.284f,
        },
        Gpu = new GpuMetrics
        {
            Name = "NVIDIA GeForce RTX 5070",
            Load = GpuLoad(t),
            TempC = Math.Clamp(t < 30 ? Wave(t, 46, 2, 9f) : Wave(t, 61, 3, 9f), 30, 90),
            BoardPowerW = Math.Clamp(t < 30 ? Wave(t, 42, 6, 5f) : Wave(t, 168, 14, 5f), 10, 250),
            MemoryUsedMb = t < 30 ? 2980 : 8460,
            MemoryTotalMb = 12227,
        },
        Ram = new RamMetrics
        {
            Load = RamLoad(t),
            UsedGb = RamLoad(t) * 31.9f / 100f,
            TotalGb = 31.9f,
            Type = "DDR5",
            SpeedMtps = 6000,
        },
        Motherboard = new MotherboardMetrics { Name = "ASUS TUF GAMING B650-PLUS" },
        Drives =
        [
            new StorageMetrics { Id = "c", Name = "System (C:)", Type = "SSD", UsedGb = 781.4f, FreeGb = 170.0f, TotalGb = 951.4f, UsedPercent = 82 },
            new StorageMetrics { Id = "d", Name = "Games (D:)",  Type = "SSD", UsedGb = 934.6f, FreeGb = 1046.2f, TotalGb = 1980.8f, UsedPercent = 47 },
            new StorageMetrics { Id = "g", Name = "Data (G:)",   Type = "HDD", UsedGb = 240.5f, FreeGb = 161.5f, TotalGb = 402.0f, UsedPercent = 60 },
        ],
        Network =
        [
            new NetworkAdapterMetrics
            {
                Id = "ethernet",
                Name = "Ethernet", UploadKbps = 212.4f, DownloadKbps = 1843.9f,
                IpAddress = "192.168.1.42", Mac = "a4:5e:60:d2:4b:1c",
            },
        ],
        Fans =
        [
            new FanMetrics { Id = "fan_1", Name = "Fan #1", Rpm = 1042, Pwm = 42 },
            new FanMetrics { Id = "fan_2", Name = "Fan #2", Rpm = 688, Pwm = 34 },
            new FanMetrics { Id = "gpu_fan_1", Name = "GPU Fan 1", Rpm = t < 30 ? 0 : 1450, Pwm = t < 30 ? 0 : 55 },
            new FanMetrics { Id = "gpu_fan_2", Name = "GPU Fan 2", Rpm = t < 30 ? 0 : 1430, Pwm = t < 30 ? 0 : 55 },
        ],
        System = new SystemMetrics
        {
            UptimeSec = 2 * 86400 + 8 * 3600 + 40 * 60,
            IdleSec = 4,
            OsVersion = "Windows 11 Pro 24H2",
        },
    };
}
