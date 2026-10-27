using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// What the tray icon shows instead of the logo (GeneralConfig.TrayIcon) and
// how that icon is drawn.
static class TrayIcon
{
    public const string Logo = "logo", CpuLoad = "cpu_load", CpuTemp = "cpu_temp",
                        GpuLoad = "gpu_load", GpuTemp = "gpu_temp", RamLoad = "ram_load",
                        LoadBars = "load_bars";

    // Order of the Settings dropdown.
    public static readonly string[] Modes = [Logo, CpuLoad, CpuTemp, GpuLoad, GpuTemp, RamLoad, LoadBars];

    const float HotTempC = 80;   // same threshold as the dashboard's red

    // The number to show for a numeric mode: at most two digits (100 % is
    // shown as 99), Hot for temperatures from 80 °C. Null when the mode is
    // not numeric or the value is not available.
    internal static (string Text, bool Hot)? Value(MetricsSnapshot m, string? mode)
    {
        float? v = mode switch
        {
            CpuLoad => m.Cpu?.Load,
            CpuTemp => m.Cpu?.TempC,
            GpuLoad => m.Gpu?.Load,
            GpuTemp => m.Gpu?.TempC,
            RamLoad => m.Ram?.Load,
            _ => null,
        };
        if (v == null) return null;
        int n = Math.Clamp((int)MathF.Round(v.Value), 0, 99);
        bool hot = mode is CpuTemp or GpuTemp && v.Value >= HotTempC;
        return (n.ToString(), hot);
    }

    // Square icon bitmap at the tray's icon size. Returns null when the mode
    // has nothing to show, so the caller keeps the logo.
    public static Bitmap? Render(MetricsSnapshot m, string? mode, int size)
    {
        bool light = TaskbarIsLight();
        if (mode == LoadBars)
        {
            if (m.Cpu?.Load == null && m.Gpu?.Load == null) return null;
            return Bars(size, m.Cpu?.Load ?? 0, m.Gpu?.Load ?? 0, light);
        }
        if (Value(m, mode) is not { } value) return null;
        var color = value.Hot ? (light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 88))
                              : (light ? Color.Black : Color.White);
        return Number(size, value.Text, color);
    }

    // The digits as an outline stretched to fill the icon: the largest
    // possible glyphs at 16 px, independent of the font's own proportions.
    static Bitmap Number(int size, string text, Color color)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = new GraphicsPath();
        using (var family = new FontFamily("Segoe UI"))
            path.AddString(text, family, (int)FontStyle.Bold, 100, Point.Empty, StringFormat.GenericTypographic);
        var b = path.GetBounds();
        using var fit = new Matrix();
        fit.Translate(0, size * 0.04f);
        fit.Scale(size / b.Width, size * 0.92f / b.Height);
        fit.Translate(-b.X, -b.Y);
        path.Transform(fit);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
        return bmp;
    }

    // Two vertical bars: CPU load (blue) and GPU load (orange), 0–100 %.
    static Bitmap Bars(int size, float cpu, float gpu, bool light)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        int w = (int)MathF.Round(size * 0.4f);
        using var track = new SolidBrush(light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
        using var cpuBrush = new SolidBrush(light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(96, 205, 255));
        using var gpuBrush = new SolidBrush(light ? Color.FromArgb(202, 80, 16) : Color.FromArgb(255, 170, 40));
        foreach (var (x, load, brush) in new[] { (0, cpu, cpuBrush), (size - w, gpu, gpuBrush) })
        {
            g.FillRectangle(track, x, 0, w, size);
            int h = (int)MathF.Round(size * Math.Clamp(load, 0, 100) / 100f);
            g.FillRectangle(brush, x, size - h, w, h);
        }
        return bmp;
    }

    // The taskbar has its own light/dark setting, separate from the app theme.
    static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    // Icon.FromHandle does not own the handle; the caller frees it with this.
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
