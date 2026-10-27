using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// What the tray icon shows instead of the logo (GeneralConfig.TrayIcon) and
// how that icon is drawn.
static class TrayIcon
{
    public const string Logo = "logo", CpuLoad = "cpu_load", CpuTemp = "cpu_temp",
                        GpuLoad = "gpu_load", GpuTemp = "gpu_temp", RamLoad = "ram_load",
                        LoadBars = "load_bars", TempBars = "temp_bars";

    // Order of the Settings dropdown.
    public static readonly string[] Modes = [Logo, CpuLoad, CpuTemp, GpuLoad, GpuTemp, RamLoad, LoadBars, TempBars];

    // Same thresholds as the dashboard's temperature colors.
    const float WarmTempC = 60, HotTempC = 80;

    // Temperature bars run from empty at 30 °C to full at 100 °C: a PC is
    // hardly ever cooler, and CPUs throttle at about 95–100 °C.
    const float BarMinC = 30, BarMaxC = 100;

    // The number to show for a numeric mode: at most two digits (100 % is
    // shown as 99), Hot for temperatures from 80 °C. Hot follows the shown
    // (rounded) number, so a displayed "80" is always red. Null when the mode
    // is not numeric or the value is not available.
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
        bool hot = mode is CpuTemp or GpuTemp && n >= HotTempC;
        return (n.ToString(), hot);
    }

    // Bar height for a temperature, 0 at 30 °C to 1 at 100 °C.
    internal static float TempFraction(float c) => Math.Clamp((c - BarMinC) / (BarMaxC - BarMinC), 0, 1);

    // Everything the icon depends on: the icon is redrawn only when this
    // changes. Temperature bars are drawn from the rounded values, so the key
    // covers their color thresholds; the taskbar theme sets the text color.
    internal static string Key(MetricsSnapshot m, string? mode) => (mode switch
    {
        LoadBars => $"{mode}|{m.Cpu?.Load}|{m.Gpu?.Load}",
        TempBars => $"{mode}|{Round(m.Cpu?.TempC)}|{Round(m.Gpu?.TempC)}",
        _ => $"{mode}|{Value(m, mode)}",
    }) + (TaskbarIsLight() ? "|light" : "|dark");

    static int? Round(float? v) => v == null ? null : (int)MathF.Round(v.Value);

    // Square icon bitmap at the tray's icon size. Returns null when the mode
    // has nothing to show, so the caller keeps the logo.
    public static Bitmap? Render(MetricsSnapshot m, string? mode, int size)
    {
        bool light = TaskbarIsLight();
        if (mode == LoadBars)
        {
            if (m.Cpu?.Load == null && m.Gpu?.Load == null) return null;
            var cpu = light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(96, 205, 255);
            var gpu = light ? Color.FromArgb(202, 80, 16) : Color.FromArgb(255, 170, 40);
            return Bars(size, light, ((m.Cpu?.Load ?? 0) / 100f, cpu), ((m.Gpu?.Load ?? 0) / 100f, gpu));
        }
        if (mode == TempBars)
        {
            if (m.Cpu?.TempC == null && m.Gpu?.TempC == null) return null;
            return Bars(size, light, TempBar(Round(m.Cpu?.TempC), light), TempBar(Round(m.Gpu?.TempC), light));
        }
        if (Value(m, mode) is not { } value) return null;
        return Number(size, value.Text, value.Hot ? TempColor(HotTempC, light) : light ? Color.Black : Color.White);
    }

    static (float, Color) TempBar(int? c, bool light) =>
        c == null ? (0, Color.Transparent) : (TempFraction(c.Value), TempColor(c.Value, light));

    // Green below 60 °C, yellow below 80 °C, red from 80 °C; darker shades
    // on a light taskbar.
    static Color TempColor(float c, bool light) =>
        c >= HotTempC  ? (light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 88))
        : c >= WarmTempC ? (light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(252, 225, 0))
        :                  (light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95));

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

    // Two vertical bars on a faint track, left and right; each filled from
    // the bottom by its fraction (0–1).
    static Bitmap Bars(int size, bool light, (float Fraction, Color Color) left, (float Fraction, Color Color) right)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        int w = (int)MathF.Round(size * 0.4f);
        using var track = new SolidBrush(light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
        foreach (var (x, bar) in new[] { (0, left), (size - w, right) })
        {
            g.FillRectangle(track, x, 0, w, size);
            int h = (int)MathF.Round(size * Math.Clamp(bar.Fraction, 0, 1));
            using var fill = new SolidBrush(bar.Color);
            g.FillRectangle(fill, x, size - h, w, h);
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
