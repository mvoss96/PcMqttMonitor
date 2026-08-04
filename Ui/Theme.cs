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

    public static Color WinBg,      CardBg,   CardBorder;
    public static Color Fg,         Fg2,      Fg3;
    public static Color Accent,     AccentSoft, AccentFg;
    public static Color BarTrack,   Hover;
    public static Color Good,       Warn,     Crit;
    public static Color InputBg,    InputBorder;
    public static Color Scroll,     ScrollHover;

    // Sizes mirror the mockup's CSS pixel values (px * 72 / 96 = pt).
    public static readonly Font Base     = new("Segoe UI", 9.75f);           // 13px
    public static readonly Font Small    = new("Segoe UI", 8.5f);            // 11.5px
    public static readonly Font Tiny     = new("Segoe UI", 8f);              // 10.5px
    public static readonly Font SemiBold = new("Segoe UI Semibold", 9.75f);  // 13px
    public static readonly Font Title    = new("Segoe UI Semibold", 12f);    // 16px
    public static readonly Font BigValue = new("Segoe UI Semibold", 19.5f);  // 26px

    public static void Init()
    {
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
