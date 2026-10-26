using System.Runtime.InteropServices;
using System.Windows;
using WpfApplication = System.Windows.Application;

// Dev entry point for the WPF prototype:
//   PcMqttMonitor.exe --wpf [--lang en|de] [--theme system|light|dark] [--shot <dir>]
// Opens the WPF window with the demo config (saved to %TEMP%, never the real
// config.json). --shot captures the window from the screen, so the Mica
// backdrop is included, and exits.
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

        var app = new WpfApplication { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new WpfMainWindow(config, () => ConfigLoader.Save(configPath, config));

        string shotDir = Arg("--shot");
        if (shotDir.Length > 0)
        {
            Directory.CreateDirectory(shotDir);
            window.Topmost = true;
            window.ContentRendered += async (_, _) =>
            {
                string suffix = $"settings-{config.General.Theme}";
                await Task.Delay(800);
                Capture(window, Path.Combine(shotDir, $"wpf-{suffix}.png"));
                window.DemoMarkDirty();
                await Task.Delay(400);
                Capture(window, Path.Combine(shotDir, $"wpf-{suffix}-dirty.png"));
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
