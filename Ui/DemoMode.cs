using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Hidden dev mode for CI-generated screenshots:
//   PcMqttMonitor.exe --screenshots <dir> [--lang en|de] [--theme dark|light]
// Renders every page with deterministic demo data (no sensors, no sinks, no
// tray) and writes one PNG per page, then exits. The screenshots workflow uses
// this so the README images never go stale — real hardware values would be
// empty on a CI VM.
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
        Theme.Init();

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

    // GetWindowRect on purpose, NOT DwmGetWindowAttribute: this process is
    // DPI-unaware, so CopyFromScreen works in virtualized coordinates — and
    // GetWindowRect is virtualized to match, while the DWM API returns
    // physical pixels and would capture the wrong screen region.
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    static void Capture(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        // Win11 windows carry 7px invisible borders on the left/right/bottom
        // (none on top) — without the insets the capture shows desktop slivers.
        const int inset = 7;
        int w = r.Right - r.Left - 2 * inset, h = r.Bottom - r.Top - inset;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left + inset, r.Top, 0, 0, bmp.Size);
        bmp.Save(path, ImageFormat.Png);
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
