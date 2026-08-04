using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Small owner-drawn building blocks shared by the pages: nav button,
// toggle switch, card panel, rounded pill button.

// One icon button in the 48px sidebar rail. The icon is drawn via a delegate
// so all glyphs live in NavIcons below and stay plain GDI+ line art.
sealed class NavButton : Control
{
    public Action<Graphics, RectangleF, Color> IconPainter = static (_, _, _) => { };
    bool _active, _hover, _badge;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Invalidate(); } }
    }

    // Accent dot at the icon's top-right corner — used on the About button
    // while an update is pending.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Badge
    {
        get => _badge;
        set { if (_badge != value) { _badge = value; Invalidate(); } }
    }

    public NavButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        // Almost the full rail width — the accent marker sits at the rail's left
        // edge, so it must be inside this control's client area to be visible.
        // One pixel stays free on the right for the rail's separator line.
        Size = new Size(47, 36);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.WinBg);

        if (_active || _hover)
        {
            using var bg = new SolidBrush(_active ? Theme.AccentSoft : Theme.Hover);
            using var path = Theme.RoundedRect(new RectangleF(5.5f, 0.5f, Width - 10, Height - 1), 5);
            g.FillPath(bg, path);
        }
        if (_active)
        {
            using var accent = new SolidBrush(Theme.Accent);
            using var path = Theme.RoundedRect(new RectangleF(0, 8, 3, Height - 16), 1.5f);
            g.FillPath(accent, path);
        }

        var iconColor = _active || _hover ? Theme.Fg : Theme.Fg2;
        var box = new RectangleF((Width - 16) / 2f, (Height - 16) / 2f, 16, 16);
        IconPainter(g, box, iconColor);

        if (_badge)
        {
            // Ring in the rail color so the dot stays readable on hover/active fills.
            using var dot  = new SolidBrush(Theme.Accent);
            using var ring = new Pen(Parent?.BackColor ?? Theme.WinBg, 1.5f);
            var r = new RectangleF(box.Right - 3.5f, box.Top - 3.5f, 7, 7);
            g.FillEllipse(dot, r);
            g.DrawEllipse(ring, r);
        }
    }
}

// The five sidebar glyphs — GDI+ line art on a 16x16 box, mirroring the mockup SVGs.
static class NavIcons
{
    static Pen MakePen(Color c) => new(c, 1.4f)
        { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    public static void Dashboard(Graphics g, RectangleF b, Color c)
    {
        using var pen = MakePen(c);
        // Same geometry as the mockup SVG: 5.4px squares at 1.5/9.1 on a 16px box.
        float u = b.Width / 16f, s = 5.4f * u;
        foreach (var (x, y) in new[] { (1.5f, 1.5f), (9.1f, 1.5f), (1.5f, 9.1f), (9.1f, 9.1f) })
        {
            using var p = Theme.RoundedRect(new RectangleF(b.X + x * u, b.Y + y * u, s, s), u);
            g.DrawPath(pen, p);
        }
    }

    public static void Outputs(Graphics g, RectangleF b, Color c)
    {
        using var pen = MakePen(c);
        float cx = b.X + b.Width / 2, cy = b.Y + b.Height * 0.56f;
        g.DrawEllipse(pen, cx - 1.6f, cy - 1.6f, 3.2f, 3.2f);
        g.DrawLine(pen, cx, cy + 1.8f, cx, b.Bottom);
        g.DrawArc(pen, cx - 4.6f, cy - 4.4f, 9.2f, 9.2f, 215, 110);   // inner arc
        g.DrawArc(pen, cx - 7.4f, cy - 7.4f, 14.8f, 14.8f, 220, 100); // outer arc
    }

    public static void Sensors(Graphics g, RectangleF b, Color c)
    {
        using var pen = MakePen(c);
        var chip = new RectangleF(b.X + 3, b.Y + 3, b.Width - 6, b.Height - 6);
        using (var p = Theme.RoundedRect(chip, 1.5f)) g.DrawPath(pen, p);
        g.DrawRectangle(pen, b.X + 6.2f, b.Y + 6.2f, b.Width - 12.4f, b.Height - 12.4f);
        foreach (var t in new[] { 0.32f, 0.5f, 0.68f })
        {
            float v = b.X + b.Width * t, h = b.Y + b.Height * t;
            g.DrawLine(pen, v, b.Y, v, b.Y + 2.2f);                    // top pins
            g.DrawLine(pen, v, b.Bottom - 2.2f, v, b.Bottom);          // bottom pins
            g.DrawLine(pen, b.X, h, b.X + 2.2f, h);                    // left pins
            g.DrawLine(pen, b.Right - 2.2f, h, b.Right, h);            // right pins
        }
    }

    public static void Settings(Graphics g, RectangleF b, Color c)
    {
        using var pen = MakePen(c);
        float y1 = b.Y + b.Height * 0.3f, y2 = b.Y + b.Height * 0.7f;
        g.DrawLine(pen, b.X, y1, b.Right, y1);
        g.DrawLine(pen, b.X, y2, b.Right, y2);
        using var knobBg = new SolidBrush(Theme.WinBg);
        var k1 = new RectangleF(b.X + b.Width * 0.58f - 2.2f, y1 - 2.2f, 4.4f, 4.4f);
        var k2 = new RectangleF(b.X + b.Width * 0.30f - 2.2f, y2 - 2.2f, 4.4f, 4.4f);
        g.FillEllipse(knobBg, k1); g.DrawEllipse(pen, k1);
        g.FillEllipse(knobBg, k2); g.DrawEllipse(pen, k2);
    }

    public static void About(Graphics g, RectangleF b, Color c)
    {
        using var pen = MakePen(c);
        g.DrawEllipse(pen, b.X + 1, b.Y + 1, b.Width - 2, b.Height - 2);
        float cx = b.X + b.Width / 2;
        g.DrawLine(pen, cx, b.Y + b.Height * 0.45f, cx, b.Y + b.Height * 0.7f);
        using var dot = new SolidBrush(c);
        g.FillEllipse(dot, cx - 1f, b.Y + b.Height * 0.26f, 2f, 2f);
    }

    // The app's signal-bars logo, used on the About page.
    public static void Logo(Graphics g, RectangleF b, Color accent)
    {
        float u = b.Width / 16f;
        using var solid = new SolidBrush(accent);
        using var faded = new SolidBrush(Color.FromArgb(115, accent));
        var bars = new[] { (1f, 10f, solid), (5f, 7f, solid), (9f, 4f, solid), (13f, 1f, faded) };
        foreach (var (x, y, brush) in bars)
        {
            using var p = Theme.RoundedRect(
                new RectangleF(b.X + x * u, b.Y + y * u, 2.6f * u, (15f - y) * u), 0.8f * u);
            g.FillPath(brush, p);
        }
    }
}

// Windows-11-style toggle switch (38x19), replaces CheckBox where the mockup
// shows a switch. Raises CheckedChanged on user click and on programmatic set.
sealed class ToggleSwitch : Control
{
    bool _checked;

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
    }

    // Set without firing CheckedChanged — for populating from config.
    public void SetChecked(bool value) { _checked = value; Invalidate(); }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        Size = new Size(38, 19);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.CardBg);

        var track = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(track, (Height - 1) / 2f);
        if (_checked)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
        }
        else
        {
            using var fill = new SolidBrush(Theme.BarTrack);
            using var border = new Pen(Theme.InputBorder);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        float knob = Height - 6;
        float x = _checked ? Width - knob - 3 : 3;
        using var knobBrush = new SolidBrush(_checked ? Color.White : Theme.Fg2);
        g.FillEllipse(knobBrush, x, 3, knob, knob);
    }
}

// Rounded card container with themed background/border — the building block
// of every page. Children get the card background automatically.
class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.CardBg;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.WinBg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, 7);
        using var bg = new SolidBrush(Theme.CardBg);
        using var border = new Pen(Theme.CardBorder);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
    }
}

// Mockup-style checkbox: 15px rounded box, accent fill with a white check
// when set, crisp Theme.Fg label. Replaces CheckBox, whose dark-mode
// rendering washes the label out against card backgrounds.
sealed class FlatCheck : Control
{
    const int BoxSize = 15, Gap = 8;
    bool _checked, _hover;

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set { if (_checked != value) { _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
    }

    public FlatCheck()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Font = Theme.Base;
        Height = 22;
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Width = BoxSize + Gap + TextRenderer.MeasureText(Text, Font).Width + 4;
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.CardBg);

        var box = new RectangleF(0.5f, (Height - BoxSize) / 2f + 0.5f, BoxSize - 1, BoxSize - 1);
        using var path = Theme.RoundedRect(box, 3);
        if (_checked)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
            using var check = new Pen(Theme.AccentFg, 1.8f)
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float bx = box.X, by = box.Y;
            g.DrawLines(check,
            [
                new PointF(bx + 3.2f, by + 7.2f),
                new PointF(bx + 6.0f, by + 10f),
                new PointF(bx + 10.8f, by + 4.2f),
            ]);
        }
        else
        {
            using var fill = new SolidBrush(Theme.InputBg);
            using var border = new Pen(_hover ? Theme.Fg3 : Theme.InputBorder, 1.2f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(BoxSize + Gap, 0, Width - BoxSize - Gap, Height), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

// A mockup-style input: rounded border, vertically centered text, accent
// bottom edge while focused. Wraps a borderless TextBox because a bare
// WinForms TextBox can neither round its corners nor center vertically.
sealed class InputBox : Control
{
    public readonly TextBox Box;

    public InputBox()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // Box must exist before Size is set — the Size setter triggers OnLayout.
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Fg,
            Font = Theme.Base,
        };
        Box.GotFocus  += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Size = new Size(70, 28);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Box?.SetBounds(8, (Height - Box.Height) / 2 + 1, Width - 16, Box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.CardBg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, 4);
        using var bg = new SolidBrush(Theme.InputBg);
        using var border = new Pen(Theme.InputBorder);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
        // 2px bottom edge, accent while the field has focus (mockup style)
        using var bottom = new Pen(Box.Focused ? Theme.Accent : Theme.InputBorder, 2f);
        g.DrawLine(bottom, 4, Height - 1.5f, Width - 4, Height - 1.5f);
    }
}

// The floating "unsaved changes" panel. Sizes itself from the ACTUAL child
// sizes at layout time (never from cached text measurements — those break as
// soon as DPI scaling resizes the children after creation).
sealed class SaveBar : CardPanel
{
    public readonly Label Msg;
    public readonly PillButton Button;

    public SaveBar()
    {
        Msg = new Label
        {
            Text = "Unsaved changes", AutoSize = true,
            Font = Theme.Small, ForeColor = Theme.Fg2, BackColor = Theme.CardBg,
        };
        Button = new PillButton { Text = "Save", Size = new Size(64, 26) };
        Controls.Add(Msg);
        Controls.Add(Button);
        Msg.TextChanged += (_, _) => PerformLayout();
        Visible = false;
    }

    // Message on the left, button on the right, bar shrink-wraps both.
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        const int h = 42;
        int x = 14;
        Msg.Location = new Point(x, (h - Msg.Height) / 2);
        x += Msg.Width + 12;
        if (Button.Visible)
        {
            Button.Location = new Point(x, (h - Button.Height) / 2);
            x += Button.Width;
        }
        Size = new Size(x + 8, h);
    }
}

// Accent-colored rounded button (the save bar's "Save"). Kept deliberately
// simple: flat fill, hover brightening, no focus chrome beyond the cue.
sealed class PillButton : Control
{
    bool _hover;

    public PillButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Font = Theme.SemiBold;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.CardBg);

        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, 5);
        var bg = Theme.Accent;
        if (_hover) bg = Theme.Mix(bg, Theme.Dark ? Color.White : Color.Black, 0.08f);
        using var fill = new SolidBrush(bg);
        g.FillPath(fill, path);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), Theme.AccentFg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
