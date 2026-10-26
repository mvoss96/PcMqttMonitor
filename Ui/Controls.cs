using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// Small owner-drawn building blocks shared by the pages, styled after the
// Windows 11 (WinUI) controls: nav button, toggle switch, card panel, input,
// button, choice box, info bar, save footer and the menu renderer.

// One icon button in the 48px sidebar rail; the icon is a Segoe Fluent Icons glyph.
sealed class NavButton : Control
{
    public string Glyph = "";
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
        Size = new Size(Theme.S(48) - 1, Theme.S(36));
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
            using var path = Theme.RoundedRect(
                new RectangleF(Theme.SF(5.5f), 0.5f, Width - Theme.SF(10f), Height - 1), Theme.SF(5));
            g.FillPath(bg, path);
        }
        if (_active)
        {
            using var accent = new SolidBrush(Theme.Accent);
            using var path = Theme.RoundedRect(
                new RectangleF(0, Theme.S(8), Theme.SF(3), Height - Theme.S(16)), Theme.SF(1.5f));
            g.FillPath(accent, path);
        }

        var iconColor = _active || _hover ? Theme.Fg : Theme.Fg2;
        float icon = Theme.SF(16);
        var box = new RectangleF((Width - icon) / 2f, (Height - icon) / 2f, icon, icon);
        TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(0, 0, Width, Height), iconColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        if (_badge)
        {
            // Ring in the rail color so the dot stays readable on hover/active fills.
            using var dot  = new SolidBrush(Theme.Accent);
            using var ring = new Pen(Parent?.BackColor ?? Theme.WinBg, Theme.SF(1.5f));
            var r = new RectangleF(box.Right - Theme.SF(3.5f), box.Top - Theme.SF(3.5f), Theme.SF(7), Theme.SF(7));
            g.FillEllipse(dot, r);
            g.DrawEllipse(ring, r);
        }
    }
}

// Segoe Fluent Icons code points used by the UI, plus the app logo.
static class NavIcons
{
    public const int Dashboard = 0xF0E2, Outputs = 0xE93E, Sensors = 0xE950,
                     Settings = 0xE713, About = 0xE946,
                     Warning = 0xE7BA, ChevronDown = 0xE70D, ChevronRight = 0xE76C, Check = 0xE73E;

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
        Size = new Size(Theme.S(38), Theme.S(19));
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

        float margin = Theme.SF(3);
        float knob = Height - 2 * margin;
        float x = _checked ? Width - knob - margin : margin;
        using var knobBrush = new SolidBrush(_checked ? Color.White : Theme.Fg2);
        g.FillEllipse(knobBrush, x, margin, knob, knob);
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
        using var path = Theme.RoundedRect(r, Theme.SF(8));
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
    static readonly int BoxSize = Theme.S(15), Gap = Theme.S(8);
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
        Height = Theme.S(22);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        Width = BoxSize + Gap + TextRenderer.MeasureText(Text, Font).Width + Theme.S(4);
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
        using var path = Theme.RoundedRect(box, Theme.SF(3));
        if (_checked)
        {
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
            using var check = new Pen(Theme.AccentFg, Theme.SF(1.8f))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            float bx = box.X, by = box.Y;
            g.DrawLines(check,
            [
                new PointF(bx + Theme.SF(3.2f), by + Theme.SF(7.2f)),
                new PointF(bx + Theme.SF(6.0f), by + Theme.SF(10f)),
                new PointF(bx + Theme.SF(10.8f), by + Theme.SF(4.2f)),
            ]);
        }
        else
        {
            using var fill = new SolidBrush(Theme.InputBg);
            using var border = new Pen(_hover ? Theme.Fg3 : Theme.InputBorder, Theme.SF(1.2f));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        TextRenderer.DrawText(g, Text, Font,
            new Rectangle(BoxSize + Gap, 0, Width - BoxSize - Gap, Height), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

// Windows 11 text box: 4px rounded border, a stronger 1px bottom stroke, and a
// 2px accent bottom edge while focused. Wraps a borderless TextBox because a
// bare WinForms TextBox can neither round its corners nor center vertically.
sealed class InputBox : Control
{
    public readonly TextBox Box;

    // Red border and bottom edge for a value that cannot be saved.
    bool _invalid;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Invalid
    {
        get => _invalid;
        set { if (_invalid != value) { _invalid = value; Invalidate(); } }
    }

    // Optional suggestion list: a chevron at the right edge (or F4 / Alt+Down
    // in the field) opens a menu; picking an entry replaces the text. The
    // list is fetched on every open. Typing stays possible.
    Func<string[]>? _suggestions;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string[]>? Suggestions
    {
        get => _suggestions;
        set { _suggestions = value; PerformLayout(); Invalidate(); }
    }

    static readonly int ChevronW = Theme.S(32);
    bool OnChevron(Point p) => _suggestions != null && p.X >= Width - ChevronW;

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
        Box.KeyDown += (_, e) =>
        {
            if (_suggestions != null && (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down)))
            {
                OpenSuggestions();
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        Controls.Add(Box);
        Size = new Size(Theme.S(70), Theme.S(32));
        Cursor = Cursors.IBeam;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (OnChevron(e.Location)) OpenSuggestions();
        else Box.Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = OnChevron(e.Location) ? Cursors.Hand : Cursors.IBeam;
        base.OnMouseMove(e);
    }

    void OpenSuggestions()
    {
        var items = _suggestions!();
        var menu = new ContextMenuStrip();
        MenuStyle.Apply(menu);
        menu.MinimumSize = new Size(Width, 0);
        string current = Box.Text.Trim();
        foreach (var text in items)
        {
            var item = new ToolStripMenuItem(text)
            {
                Checked = current.Length > 0 && text.StartsWith(current, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) =>
            {
                Box.Text = text;
                Box.Focus();
                Box.SelectionStart = Box.Text.Length;
            };
            menu.Items.Add(item);
        }
        if (items.Length == 0)
            menu.Items.Add(new ToolStripMenuItem(L.T.NoSuggestions) { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point(0, Height + Theme.S(2)));
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int right = _suggestions != null ? ChevronW : Theme.S(10);
        Box?.SetBounds(Theme.S(10), (Height - Box.Height) / 2, Width - Theme.S(10) - right, Box.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.CardBg);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, Theme.SF(4));
        using var bg = new SolidBrush(Theme.InputBg);
        using var border = new Pen(_invalid ? Theme.Crit : Theme.InputBorder);
        g.FillPath(bg, path);
        g.DrawPath(border, path);

        // Bottom stroke, clipped to the rounded shape: 1px strong stroke at rest,
        // 2px accent (or red when invalid) while focused or invalid.
        bool strong = _invalid || Box.Focused;
        float h = strong ? Theme.SF(2) : Math.Max(1f, Theme.SF(1));
        var clip = g.Clip;
        g.SetClip(path);
        using var bottom = new SolidBrush(_invalid ? Theme.Crit : Box.Focused ? Theme.Accent : Theme.InputBottom);
        g.FillRectangle(bottom, 0, Height - h, Width, h);
        g.Clip = clip;

        if (_suggestions != null)
            TextRenderer.DrawText(g, Theme.Glyph(NavIcons.ChevronDown), Theme.IconsSmall,
                new Rectangle(Width - ChevronW, 0, ChevronW, Height), Theme.Fg2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// Windows 11 button: 4px corners, accent fill for the primary action, subtle
// fill with a border for standard actions, greyed out when disabled. Corners
// are cleared with BackColor, which follows the parent unless set.
sealed class ActionButton : Control
{
    bool _hover;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Accent { get; set; }

    public ActionButton(string text, bool accent)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        Cursor = Cursors.Hand;
        Font = Theme.Base;
        Text = text;
        Accent = accent;
        // Windows 11 dialog buttons: 32px high, at least 96px wide.
        Size = new Size(Math.Max(Theme.S(96), TextRenderer.MeasureText(text, Theme.Base).Width + Theme.S(32)),
                        Theme.S(32));
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        Color fill, text;
        bool border = !Accent;
        if (!Enabled)
        {
            fill = Accent ? Theme.InputBorder : Theme.ButtonBg;
            text = Theme.Fg3;
        }
        else if (Accent)
        {
            fill = _hover ? Theme.Mix(Theme.Accent, Theme.Dark ? Color.Black : Color.White, 0.1f) : Theme.Accent;
            text = Theme.AccentFg;
        }
        else
        {
            fill = _hover ? Theme.ButtonHover : Theme.ButtonBg;
            text = Theme.Fg;
        }

        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, Theme.SF(4));
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (border)
        {
            using var pen = new Pen(Theme.InputBorder);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

// Footer of the content area while there are unsaved changes, laid out like
// a Windows 11 dialog footer: message left, Discard and Save right.
sealed class SaveFooter : Control
{
    public readonly Label Msg;
    public readonly ActionButton Revert, Button;

    public SaveFooter()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // Children first: setting Height triggers OnLayout, which positions them.
        Msg = new Label { AutoSize = false, AutoEllipsis = true, Height = Theme.S(22), Font = Theme.Base, ForeColor = Theme.Fg2, BackColor = Theme.CardBg, TextAlign = ContentAlignment.MiddleLeft };
        Revert = new ActionButton(L.T.Discard, accent: false);
        Button = new ActionButton(L.T.Save, accent: true);
        Controls.Add(Msg);
        Controls.Add(Revert);
        Controls.Add(Button);
        Msg.TextChanged += (_, _) => PerformLayout();
        Dock = DockStyle.Bottom;
        Height = Theme.S(64);
        BackColor = Theme.CardBg;
        Visible = false;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int pad = Theme.S(16), x = Width - pad;
        foreach (var b in new[] { Button, Revert })
        {
            if (!b.Visible) continue;
            x -= b.Width;
            b.Location = new Point(x, (Height - b.Height) / 2);
            x -= Theme.S(8);
        }
        // The message takes the space left of the buttons, cut with "…" if longer.
        Msg.Bounds = new Rectangle(pad, (Height - Msg.Height) / 2, Math.Max(0, x - pad), Msg.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.CardBg);
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }
}

// Windows 11 warning InfoBar: tinted rounded box with a warning glyph, a
// message and one action button. Inset so it lines up with the dashboard cards.
sealed class InfoBar : Control
{
    public readonly ActionButton Action;
    readonly string _message;
    static readonly int Inset = Theme.S(14);

    public InfoBar(string message, string action)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        _message = message;
        // Child first: setting Height triggers OnLayout, which positions it.
        Action = new ActionButton(action, accent: false) { BackColor = Theme.WarnBg };
        Controls.Add(Action);
        Dock = DockStyle.Top;
        Height = Inset + Theme.S(52);
        BackColor = Theme.WinBg;
    }

    RectangleF Box => new(Inset + 0.5f, Inset + 0.5f, Width - 2 * Inset - 1, Height - Inset - 1);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        Action.Location = new Point(Width - Inset - Theme.S(10) - Action.Width,
            Inset + (Height - Inset - Action.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.WinBg);
        var box = Box;
        using (var path = Theme.RoundedRect(box, Theme.SF(8)))
        using (var fill = new SolidBrush(Theme.WarnBg))
        using (var pen = new Pen(Theme.CardBorder))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        var row = new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height);
        TextRenderer.DrawText(g, Theme.Glyph(NavIcons.Warning), Theme.Icons,
            new Rectangle(row.X + Theme.S(14), row.Y, Theme.S(18), row.Height), Theme.WarnIcon,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        int textX = row.X + Theme.S(42);
        TextRenderer.DrawText(g, _message, Theme.Base,
            new Rectangle(textX, row.Y, Action.Left - textX - Theme.S(8), row.Height), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

// Windows 11 style combo box for a fixed list: drawn like an InputBox with a
// chevron, opens a menu of the choices. Replaces the native ComboBox, whose
// frame cannot be themed.
sealed class ChoiceBox : Control
{
    readonly string[] _items;
    int _selected;
    bool _hover;

    public event EventHandler? SelectedIndexChanged;

    public ChoiceBox(string[] items)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        _items = items;
        Font = Theme.Base;
        Cursor = Cursors.Hand;
        int textW = items.Max(i => TextRenderer.MeasureText(i, Theme.Base).Width);
        Size = new Size(Math.Max(Theme.S(120), textW + Theme.S(48)), Theme.S(32));
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnGotFocus(EventArgs e)  { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); OpenMenu(); }

    // Up/Down reach OnKeyDown instead of moving the focus to the next control.
    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    // Keyboard like a native ComboBox: Space, Enter, F4 or Alt+Down open the
    // list; Up/Down change the selection directly.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4 || (e.Alt && e.KeyCode == Keys.Down))
        {
            OpenMenu();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Up)
        {
            SelectedIndex = Math.Max(0, _selected - 1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Down)
        {
            SelectedIndex = Math.Min(_items.Length - 1, _selected + 1);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    void OpenMenu()
    {
        var menu = new ContextMenuStrip();
        MenuStyle.Apply(menu);
        menu.MinimumSize = new Size(Width, 0);
        for (int i = 0; i < _items.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(this, new Point(0, Height + Theme.S(2)));
        menu.Items[_selected].Select();   // arrow keys and Enter start at the current choice
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using var path = Theme.RoundedRect(r, Theme.SF(4));
        using var bg = new SolidBrush(_hover ? Theme.ButtonHover : Theme.ButtonBg);
        using var border = new Pen(Theme.InputBorder);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
        if (Focused)
        {
            // Keyboard focus: 2px accent bottom edge, like a focused InputBox.
            var clip = g.Clip;
            g.SetClip(path);
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, 0, Height - Theme.SF(2), Width, Theme.SF(2));
            g.Clip = clip;
        }

        int chevronW = Theme.S(32);
        TextRenderer.DrawText(g, _items[_selected], Font,
            new Rectangle(Theme.S(10), 0, Width - Theme.S(10) - chevronW, Height), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Theme.Glyph(NavIcons.ChevronDown), Theme.IconsSmall,
            new Rectangle(Width - chevronW, 0, chevronW, Height), Theme.Fg2,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// Shared look for popup menus (tray menu, ChoiceBox): Windows 11 rounded
// window corners via DWM, roomier items, rounded hover highlight and an
// accent check glyph.
static class MenuStyle
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    public static void Apply(ToolStripDropDownMenu menu, bool checkMargin = true)
    {
        menu.Renderer = new Renderer();
        menu.Font = Theme.Base;
        menu.Padding = new Padding(Theme.S(4));
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = checkMargin;
        // Windows 10 ignores the attribute and keeps square corners.
        menu.HandleCreated += (_, _) =>
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(menu.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        };
        menu.ItemAdded += (_, e) => e.Item!.Padding = new Padding(Theme.S(2), Theme.S(5), Theme.S(2), Theme.S(5));
    }

    sealed class Renderer() : ToolStripProfessionalRenderer(new Colors())
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(Theme.SF(2), Theme.SF(1), e.Item.Width - Theme.SF(4), e.Item.Height - Theme.SF(2));
            using var path = Theme.RoundedRect(r, Theme.SF(4));
            using var brush = new SolidBrush(Theme.Hover);
            e.Graphics.FillPath(brush, path);
        }

        // DWM draws the border of the rounded window; a square one would poke out.
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.Fg3;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, Theme.Glyph(NavIcons.Check), Theme.IconsSmall, e.ImageRectangle,
                Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.CardBg;
        public override Color ImageMarginGradientBegin    => Theme.CardBg;
        public override Color ImageMarginGradientMiddle   => Theme.CardBg;
        public override Color ImageMarginGradientEnd      => Theme.CardBg;
        public override Color MenuBorder                  => Theme.CardBorder;
        public override Color MenuItemBorder              => Color.Transparent;
        public override Color MenuItemSelected            => Theme.Hover;
        public override Color SeparatorDark               => Theme.CardBorder;
        public override Color SeparatorLight              => Theme.CardBg;
    }
}
