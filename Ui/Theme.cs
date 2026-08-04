using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Central color palette for all owner-drawn UI, mirroring the approved mockup
// tokens. Initialized once at startup from the effective system color mode;
// standard controls (TextBox, CheckBox, scrollbars) are themed by
// Application.SetColorMode, everything custom reads these fields.
static class Theme
{
    public static bool Dark { get; private set; }

    // DPI scale factor (1.0 at 100%, 1.75 at 175%). The process runs
    // system-DPI-aware, so this is fixed for the app's lifetime: system DPI at
    // startup, or an explicit override (demo mode renders high-res screenshots
    // with --scale). Everything visual goes through it — S()/SF() for every
    // hand-drawn pixel value (layout constants, icon strokes, corner radii,
    // control sizes) and the fonts below, which use pixel units for exactly
    // this reason: point-based fonts would follow the OS DPI instead of the
    // override.
    public static float Scale { get; private set; } = 1f;

    public static int S(int px) => (int)MathF.Round(px * Scale);
    public static float SF(float px) => px * Scale;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    public static Color WinBg,      CardBg,   CardBorder;
    public static Color Fg,         Fg2,      Fg3;
    public static Color Accent,     AccentSoft, AccentFg;
    public static Color BarTrack,   Hover;
    public static Color Good,       Warn,     Crit;
    public static Color InputBg,    InputBorder;
    public static Color Scroll,     ScrollHover;

    // Sizes are the mockup's CSS pixel values, created in Init once the scale
    // factor is known.
    public static Font Base       = null!;   // 13px
    public static Font Small      = null!;   // 11.5px
    public static Font Tiny       = null!;   // 10.5px
    public static Font SemiBold   = null!;   // 13px
    public static Font Title      = null!;   // 16px
    public static Font BigValue   = null!;   // 26px
    public static Font BigCompact = null!;   // 21px — dashboard half-width cards
    public static Font GroupHead  = null!;   // 10.5px semibold — sensors page group labels

    public static void Init(float? scaleOverride = null)
    {
        Scale = scaleOverride ?? GetDpiForSystem() / 96f;

        Base       = Px("Segoe UI", 13f);
        Small      = Px("Segoe UI", 11.5f);
        Tiny       = Px("Segoe UI", 10.5f);
        SemiBold   = Px("Segoe UI Semibold", 13f);
        Title      = Px("Segoe UI Semibold", 16f);
        BigValue   = Px("Segoe UI Semibold", 26f);
        BigCompact = Px("Segoe UI Semibold", 21f);
        GroupHead  = Px("Segoe UI Semibold", 10.5f);

#pragma warning disable WFO5001 // SetColorMode/IsDarkModeEnabled are marked experimental
        Dark = Application.IsDarkModeEnabled;
#pragma warning restore WFO5001

        if (Dark)
        {
            WinBg       = FromHex("202124");
            CardBg      = FromHex("2b2c30");
            CardBorder  = FromHex("3a3b40");
            Fg          = FromHex("f2f2f3");
            Fg2         = FromHex("a8abb1");
            Fg3         = FromHex("7c7f86");
            Accent      = FromHex("5ec1ff");
            AccentSoft  = Color.FromArgb(41, Accent);
            AccentFg    = FromHex("0d2b41");
            BarTrack    = FromHex("3d3e44");
            Hover       = FromHex("333438");
            Good        = FromHex("6ccb5f");
            Warn        = FromHex("f0a30a");
            Crit        = FromHex("ff6b5e");
            InputBg     = FromHex("242529");
            InputBorder = FromHex("4a4b51");
            Scroll      = FromHex("4c4e54");
            ScrollHover = FromHex("5f6167");
        }
        else
        {
            WinBg       = FromHex("f3f3f3");
            CardBg      = FromHex("ffffff");
            CardBorder  = FromHex("e4e4e6");
            Fg          = FromHex("1b1c1e");
            Fg2         = FromHex("63666c");
            Fg3        = FromHex("8b8e94");
            Accent      = FromHex("0067c0");
            AccentSoft  = Color.FromArgb(36, Accent);
            AccentFg    = Color.White;
            BarTrack    = FromHex("e6e7ea");
            Hover       = FromHex("f6f6f7");
            Good        = FromHex("107c10");
            Warn        = FromHex("c46200");
            Crit        = FromHex("c42b1c");
            InputBg     = FromHex("fdfdfd");
            InputBorder = FromHex("d6d6d9");
            Scroll      = FromHex("c4c6cb");
            ScrollHover = FromHex("a8abb1");
        }
    }

    static Font Px(string family, float designPx) =>
        new(family, SF(designPx), System.Drawing.GraphicsUnit.Pixel);

    // Traffic-light coloring for temperatures — applied to the value text only.
    public static Color TempColor(float? c) =>
        c == null ? Fg :
        c <  60   ? Good :
        c <  80   ? Warn :
                    Crit;

    // Linear blend a→b, keeping a's alpha. Used for hover shades.
    public static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb(a.A,
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

    public static Color FromHex(string hex) =>
        Color.FromArgb(
            Convert.ToInt32(hex[..2],   16),
            Convert.ToInt32(hex[2..4],  16),
            Convert.ToInt32(hex[4..6],  16));

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
