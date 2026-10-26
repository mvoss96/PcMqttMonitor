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
    public static Color InputBottom;              // unfocused bottom stroke of inputs
    public static Color ButtonBg,   ButtonHover;  // standard (non-accent) buttons
    public static Color WarnBg,     WarnIcon;     // warning InfoBar

    // Windows 11 type ramp (Body 14, Caption 12, Subtitle 20), created in Init
    // once the scale factor is known.
    public static Font Base       = null!;   // 14px Body
    public static Font Small      = null!;   // 12px Caption
    public static Font Tiny       = null!;   // 12px Caption — settings subtitles
    public static Font SemiBold   = null!;   // 14px Body Strong
    public static Font Title      = null!;   // 20px Subtitle — page titles
    public static Font BigValue   = null!;   // 26px
    public static Font BigCompact = null!;   // 22px — dashboard half-width cards
    public static Font GroupHead  = null!;   // 12px semibold — sensors page group labels
    public static Font Icons      = null!;   // 16px icon glyphs (sidebar, banner)
    public static Font IconsSmall = null!;   // 12px icon glyphs (chevrons)

    // Content of every page starts below its title at this offset.
    public static int PageTop => S(52);

    public static void Init(float? scaleOverride = null)
    {
        Scale = scaleOverride ?? GetDpiForSystem() / 96f;

        // Segoe UI Variable ships with Windows 11. GDI+ only sees its named
        // instances, under names cut to 31 characters ("…Display Semib").
        // Windows 10 falls back to Segoe UI and the MDL2 icon font.
        string text     = Installed("Segoe UI Variable Text", "Segoe UI");
        string textSemi = Installed("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
        string display  = Installed("Segoe UI Variable Display Semib", "Segoe UI Semibold");
        string icons    = Installed("Segoe Fluent Icons", "Segoe MDL2 Assets");

        Base       = Px(text, 14f);
        Small      = Px(text, 12f);
        Tiny       = Px(text, 12f);
        SemiBold   = Px(textSemi, 14f);
        Title      = Px(display, 20f);
        BigValue   = Px(display, 26f);
        BigCompact = Px(display, 22f);
        GroupHead  = Px(textSemi, 12f);
        Icons      = Px(icons, 16f);
        IconsSmall = Px(icons, 12f);

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
            InputBg     = FromHex("35363b");
            InputBorder = FromHex("4a4b51");
            Scroll      = FromHex("4c4e54");
            ScrollHover = FromHex("5f6167");
            InputBottom = FromHex("8b8d93");
            ButtonBg    = FromHex("35363b");
            ButtonHover = FromHex("3d3e44");
            WarnBg      = FromHex("433519");
            WarnIcon    = FromHex("fce100");
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
            InputBottom = FromHex("8a8a8a");
            ButtonBg    = FromHex("fbfbfb");
            ButtonHover = FromHex("f3f3f4");
            WarnBg      = FromHex("fff4ce");
            WarnIcon    = FromHex("9d5d00");
        }
    }

    static Font Px(string family, float designPx) =>
        new(family, SF(designPx), System.Drawing.GraphicsUnit.Pixel);

    // new Font() silently substitutes a default for a missing family, so check first.
    static string Installed(string family, string fallback) =>
        FontFamily.Families.Any(f => f.Name == family) ? family : fallback;

    // Single glyph from the icon font, e.g. Glyph(0xE713) for the gear.
    public static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

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
