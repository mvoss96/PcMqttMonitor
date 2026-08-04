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

        // Borderless with an own caption bar: the OS would draw the real title
        // bar for the monitor's DPI, not for --scale — at scale 2 it stays half
        // height with tiny text. The drawn caption scales with everything else.
        window.FormBorderStyle = FormBorderStyle.None;
        window.Controls.Add(new CaptionBar { Text = window.Text });
        // Removing the border shrank the window to the former client area —
        // restore the design size so the pages keep their proportions.
        // A --scale window can be larger than the screen (CI runs on a small
        // display), which silently produced cut-off captures: WinForms clamps
        // Form.Size to the screen's MaxWindowTrackSize, and Windows enforces
        // the same limit from WM_GETMINMAXINFO. It takes BOTH to get past
        // that: MaximumSize makes the form report the large size as its max
        // track size, and raw SetWindowPos (in Shown, below) bypasses the
        // managed clamp.
        var size = new Size(Theme.S(500), Theme.S(700));
        window.MinimumSize = size;
        window.MaximumSize = size;

        window.StartPosition = FormStartPosition.CenterScreen;
        window.TopMost = true;   // nothing may cover the window during capture

        Directory.CreateDirectory(dir);
        window.Shown += async (_, _) =>
        {
            SetWindowPos(window.Handle, IntPtr.Zero, 0, 0, size.Width, size.Height,
                0x0002 | 0x0004 | 0x0010 /* NOMOVE | NOZORDER | NOACTIVATE */);
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

    // Raw SetWindowPos: not subject to the WinForms MaxWindowTrackSize clamp.
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    // PW_RENDERFULLCONTENT: DWM-composed content, works even if the window is
    // partially covered or larger than the screen (a --scale 2 window exceeds
    // the CI runner's 1080p display; screen capture would clip it).
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    // Borderless window — GetWindowRect is exactly the visible area, no
    // invisible Win11 frame insets to compensate.
    static void Capture(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    // Win11-style caption strip: app icon, title, close glyph — drawn at
    // Theme.Scale like the rest of the UI. Purely decorative (screenshots).
    sealed class CaptionBar : Control
    {
        public CaptionBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint, true);
            Dock = DockStyle.Top;
            Height = Theme.S(32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.WinBg);

            int size = Theme.S(16);
            using (var sized = new Icon(TrayApp.CreateIcon(), 32, 32))
            using (var bmp = sized.ToBitmap())
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, new Rectangle(Theme.S(10), (Height - size) / 2, size, size));
            }

            TextRenderer.DrawText(g, Text, Theme.Small,
                new Rectangle(Theme.S(34), 0, Width - Theme.S(80), Height), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // close glyph, centered in the standard ~46px-wide hit zone
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var pen = new Pen(Theme.Fg, Theme.SF(1f));
            float cx = Width - Theme.SF(23), cy = Height / 2f, r = Theme.SF(5);
            g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
            g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
        }
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
