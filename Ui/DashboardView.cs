using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// The dashboard: owner-drawn cards per hardware group (big value, accent bar,
// detail rows, 60s sparkline), matching the approved mockup. One control, one
// paint — a refresh is "copy values, Invalidate()". Scrolling is handled
// in-control with a slim custom scrollbar instead of the chunky native one.
sealed class DashboardView : Control
{
    // ── layout constants ──────────────────────────────────────────────────────
    const int PadX = 14, PadY = 14, CardGap = 10;
    const int CardPadX = 14, CardPadY = 12;
    const int HeadH = 22, BigH = 34, BarBlockH = 13, RowH = 19;
    const int SparkH = 34, SparkCapH = 16, SparkGap = 8;
    const int ScrollW = 10;

    const int HistoryLen = 60;   // one sample per publish cycle ≈ last 60 s

    MetricsSnapshot? _m;
    readonly Queue<float> _cpuHist = new(), _gpuHist = new(), _ramHist = new(), _netHist = new();

    // scrolling state
    int _offset;
    int _contentH;
    bool _dragging, _thumbHover;
    int _dragStartY, _dragStartOffset;

    public DashboardView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.WinBg;
        TabStop = false;
    }

    public void SetMetrics(MetricsSnapshot m)
    {
        _m = m;
        Push(_cpuHist, m.Cpu?.Load);
        Push(_gpuHist, m.Gpu?.Load);
        Push(_ramHist, m.Ram?.Load);
        Push(_netHist, m.Network?.DownloadKbps);
        Invalidate();
    }

    static void Push(Queue<float> q, float? v)
    {
        q.Enqueue(v ?? 0);
        while (q.Count > HistoryLen) q.Dequeue();
    }

    // ── scrolling ─────────────────────────────────────────────────────────────

    int MaxOffset => Math.Max(0, _contentH - Height);

    protected override void OnMouseEnter(EventArgs e) { Select(); base.OnMouseEnter(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SetOffset(_offset - e.Delta / 120 * 48);
        base.OnMouseWheel(e);
    }

    void SetOffset(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffset);
        if (clamped != _offset) { _offset = clamped; Invalidate(); }
    }

    Rectangle ThumbRect()
    {
        if (MaxOffset == 0) return Rectangle.Empty;
        int trackH = Height - 4;
        int thumbH = Math.Max(30, trackH * Height / _contentH);
        int thumbY = 2 + (trackH - thumbH) * _offset / MaxOffset;
        return new Rectangle(Width - ScrollW + 2, thumbY, ScrollW - 5, thumbH);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var thumb = ThumbRect();
        if (!thumb.IsEmpty && e.X >= Width - ScrollW)
        {
            if (!thumb.Contains(e.Location))
            {
                // click on the track: jump so the thumb centers on the cursor
                int trackH = Height - 4 - thumb.Height;
                if (trackH > 0)
                    SetOffset((e.Y - 2 - thumb.Height / 2) * MaxOffset / trackH);
                thumb = ThumbRect();
            }
            _dragging = true;
            _dragStartY = e.Y;
            _dragStartOffset = _offset;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            var thumb = ThumbRect();
            int trackH = Height - 4 - thumb.Height;
            if (trackH > 0)
                SetOffset(_dragStartOffset + (e.Y - _dragStartY) * MaxOffset / trackH);
        }
        else
        {
            bool hover = e.X >= Width - ScrollW && MaxOffset > 0;
            if (hover != _thumbHover) { _thumbHover = hover; Invalidate(); }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; Invalidate(); }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_thumbHover) { _thumbHover = false; Invalidate(); }
        base.OnMouseLeave(e);
    }

    // ── painting ──────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WinBg);
        if (_m == null) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Scrolling is plain coordinate arithmetic — TextRenderer (GDI) ignores
        // GDI+ transforms, so a TranslateTransform would scroll shapes but not text.
        int w = Width - PadX * 2 - (MaxOffset > 0 ? ScrollW - 2 : 0);
        int x = PadX, y = PadY - _offset;

        if (_m.Cpu != null)
        {
            int h = CardHeight(rows: 3, big: true, spark: true, head: true);
            DrawLoadCard(g, new Rectangle(x, y, w, h), "CPU", _m.Cpu.Name, _m.Cpu.Load, _cpuHist, CpuRows());
            y += h + CardGap;
        }
        if (_m.Gpu != null)
        {
            int h = CardHeight(rows: 4, big: true, spark: true, head: true);
            DrawLoadCard(g, new Rectangle(x, y, w, h), "GPU", _m.Gpu.Name, _m.Gpu.Load, _gpuHist, GpuRows());
            y += h + CardGap;
        }
        if (_m.Ram != null)
        {
            int h = CardHeight(rows: 0, big: true, spark: true, head: true);
            DrawRamCard(g, new Rectangle(x, y, w, h));
            y += h + CardGap;
        }
        if (_m.Drives is { Count: > 0 })
        {
            int h = CardPadY * 2 + HeadH + _m.Drives.Count * (18 + BarBlockH) + (_m.Drives.Count - 1) * 6;
            DrawDrivesCard(g, new Rectangle(x, y, w, h));
            y += h + CardGap;
        }
        if (_m.Network != null || _m.System != null)
        {
            int netH = CardPadY * 2 + HeadH + 2 * RowH + SparkGap + SparkH + SparkCapH;
            int sysH = CardPadY * 2 + HeadH + 3 * RowH;
            int h = Math.Max(netH, sysH);
            int half = (w - CardGap) / 2;
            if (_m.Network != null)
                DrawNetworkCard(g, new Rectangle(x, y, _m.System != null ? half : w, h));
            if (_m.System != null)
                DrawSystemCard(g, new Rectangle(_m.Network != null ? x + half + CardGap : x, y,
                    _m.Network != null ? w - half - CardGap : w, h));
            y += h;
        }

        _contentH = y + _offset + PadY;

        // slim scrollbar, drawn last so it overlays the card edge
        var thumb = ThumbRect();
        if (!thumb.IsEmpty)
        {
            using var brush = new SolidBrush(_dragging || _thumbHover ? Theme.ScrollHover : Theme.Scroll);
            using var path = Theme.RoundedRect(thumb, thumb.Width / 2f);
            g.FillPath(brush, path);
        }

        // offset may exceed the new max after a resize/section change
        if (_offset > MaxOffset) _offset = MaxOffset;
    }

    static int CardHeight(int rows, bool big, bool spark, bool head)
    {
        int h = CardPadY * 2;
        if (head)  h += HeadH;
        if (big)   h += BigH + BarBlockH;
        if (rows > 0) h += rows * RowH + 4;
        if (spark) h += SparkGap + SparkH + SparkCapH;
        return h;
    }

    void DrawCardBg(Graphics g, Rectangle r)
    {
        using var path = Theme.RoundedRect(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), 7);
        using var bg = new SolidBrush(Theme.CardBg);
        using var border = new Pen(Theme.CardBorder);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
    }

    static void DrawHead(Graphics g, Rectangle card, string name, string? sub)
    {
        int x = card.X + CardPadX, y = card.Y + CardPadY;
        TextRenderer.DrawText(g, name, Theme.SemiBold, new Point(x, y), Theme.Fg);
        if (!string.IsNullOrEmpty(sub))
        {
            int nameW = TextRenderer.MeasureText(name, Theme.SemiBold).Width;
            var rect = new Rectangle(x + nameW + 4, y + 1, card.Width - CardPadX * 2 - nameW - 4, 16);
            TextRenderer.DrawText(g, sub, Theme.Small, rect, Theme.Fg3,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    void DrawBar(Graphics g, int x, int y, int w, int pct)
    {
        var track = new RectangleF(x, y, w, 5);
        using (var path = Theme.RoundedRect(track, 2.5f))
        using (var bg = new SolidBrush(Theme.BarTrack))
            g.FillPath(bg, path);
        int fillW = w * Math.Clamp(pct, 0, 100) / 100;
        if (fillW > 5)
        {
            using var path = Theme.RoundedRect(new RectangleF(x, y, fillW, 5), 2.5f);
            using var fg = new SolidBrush(Theme.Accent);
            g.FillPath(fg, path);
        }
    }

    void DrawSpark(Graphics g, Rectangle r, Queue<float> hist, float max)
    {
        if (hist.Count < 2 || max <= 0) return;
        var data = hist.ToArray();
        float stepX = (float)r.Width / (HistoryLen - 1);
        float x0 = r.X + (HistoryLen - data.Length) * stepX;   // right-aligned: newest at the right edge

        var pts = new PointF[data.Length];
        for (int i = 0; i < data.Length; i++)
            pts[i] = new PointF(x0 + i * stepX,
                r.Bottom - Math.Min(data[i], max) / max * (r.Height - 2) - 1);

        var area = new PointF[data.Length + 2];
        area[0] = new PointF(pts[0].X, r.Bottom);
        pts.CopyTo(area, 1);
        area[^1] = new PointF(pts[^1].X, r.Bottom);
        using (var fill = new SolidBrush(Theme.AccentSoft))
            g.FillPolygon(fill, area);

        using (var pen = new Pen(Theme.Accent, 1.4f) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts);

        using (var dot = new SolidBrush(Theme.Accent))
            g.FillEllipse(dot, pts[^1].X - 2.4f, pts[^1].Y - 2.4f, 4.8f, 4.8f);
    }

    static void DrawRow(Graphics g, int x, int y, int labelW, string label, string value, Color valueColor)
    {
        TextRenderer.DrawText(g, label, Theme.Base, new Rectangle(x, y, labelW, RowH), Theme.Fg2,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, value, Theme.Base, new Rectangle(x + labelW, y, 220, RowH), valueColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    void DrawBigPercent(Graphics g, Rectangle card, ref int y, int? pct, string? extra = null)
    {
        int x = card.X + CardPadX;
        string big = pct?.ToString() ?? "—";
        TextRenderer.DrawText(g, big, Theme.BigValue, new Point(x, y), Theme.Fg);
        int bigW = TextRenderer.MeasureText(big, Theme.BigValue).Width;
        string tail = extra != null ? $" %   ·   {extra}" : " %";
        TextRenderer.DrawText(g, tail, Theme.Base, new Point(x + bigW - 2, y + 14), Theme.Fg2);
        y += BigH;
        DrawBar(g, x, y, card.Width - CardPadX * 2, pct ?? 0);
        y += BarBlockH;
    }

    // ── cards ─────────────────────────────────────────────────────────────────

    void DrawLoadCard(Graphics g, Rectangle card, string name, string? sub,
        int? load, Queue<float> hist, (string Label, string Value, Color Color)[] rows)
    {
        DrawCardBg(g, card);
        DrawHead(g, card, name, sub);
        int y = card.Y + CardPadY + HeadH;
        DrawBigPercent(g, card, ref y, load);
        foreach (var (label, value, color) in rows)
        {
            DrawRow(g, card.X + CardPadX, y, 110, label, value, color);
            y += RowH;
        }
        y += 4 + SparkGap;
        DrawSpark(g, new Rectangle(card.X + CardPadX, y, card.Width - CardPadX * 2, SparkH), hist, 100);
        y += SparkH;
        TextRenderer.DrawText(g, "Load, last 60 s", Theme.Tiny, new Point(card.X + CardPadX, y + 2), Theme.Fg3);
    }

    (string, string, Color)[] CpuRows()
    {
        var c = _m!.Cpu!;
        return
        [
            ("Temperature",   Fmt(c.TempC, "°C"),           Theme.TempColor(c.TempC)),
            ("Package Power", Fmt(c.PackagePowerW, "W"),    Theme.Fg),
            ("Core Voltage",  Fmt(c.CoreVoltageV, "V", "0.###"), Theme.Fg),
        ];
    }

    (string, string, Color)[] GpuRows()
    {
        var gpu = _m!.Gpu!;
        string vram = gpu.MemoryUsedMb != null || gpu.MemoryTotalMb != null
            ? $"{FmtGb(gpu.MemoryUsedMb)} / {FmtGb(gpu.MemoryTotalMb)} GB"
            : "—";
        return
        [
            ("Temperature", Fmt(gpu.TempC, "°C"),        Theme.TempColor(gpu.TempC)),
            ("Board Power", Fmt(gpu.BoardPowerW, "W"),   Theme.Fg),
            ("Fan",         Fmt(gpu.FanRpm, "RPM", "0"), Theme.Fg),
            ("VRAM",        vram,                        Theme.Fg),
        ];
    }

    void DrawRamCard(Graphics g, Rectangle card)
    {
        var ram = _m!.Ram!;
        DrawCardBg(g, card);
        DrawHead(g, card, "RAM", null);
        int y = card.Y + CardPadY + HeadH;
        string? extra = ram.UsedGb != null || ram.TotalGb != null
            ? $"{FmtNum(ram.UsedGb)} / {FmtNum(ram.TotalGb)} GB"
            : null;
        DrawBigPercent(g, card, ref y, ram.Load, extra);
        y += SparkGap;
        DrawSpark(g, new Rectangle(card.X + CardPadX, y, card.Width - CardPadX * 2, SparkH), _ramHist, 100);
        y += SparkH;
        TextRenderer.DrawText(g, "Load, last 60 s", Theme.Tiny, new Point(card.X + CardPadX, y + 2), Theme.Fg3);
    }

    void DrawDrivesCard(Graphics g, Rectangle card)
    {
        DrawCardBg(g, card);
        DrawHead(g, card, "Drives", null);
        int x = card.X + CardPadX, w = card.Width - CardPadX * 2;
        int y = card.Y + CardPadY + HeadH;
        foreach (var d in _m!.Drives!)
        {
            TextRenderer.DrawText(g, d.Name.TrimEnd('\\'), Theme.SemiBold,
                new Rectangle(x, y, w / 2, 18), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string right = d.UsedPercent != null && d.UsedGb != null && d.TotalGb != null
                ? $"{d.UsedPercent} % · {FmtNum(d.UsedGb)} / {FmtNum(d.TotalGb)} GB"
                : d.TotalGb != null ? $"{FmtNum(d.TotalGb)} GB" : "";
            TextRenderer.DrawText(g, right, Theme.Base, new Rectangle(x + w / 2, y, w - w / 2, 18), Theme.Fg2,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += 18;
            DrawBar(g, x, y + 3, w, d.UsedPercent ?? 0);
            y += BarBlockH + 6;
        }
    }

    void DrawNetworkCard(Graphics g, Rectangle card)
    {
        var net = _m!.Network!;
        DrawCardBg(g, card);
        DrawHead(g, card, "Network", null);
        int x = card.X + CardPadX, y = card.Y + CardPadY + HeadH;
        DrawRow(g, x, y, 26, "↑", FmtSpeed(net.UploadKbps), Theme.Fg);   y += RowH;
        DrawRow(g, x, y, 26, "↓", FmtSpeed(net.DownloadKbps), Theme.Fg); y += RowH;
        y += SparkGap;
        float max = Math.Max(100, _netHist.Count > 0 ? _netHist.Max() : 0);
        DrawSpark(g, new Rectangle(x, y, card.Width - CardPadX * 2, SparkH), _netHist, max);
        y += SparkH;
        TextRenderer.DrawText(g, "Download, last 60 s", Theme.Tiny, new Point(x, y + 2), Theme.Fg3);
    }

    void DrawSystemCard(Graphics g, Rectangle card)
    {
        DrawCardBg(g, card);
        DrawHead(g, card, "System", null);
        int x = card.X + CardPadX, y = card.Y + CardPadY + HeadH;
        DrawRow(g, x, y, 52, "Uptime", FmtUptime(_m!.System?.UptimeSec), Theme.Fg); y += RowH;
        DrawRow(g, x, y, 52, "Host", _m.Host, Theme.Fg); y += RowH;
        if (!string.IsNullOrEmpty(_m.Motherboard?.Name))
        {
            TextRenderer.DrawText(g, "Board", Theme.Base, new Rectangle(x, y, 52, RowH), Theme.Fg2,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _m.Motherboard.Name, Theme.Small,
                new Rectangle(x + 52, y, card.Width - CardPadX * 2 - 52, RowH), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // ── formatting ────────────────────────────────────────────────────────────

    static string FmtNum(float? v, string fmt = "0.#") =>
        v?.ToString(fmt, CultureInfo.InvariantCulture) ?? "?";

    static string Fmt(float? v, string unit, string fmt = "0.#") =>
        v == null ? "—" : $"{v.Value.ToString(fmt, CultureInfo.InvariantCulture)} {unit}";

    static string FmtGb(float? mb) =>
        mb == null ? "?" : (mb.Value / 1024f).ToString("0.#", CultureInfo.InvariantCulture);

    static string FmtSpeed(float? kbps) =>
        kbps == null ? "—"
        : kbps.Value >= 1024 ? $"{kbps.Value / 1024f:0.##} MB/s"
        : $"{kbps.Value:0.#} KB/s";

    static string FmtUptime(int? sec)
    {
        if (sec == null) return "—";
        var t = TimeSpan.FromSeconds(sec.Value);
        return t.Days  > 0 ? $"{t.Days}d {t.Hours}h {t.Minutes}m"
             : t.Hours > 0 ? $"{t.Hours}h {t.Minutes}m"
             :               $"{t.Minutes}m {t.Seconds}s";
    }
}
