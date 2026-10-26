using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

// Dev entry point for the WPF prototype:
//   PcMqttMonitor.exe --wpf [--lang en|de] [--theme system|light|dark] [--shot <dir>]
// Opens the WPF window with the demo config (saved to %TEMP%, never the real
// config.json) and demo metrics, one new snapshot per second. --shot captures
// a series of states from the screen, so the Mica backdrop is included, and exits.
static class WpfPreview
{
    public static bool TryRun(string[] args)
    {
        if (!args.Contains("--wpf")) return false;

        string Arg(string name)
        {
            int k = Array.IndexOf(args, name);
            return k >= 0 && k + 1 < args.Length ? args[k + 1] : "";
        }

        L.Init(Arg("--lang") is { Length: > 0 } lang ? lang : "system");
        var config = DemoMode.DemoConfig();
        if (Arg("--theme") is { Length: > 0 } theme) config.General.Theme = theme;
        var configPath = Path.Combine(Path.GetTempPath(), "PcMqttMonitor-wpf-config.json");

        // Theme first: the shared styles build on the Fluent resources.
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        WpfMainWindow.ApplyTheme(config.General.Theme);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PcMqttMonitor;component/Wpf/Styles.xaml"),
        });

        var window = new WpfMainWindow(config, () => ConfigLoader.Save(configPath, config));
        window.SetSinkStatus("MQTT", StatusKind.Good, string.Format(L.T.StatusConnected, $"{config.Mqtt.Host}:{config.Mqtt.Port}"));
        int t = 0;
        for (; t < 60; t++) window.SetLatestMetrics(DemoMode.Snapshot(t));
        var feed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        feed.Tick += (_, _) => window.SetLatestMetrics(DemoMode.Snapshot(t++));
        feed.Start();

        string shotDir = Arg("--shot");
        if (shotDir.Length > 0)
        {
            Directory.CreateDirectory(shotDir);
            window.Topmost = true;
            window.ContentRendered += async (_, _) =>
            {
                string tag = $"{config.General.Theme}-{Arg("--lang")}";
                async Task Shot(string name)
                {
                    await Task.Delay(600);
                    Capture(window, Path.Combine(shotDir, $"wpf-{name}-{tag}.png"));
                }
                await Shot("dashboard");
                window.SelectPage(1);
                await Shot("outputs");
                window.DemoMarkDirty();
                await Shot("outputs-dirty");
                window.DemoDiscard();
                window.DemoOutputs.DemoExpandAll();
                window.DemoOutputs.DemoSetText("udp.host", "192.168.2.411");
                await Shot("outputs-invalid");
                window.SelectPage(3);
                await Shot("settings");
                window.DemoMarkDirty();
                await Shot("settings-dirty");
                window.SelectPage(0);
                await Shot("dashboard-dirty");
                window.Close();
            };
        }
        app.Run(window);
        return true;
    }

    // Copy the window's on-screen pixels (frame bounds without the shadow).
    static void Capture(Window w, string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT r, Marshal.SizeOf<RECT>());
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
