using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// The dashboard, compact layout (approved mockup, 2026-08-04): all groups as
// half-width cards in a two-column grid — CPU+GPU, RAM+Drives, Network+System —
// so the whole dashboard fits without scrolling. Each card: title, big value,
// slim bar, ONE compact detail line, 20px mini sparkline. Owner-drawn: a
// refresh is "copy values, Invalidate()". Scrolling (only needed with many
// drives) uses the slim custom scrollbar.
sealed class DashboardView : Control
{
    // ── layout constants (design values in 96-DPI pixels, scaled once) ────────
    static readonly int PadX = Theme.S(14), PadY = Theme.S(14), CardGap = Theme.S(10);
    static readonly int CardPadX = Theme.S(12), CardPadY = Theme.S(10);
    static readonly int HeadH = Theme.S(20), BigH = Theme.S(28), BarBlockH = Theme.S(12),
                        RowH = Theme.S(19), DetailH = Theme.S(18);
    static readonly int SparkH = Theme.S(20), SparkGap = Theme.S(6);
    static readonly int ScrollW = Theme.S(10);

    const int HistoryLen = 60;   // one sample per publish cycle ≈ last 60 s

    MetricsSnapshot? _m;
    readonly Queue<float> _cpuHist = new(), _gpuHist = new(), _ramHist = new();

    // Hover tooltips for truncated/condensed values (full OS/board strings,
    // drive details). Zones are rebuilt on every paint in client coordinates.
    readonly ToolTip _tip = new();
    readonly List<(Rectangle Rect, string Text)> _tipZones = new();
    string? _tipText;

    // Shown centered while no snapshot has arrived yet (startup phases).
    string _placeholder = L.T.StatusStarting;

    public void SetPlaceholder(string text)
    {
        _placeholder = text;
        if (_m == null) Invalidate();
    }

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
        SetOffset(_offset - e.Delta / 120 * Theme.S(48));
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
        int trackH = Height - Theme.S(4);
        int thumbH = Math.Max(Theme.S(30), trackH * Height / _contentH);
        int thumbY = Theme.S(2) + (trackH - thumbH) * _offset / MaxOffset;
        return new Rectangle(Width - ScrollW + Theme.S(2), thumbY, ScrollW - Theme.S(5), thumbH);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var thumb = ThumbRect();
        if (!thumb.IsEmpty && e.X >= Width - ScrollW)
        {
            if (!thumb.Contains(e.Location))
            {
                // click on the track: jump so the thumb centers on the cursor
                int trackH = Height - Theme.S(4) - thumb.Height;
                if (trackH > 0)
                    SetOffset((e.Y - Theme.S(2) - thumb.Height / 2) * MaxOffset / trackH);
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
            int trackH = Height - Theme.S(4) - thumb.Height;
            if (trackH > 0)
                SetOffset(_dragStartOffset + (e.Y - _dragStartY) * MaxOffset / trackH);
        }
        else
        {
            bool hover = e.X >= Width - ScrollW && MaxOffset > 0;
            if (hover != _thumbHover) { _thumbHover = hover; Invalidate(); }
            UpdateTip(e.Location);
        }
        base.OnMouseMove(e);
    }

    void UpdateTip(Point p)
    {
        string? text = null;
        foreach (var zone in _tipZones)
            if (zone.Rect.Contains(p)) { text = zone.Text; break; }
        if (text == _tipText) return;
        _tipText = text;
        if (text == null) _tip.Hide(this);
        else _tip.Show(text, this, p.X + Theme.S(14), p.Y + Theme.S(20), 5000);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; Invalidate(); }
        base.OnMouseUp(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_thumbHover) { _thumbHover = false; Invalidate(); }
        if (_tipText != null) { _tipText = null; _tip.Hide(this); }
        base.OnMouseLeave(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }

    // ── painting ──────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.WinBg);
        _tipZones.Clear();
        if (_m == null)
        {
            // No snapshot yet — show the startup phase instead of a blank page.
            TextRenderer.DrawText(g, _placeholder, Theme.Base,
                new Rectangle(0, 0, Width, Height), Theme.Fg2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Scrolling is plain coordinate arithmetic — TextRenderer (GDI) ignores
        // GDI+ transforms, so a TranslateTransform would scroll shapes but not text.
        int w = Width - PadX * 2 - (MaxOffset > 0 ? ScrollW - Theme.S(2) : 0);
        int half = (w - CardGap) / 2;

        // Present cards in fixed order, paired two per grid row. A missing
        // section (no GPU, no drives) just shifts the following cards up.
        var cards = new List<(int H, Action<Graphics, Rectangle> Draw)>();
        if (_m.Cpu != null)            cards.Add((LoadCardHeight, DrawCpuCard));
        if (_m.Gpu != null)            cards.Add((LoadCardHeight, DrawGpuCard));
        if (_m.Ram != null)            cards.Add((LoadCardHeight, DrawRamCard));
        if (_m.Drives is { Count: > 0 }) cards.Add((DrivesHeight(_m.Drives.Count), DrawDrivesCard));
        if (_m.Network is { Count: > 0 }) cards.Add((NetworkHeight(_m.Network.Count), DrawNetworkCard));
        if (_m.System != null)         cards.Add((SystemHeight, DrawSystemCard));

        int x = PadX, y = PadY - _offset;
        for (int i = 0; i < cards.Count; i += 2)
        {
            bool pair = i + 1 < cards.Count;
            int rowH = pair ? Math.Max(cards[i].H, cards[i + 1].H) : cards[i].H;
            cards[i].Draw(g, new Rectangle(x, y, pair ? half : w, rowH));
            if (pair)
                cards[i + 1].Draw(g, new Rectangle(x + half + CardGap, y, w - half - CardGap, rowH));
            y += rowH + CardGap;
        }

        _contentH = y - CardGap + _offset + PadY;

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

    static int LoadCardHeight =>
        CardPadY * 2 + HeadH + BigH + BarBlockH + DetailH + SparkGap + SparkH;

    static int DrivesHeight(int drives) =>
        CardPadY * 2 + HeadH + drives * Theme.S(27) - Theme.S(6);

    // Per adapter: header (name + IP) 17, rates line 16; 9px between blocks.
    static int NetworkHeight(int adapters) =>
        CardPadY * 2 + HeadH + adapters * Theme.S(33) + (adapters - 1) * Theme.S(9);

    static int SystemHeight =>
        CardPadY * 2 + HeadH + 4 * RowH;

    void DrawCardBg(Graphics g, Rectangle r)
    {
        using var path = Theme.RoundedRect(new RectangleF(r.X + .5f, r.Y + .5f, r.Width - 1, r.Height - 1), Theme.SF(7));
        using var bg = new SolidBrush(Theme.CardBg);
        using var border = new Pen(Theme.CardBorder);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
    }

    static void DrawHead(Graphics g, Rectangle card, string name)
        => TextRenderer.DrawText(g, name, Theme.SemiBold,
            new Point(card.X + CardPadX, card.Y + CardPadY), Theme.Fg);

    void DrawBar(Graphics g, int x, int y, int w, int pct)
    {
        float barH = Theme.SF(5);
        var track = new RectangleF(x, y, w, barH);
        using (var path = Theme.RoundedRect(track, barH / 2))
        using (var bg = new SolidBrush(Theme.BarTrack))
            g.FillPath(bg, path);
        int fillW = w * Math.Clamp(pct, 0, 100) / 100;
        if (fillW > barH)
        {
            using var path = Theme.RoundedRect(new RectangleF(x, y, fillW, barH), barH / 2);
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
                r.Bottom - Math.Min(data[i], max) / max * (r.Height - Theme.SF(2)) - Theme.SF(1));

        var area = new PointF[data.Length + 2];
        area[0] = new PointF(pts[0].X, r.Bottom);
        pts.CopyTo(area, 1);
        area[^1] = new PointF(pts[^1].X, r.Bottom);
        using (var fill = new SolidBrush(Theme.AccentSoft))
            g.FillPolygon(fill, area);

        using (var pen = new Pen(Theme.Accent, Theme.SF(1.4f)) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts);

        using (var dot = new SolidBrush(Theme.Accent))
            g.FillEllipse(dot, pts[^1].X - Theme.SF(2.4f), pts[^1].Y - Theme.SF(2.4f), Theme.SF(4.8f), Theme.SF(4.8f));
    }

    // Registers a hover zone when the value doesn't fit its column — the
    // tooltip then shows the full string.
    void AddTipIfTruncated(int x, int y, int w, string? text, Font font)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TextRenderer.MeasureText(text, font).Width <= w) return;
        _tipZones.Add((new Rectangle(x, y, w, RowH), text));
    }

    static void DrawRow(Graphics g, int x, int y, int labelW, string label, string value, int valueW)
    {
        TextRenderer.DrawText(g, label, Theme.Base, new Rectangle(x, y, labelW, RowH), Theme.Fg2,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, value, Theme.Base, new Rectangle(x + labelW, y, valueW, RowH), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // The ONE detail line per card: colored segments drawn back to back
    // (NoPadding on both draw and measure so the pieces join seamlessly).
    // Clipped at maxW — an overflowing segment gets an ellipsis and the full
    // line moves into a hover tooltip instead of bleeding over the card edge.
    void DrawDetail(Graphics g, int x, int y, int maxW, List<(string Text, Color Color)> parts)
    {
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        int right = x + maxW, cx = x;
        bool truncated = false;
        foreach (var (text, color) in parts)
        {
            int w = TextRenderer.MeasureText(g, text, Theme.Small, Size.Empty, flags).Width;
            if (cx + w > right)
            {
                TextRenderer.DrawText(g, text, Theme.Small, new Rectangle(cx, y, right - cx, DetailH), color,
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                truncated = true;
                break;
            }
            TextRenderer.DrawText(g, text, Theme.Small, new Point(cx, y), color, flags);
            cx += w;
        }
        if (truncated)
            _tipZones.Add((new Rectangle(x, y, maxW, DetailH), string.Concat(parts.Select(p => p.Text))));
    }

    // Builds "a · b · c" segments, skipping missing values.
    static List<(string, Color)> Segments(params (string? Text, Color Color)[] parts)
    {
        var result = new List<(string, Color)>();
        foreach (var (text, color) in parts)
        {
            if (text == null) continue;
            if (result.Count > 0) result.Add((" · ", Theme.Fg3));
            result.Add((text, color));
        }
        if (result.Count == 0) result.Add(("—", Theme.Fg3));
        return result;
    }

    void DrawBigPercent(Graphics g, Rectangle card, ref int y, int? pct)
    {
        int x = card.X + CardPadX;
        string big = pct?.ToString() ?? "—";
        TextRenderer.DrawText(g, big, Theme.BigCompact, new Point(x, y), Theme.Fg);
        int bigW = TextRenderer.MeasureText(big, Theme.BigCompact).Width;
        TextRenderer.DrawText(g, "%", Theme.Small, new Point(x + bigW - Theme.S(3), y + Theme.S(10)), Theme.Fg2);
        y += BigH;
        DrawBar(g, x, y, card.Width - CardPadX * 2, pct ?? 0);
        y += BarBlockH;
    }

    void DrawMiniSpark(Graphics g, Rectangle card, int y, Queue<float> hist, float max)
        => DrawSpark(g, new Rectangle(card.X + CardPadX, y, card.Width - CardPadX * 2, SparkH), hist, max);

    // ── cards ─────────────────────────────────────────────────────────────────

    void DrawCpuCard(Graphics g, Rectangle card)
    {
        var c = _m!.Cpu!;
        DrawCardBg(g, card);
        DrawHead(g, card, L.T.CardCpu);
        int y = card.Y + CardPadY + HeadH;
        DrawBigPercent(g, card, ref y, c.Load);
        // Whole watts — the decimal adds width, not information.
        DrawDetail(g, card.X + CardPadX, y, card.Width - CardPadX * 2, Segments(
            (Fmt(c.TempC, "°C"), Theme.TempColor(c.TempC)),
            (Fmt(c.PackagePowerW, "W", "0"), Theme.Fg2),
            (Fmt(c.CoreVoltageV, "V", "0.###"), Theme.Fg2)));
        y += DetailH + SparkGap;
        DrawMiniSpark(g, card, y, _cpuHist, 100);
    }

    void DrawGpuCard(Graphics g, Rectangle card)
    {
        var gpu = _m!.Gpu!;
        DrawCardBg(g, card);
        DrawHead(g, card, L.T.CardGpu);
        int y = card.Y + CardPadY + HeadH;
        DrawBigPercent(g, card, ref y, gpu.Load);
        // Per the approved compact design the fan speed is dropped here —
        // idle GPUs report 0 RPM, lowest information value of all details.
        // Total VRAM in whole GB: "8.3/12 GB" — the card size's decimal only
        // eats width the line doesn't have.
        string? vram = gpu.MemoryUsedMb != null || gpu.MemoryTotalMb != null
            ? $"VRAM {FmtGb(gpu.MemoryUsedMb)}/{(gpu.MemoryTotalMb != null ? MathF.Round(gpu.MemoryTotalMb.Value / 1024f).ToString(L.Culture) : "?")} GB"
            : null;
        DrawDetail(g, card.X + CardPadX, y, card.Width - CardPadX * 2, Segments(
            (Fmt(gpu.TempC, "°C"), Theme.TempColor(gpu.TempC)),
            (Fmt(gpu.BoardPowerW, "W", "0"), Theme.Fg2),
            (vram, Theme.Fg2)));
        y += DetailH + SparkGap;
        DrawMiniSpark(g, card, y, _gpuHist, 100);
    }

    void DrawRamCard(Graphics g, Rectangle card)
    {
        var ram = _m!.Ram!;
        DrawCardBg(g, card);
        DrawHead(g, card, L.T.CardRam);
        int y = card.Y + CardPadY + HeadH;
        DrawBigPercent(g, card, ref y, ram.Load);
        string? usage = ram.UsedGb != null || ram.TotalGb != null
            ? $"{FmtNum(ram.UsedGb)} / {FmtNum(ram.TotalGb)} GB"
            : null;
        DrawDetail(g, card.X + CardPadX, y, card.Width - CardPadX * 2, Segments((usage, Theme.Fg2)));
        y += DetailH + SparkGap;
        DrawMiniSpark(g, card, y, _ramHist, 100);
    }

    void DrawDrivesCard(Graphics g, Rectangle card)
    {
        DrawCardBg(g, card);
        DrawHead(g, card, L.T.CardDrives);
        int x = card.X + CardPadX, w = card.Width - CardPadX * 2;
        int y = card.Y + CardPadY + HeadH;
        foreach (var d in _m!.Drives!)
        {
            // Compact: drive letter + percent only; names and GB live in the tooltip-free
            // MQTT payload and were dropped from the card per the approved design.
            var letter = d.Name.TrimEnd('\\');
            int paren = letter.LastIndexOf('(');
            if (paren >= 0) letter = letter[paren..].Trim('(', ')');
            int letterW = Theme.S(40), lineH = Theme.S(16);
            TextRenderer.DrawText(g, letter, Theme.SemiBold, new Rectangle(x, y, letterW, lineH), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            // "82 % · 170 GB free" — the free space matters most day to day;
            // used/total live in the hover tooltip.
            var rightParts = new List<string>();
            if (d.UsedPercent != null) rightParts.Add($"{d.UsedPercent} %");
            if (d.FreeGb != null) rightParts.Add($"{FmtNum(d.FreeGb, "0")} GB {L.T.DriveFree}");
            TextRenderer.DrawText(g, rightParts.Count > 0 ? string.Join(" · ", rightParts) : "—", Theme.Small,
                new Rectangle(x + letterW, y, w - letterW, lineH), Theme.Fg2,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            // The compact card drops volume names and GB — hovering the row
            // reveals them ("System (C:) · 245.1 / 389.4 GB · 61 %").
            var details = new List<string> { d.Name.TrimEnd('\\') };
            if (d.UsedGb != null && d.TotalGb != null)
                details.Add($"{FmtNum(d.UsedGb)} / {FmtNum(d.TotalGb)} GB");
            if (d.UsedPercent != null)
                details.Add($"{d.UsedPercent} %");
            _tipZones.Add((new Rectangle(x, y, w, Theme.S(27)), string.Join(" · ", details)));
            y += lineH;
            DrawBar(g, x, y + Theme.S(2), w, d.UsedPercent ?? 0);
            y += Theme.S(11);
        }
    }

    // Per adapter, two lines: name bold left + IP right-aligned, then the
    // ↑/↓ rates on their own FULL-width line (a shared line kept clipping the
    // upload part at high rates). The MAC is not drawn — it lives in the
    // hover tooltip of the name line and on MQTT.
    void DrawNetworkCard(Graphics g, Rectangle card)
    {
        var net = _m!.Network!;
        DrawCardBg(g, card);
        DrawHead(g, card, L.T.CardNetwork);
        int x = card.X + CardPadX, w = card.Width - CardPadX * 2;
        int y = card.Y + CardPadY + HeadH;
        for (int i = 0; i < net.Count; i++)
        {
            var a = net[i];
            if (i > 0)
            {
                using var pen = new Pen(Theme.CardBorder);
                g.DrawLine(pen, x, y - Theme.S(5), x + w, y - Theme.S(5));
            }
            int headLineH = Theme.S(17), rateLineH = Theme.S(16);
            int nameW = Math.Min(TextRenderer.MeasureText(a.Name, Theme.SemiBold).Width + Theme.S(4), w / 2);
            TextRenderer.DrawText(g, a.Name, Theme.SemiBold, new Rectangle(x, y, nameW, headLineH), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, a.IpAddress ?? "—", Theme.Small, new Rectangle(x + nameW, y, w - nameW, headLineH), Theme.Fg,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var tip = new List<string> { a.Name };
            if (a.Mac != null) tip.Add($"MAC {a.Mac}");
            _tipZones.Add((new Rectangle(x, y, w, headLineH), string.Join(" · ", tip)));
            y += headLineH;
            string rates = $"↑ {FmtSpeed(a.UploadKbps)}   ↓ {FmtSpeed(a.DownloadKbps)}";
            TextRenderer.DrawText(g, rates, Theme.Small, new Rectangle(x, y, w, rateLineH), Theme.Fg2,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            y += rateLineH + Theme.S(9);
        }
    }

    void DrawSystemCard(Graphics g, Rectangle card)
    {
        DrawCardBg(g, card);
        DrawHead(g, card, L.T.CardSystem);
        int x = card.X + CardPadX, y = card.Y + CardPadY + HeadH;
        // Label column sized to the longest localized label of this card.
        int labelW = new[] { L.T.RowUptime, L.T.RowHost, L.T.RowOs, L.T.RowBoard }
            .Max(l => TextRenderer.MeasureText(l, Theme.Base).Width) + Theme.S(8);
        int valueW = card.Width - CardPadX * 2 - labelW;
        DrawRow(g, x, y, labelW, L.T.RowUptime, FmtUptime(_m!.System?.UptimeSec), valueW); y += RowH;
        DrawRow(g, x, y, labelW, L.T.RowHost, _m.Host, valueW);
        AddTipIfTruncated(x + labelW, y, valueW, _m.Host, Theme.Base); y += RowH;
        DrawRow(g, x, y, labelW, L.T.RowOs, _m.System?.OsVersion ?? "—", valueW);
        AddTipIfTruncated(x + labelW, y, valueW, _m.System?.OsVersion, Theme.Base); y += RowH;
        DrawRow(g, x, y, labelW, L.T.RowBoard, _m.Motherboard?.Name ?? "—", valueW);
        AddTipIfTruncated(x + labelW, y, valueW, _m.Motherboard?.Name, Theme.Base);
    }

    // ── formatting ────────────────────────────────────────────────────────────

    // UI numbers follow the app language ("72,6 °C" in German); MQTT/JSON
    // stays invariant — the two paths are fully separate.
    static string FmtNum(float? v, string fmt = "0.#") =>
        v?.ToString(fmt, L.Culture) ?? "?";

    static string? Fmt(float? v, string unit, string fmt = "0.#") =>
        v == null ? null : $"{v.Value.ToString(fmt, L.Culture)} {unit}";

    static string FmtGb(float? mb) =>
        mb == null ? "?" : (mb.Value / 1024f).ToString("0.#", L.Culture);

    static string FmtSpeed(float? kbps) =>
        kbps == null ? "—"
        : kbps.Value >= 1024 ? $"{(kbps.Value / 1024f).ToString("0.##", L.Culture)} MB/s"
        : $"{kbps.Value.ToString("0.#", L.Culture)} KB/s";

    static string FmtUptime(int? sec)
    {
        if (sec == null) return "—";
        var t = TimeSpan.FromSeconds(sec.Value);
        return t.Days  > 0 ? $"{t.Days}d {t.Hours}h {t.Minutes}m"
             : t.Hours > 0 ? $"{t.Hours}h {t.Minutes}m"
             :               $"{t.Minutes}m {t.Seconds}s";
    }
}
