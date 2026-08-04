using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Hidden dev mode for CI-generated screenshots:
//   PcMqttMonitor.exe --screenshots <dir> [--lang en|de] [--theme dark|light] [--scale N]
// Renders every page with deterministic demo data (no sensors, no sinks, no
// tray) and writes one PNG per page, then exits. The screenshots workflow uses
// this so the README images never go stale — real hardware values would be
// empty on a CI VM. --scale 2 renders everything at twice the size for
// high-res README images, independent of the machine's actual DPI.
static class DemoMode
{
    public static bool TryRun(string[] args, string configPath)
    {
        int idx = Array.IndexOf(args, "--screenshots");
        if (idx < 0 || idx + 1 >= args.Length) return false;
        string dir = args[idx + 1];

        string Arg(string name)
        {
            int k = Array.IndexOf(args, name);
            return k >= 0 && k + 1 < args.Length ? args[k + 1] : "";
        }

        L.Init(Arg("--lang") is { Length: > 0 } lang ? lang : "en");
#pragma warning disable WFO5001 // SetColorMode is marked experimental
        Application.SetColorMode(Arg("--theme").ToLowerInvariant() == "light"
            ? SystemColorMode.Classic
            : SystemColorMode.Dark);
#pragma warning restore WFO5001
        // Fixed scale 1 by default: identical output on every machine. The
        // font/layout pipeline runs entirely off Theme.Scale, so the override
        // works even on a 96-DPI CI runner.
        float scale = float.TryParse(Arg("--scale"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var s) && s is >= 1f and <= 4f ? s : 1f;
        Theme.Init(scale);

        var config = DemoConfig();
        var window = new MainWindow(configPath, config);
        window.SetConnectionStatus(true, $"{config.Mqtt.Host}:{config.Mqtt.Port}");

        // 60 samples so the sparklines show a full, plausible minute.
        for (int t = 0; t < 60; t++)
            window.DemoFeed(Snapshot(t));

        window.StartPosition = FormStartPosition.CenterScreen;
        window.TopMost = true;   // nothing may cover the window during capture

        Directory.CreateDirectory(dir);
        window.Shown += async (_, _) =>
        {
            var pages = new[] { "dashboard", "outputs", "sensors", "settings", "about" };
            for (int p = 0; p < pages.Length; p++)
            {
                window.DemoSelectPage(p);
                await Task.Delay(500);   // paint + native combos + About check settle
                Capture(window.Handle, Path.Combine(dir, pages[p] + ".png"));
            }
            Application.Exit();
        };
        Application.Run(window);
        return true;
    }

    // ── capture ───────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    // PW_RENDERFULLCONTENT: DWM-composed content, works even if the window is
    // partially covered or larger than the screen (a --scale 2 window exceeds
    // the CI runner's 1080p display; screen capture would clip it).
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    static void Capture(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        int fw = r.Right - r.Left, fh = r.Bottom - r.Top;
        using var full = new Bitmap(fw, fh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
            g.ReleaseHdc(hdc);
        }
        // Win11 windows carry invisible borders on the left/right/bottom (none
        // on top) — 7px at 96 DPI, scaling with the WINDOW's DPI (not with
        // --scale, which is pure in-window rendering). Without the insets the
        // capture has transparent slivers around the edges.
        int inset = (int)MathF.Round(7f * GetDpiForWindow(hwnd) / 96f);
        using var crop = full.Clone(
            new Rectangle(inset, 0, fw - 2 * inset, fh - inset), PixelFormat.Format32bppArgb);
        crop.Save(path, ImageFormat.Png);
    }

    // ── demo data ─────────────────────────────────────────────────────────────

    static AppConfig DemoConfig() => new()
    {
        Mqtt = new MqttConfig
        {
            Enabled = true, Host = "192.168.1.10", Port = 1883,
            Username = "homeassistant", Password = "demo1234",
            TopicRoot = "pc", HaDiscoveryEnabled = true,
        },
    };

    // Deterministic pseudo-noise — reproducible screenshots, no Random.
    static float Wave(int t, float baseV, float amp, float period) =>
        baseV + amp * MathF.Sin(t / period) + (t * 31 % 7) - 3;

    // The sparklines must LOOK alive: plain low-amplitude waves render as flat
    // lines on the 0–100 scale. CPU gets load bursts, the GPU a "game
    // launched" ramp in the second half, RAM a slow climb.
    static int CpuLoad(int t) =>
        Math.Clamp((int)(Wave(t, 34, 14, 4.5f) + (t is > 14 and < 22 ? 38 : 0) + (t is > 44 and < 50 ? 26 : 0)), 3, 98);

    static int GpuLoad(int t) =>
        Math.Clamp(t < 30 ? (int)Wave(t, 8, 5, 6f) : (int)Wave(t, 64, 12, 5f), 2, 99);

    static int RamLoad(int t) =>
        Math.Clamp((int)(46 + t * 0.35f + Wave(t, 0, 4, 7f)), 5, 95);

    static MetricsSnapshot Snapshot(int t) => new()
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
            FanRpm = t < 30 ? 0 : 1450,
            MemoryUsedMb = t < 30 ? 2980 : 8460,
            MemoryTotalMb = 12227,
        },
        Ram = new RamMetrics
        {
            Load = RamLoad(t),
            UsedGb = RamLoad(t) * 31.9f / 100f,
            TotalGb = 31.9f,
        },
        Motherboard = new MotherboardMetrics { Name = "ASUS TUF GAMING B650-PLUS" },
        Drives =
        [
            new StorageMetrics { Name = "System (C:)", UsedGb = 781.4f, FreeGb = 170.0f, TotalGb = 951.4f, UsedPercent = 82 },
            new StorageMetrics { Name = "Games (D:)",  UsedGb = 934.6f, FreeGb = 1046.2f, TotalGb = 1980.8f, UsedPercent = 47 },
            new StorageMetrics { Name = "Data (G:)",   UsedGb = 240.5f, FreeGb = 161.5f, TotalGb = 402.0f, UsedPercent = 60 },
        ],
        Network =
        [
            new NetworkAdapterMetrics
            {
                Name = "Ethernet", UploadKbps = 212.4f, DownloadKbps = 1843.9f,
                IpAddress = "192.168.1.42", Mac = "a4:5e:60:d2:4b:1c",
            },
        ],
        System = new SystemMetrics
        {
            UptimeSec = 2 * 86400 + 8 * 3600 + 40 * 60,
            OsVersion = "Windows 11 Pro 24H2",
        },
    };
}
