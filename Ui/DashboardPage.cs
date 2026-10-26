using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

// The dashboard: hardware cards in a two-column grid, built from Fluent
// controls plus a drawn sparkline. The card elements are created once
// per layout (which sections exist, how many drives/fans/adapters) and only
// their texts and values change on each snapshot, so open tooltips survive.
sealed class DashboardPage : UserControl
{
    const int HistoryLen = 60;   // one sample per publish cycle, about 60 s

    readonly Queue<float> _cpuHist = new(), _gpuHist = new(), _ramHist = new();
    readonly TextBlock _placeholder = new()
    {
        Text = L.T.StatusStarting, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly List<Action<MetricsSnapshot>> _updates = new();
    string _layout = "";

    public DashboardPage()
    {
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Content = _placeholder;
    }

    public void SetPlaceholder(string text) => _placeholder.Text = text;

    public void SetMetrics(MetricsSnapshot m)
    {
        Push(_cpuHist, m.Cpu?.Load);
        Push(_gpuHist, m.Gpu?.Load);
        Push(_ramHist, m.Ram?.Load);

        string layout = $"{m.Cpu != null}{m.Gpu != null}{m.Ram != null}|{m.Drives?.Count}|{m.Fans?.Count}|{m.Network?.Count}|{m.System != null}";
        if (layout != _layout)
        {
            _layout = layout;
            Rebuild(m);
        }
        foreach (var update in _updates)
            update(m);
    }

    static void Push(Queue<float> q, float? v)
    {
        q.Enqueue(v ?? 0);
        while (q.Count > HistoryLen) q.Dequeue();
    }

    // ── layout ────────────────────────────────────────────────────────────────

    // Cards in fixed order, two per grid row; an odd last card spans both
    // columns. A missing section shifts the following cards up.
    void Rebuild(MetricsSnapshot m)
    {
        _updates.Clear();
        var cards = new List<UIElement>();
        if (m.Cpu != null) cards.Add(LoadCard(L.T.CardCpu, _cpuHist, s =>
        {
            var c = s.Cpu!;
            return (Format.NameStages(c.Name), c.Load,
            [
                (Format.Unit(c.TempC, "°C"), TempBrush(c.TempC)),
                (Format.Unit(c.PackagePowerW, "W", "0"), null),
                (Format.Unit(c.CoreVoltageV, "V", "0.###"), null),
            ]);
        }));
        if (m.Gpu != null) cards.Add(LoadCard(L.T.CardGpu, _gpuHist, s =>
        {
            var g = s.Gpu!;
            string? vram = g.MemoryUsedMb != null || g.MemoryTotalMb != null
                ? $"VRAM {Format.Gb(g.MemoryUsedMb)}/{(g.MemoryTotalMb != null ? MathF.Round(g.MemoryTotalMb.Value / 1024f).ToString(L.Culture) : "?")} GB"
                : null;
            return (Format.NameStages(g.Name), g.Load,
            [
                (Format.Unit(g.TempC, "°C"), TempBrush(g.TempC)),
                (Format.Unit(g.BoardPowerW, "W", "0"), null),
                (vram, null),
            ]);
        }));
        if (m.Ram != null) cards.Add(LoadCard(L.T.CardRam, _ramHist, s =>
        {
            var r = s.Ram!;
            string? info = r.Type != null && r.SpeedMtps != null ? $"{r.Type}-{r.SpeedMtps}"
                : r.Type ?? (r.SpeedMtps != null ? $"{r.SpeedMtps} MT/s" : null);
            string? usage = r.UsedGb != null || r.TotalGb != null
                ? $"{Format.Num(r.UsedGb)} / {Format.Num(r.TotalGb)} GB" : null;
            return (info != null ? new[] { info } : Array.Empty<string>(), r.Load, [(usage, null)]);
        }));
        if (m.Drives is { Count: > 0 }) cards.Add(DrivesCard(m.Drives.Count));
        if (m.Fans is { Count: > 0 }) cards.Add(FansCard(m.Fans.Count));
        if (m.Network is { Count: > 0 }) cards.Add(NetworkCard(m.Network.Count));
        if (m.System != null) cards.Add(SystemCard());

        var grid = new Grid { Margin = new Thickness(16, 16, 16, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < cards.Count; i++)
        {
            int row = i / 2;
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = (FrameworkElement)cards[i];
            card.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, i % 2 == 0 ? 0 : 2);
            if (i % 2 == 0 && i == cards.Count - 1) Grid.SetColumnSpan(card, 3);
            grid.Children.Add(card);
        }
        Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ── cards ─────────────────────────────────────────────────────────────────

    // Card frame with the title line; returns the body panel to fill.
    static Border Card(string title, out StackPanel body, out StagedText info)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var titleText = new TextBlock { Text = title, Margin = new Thickness(0, 0, 6, 0) };
        titleText.Style = (Style)Application.Current.FindResource("CardTitle");
        DockPanel.SetDock(titleText, Dock.Left);
        info = new StagedText { VerticalAlignment = VerticalAlignment.Center };
        info.Text.Style = (Style)Application.Current.FindResource("Sub");
        info.Text.TextAlignment = TextAlignment.Right;
        info.Text.TextWrapping = TextWrapping.NoWrap;
        info.Text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        head.Children.Add(titleText);
        head.Children.Add(info);

        body = new StackPanel();
        body.Children.Add(head);
        var card = new Border { Padding = new Thickness(12, 10, 12, 10), Child = body };
        card.Style = (Style)Application.Current.FindResource("Card");
        return card;
    }

    // CPU / GPU / RAM: big percent, bar, one detail line, 60 s sparkline.
    UIElement LoadCard(string title, Queue<float> hist,
        Func<MetricsSnapshot, (IReadOnlyList<string> Info, int? Pct, (string? Text, string? Brush)[] Detail)> read)
    {
        var card = Card(title, out var body, out var info);

        var big = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold };
        var bigValue = new Run();
        var pctSign = new Run("%") { FontSize = 12, FontWeight = FontWeights.Normal };
        pctSign.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        big.Inlines.Add(bigValue);
        big.Inlines.Add(pctSign);
        var bar = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 2, 0, 8) };
        var detail = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        TipWhenTrimmed(detail);
        var spark = new Sparkline(hist) { Height = 20, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var e in new UIElement[] { big, bar, detail, spark }) body.Children.Add(e);

        _updates.Add(m =>
        {
            var v = read(m);
            info.SetStages(v.Info);
            bigValue.Text = v.Pct?.ToString() ?? "—";
            bar.Value = Math.Clamp(v.Pct ?? 0, 0, 100);
            SetSegments(detail, v.Detail);
            spark.InvalidateVisual();
        });
        return card;
    }

    UIElement DrivesCard(int count)
    {
        var card = Card(L.T.CardDrives, out var body, out _);
        var rows = new List<(TextBlock Letter, TextBlock Value, ProgressBar Bar, FrameworkElement Row)>();
        for (int i = 0; i < count; i++)
        {
            var letter = new TextBlock { FontWeight = FontWeights.SemiBold };
            var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var line = new DockPanel();
            DockPanel.SetDock(letter, Dock.Left);
            line.Children.Add(letter);
            line.Children.Add(value);
            var bar = new ProgressBar { Maximum = 100, Margin = new Thickness(0, 2, 0, 0) };
            var row = new StackPanel { Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0), Background = Brushes.Transparent };
            row.Children.Add(line);
            row.Children.Add(bar);
            body.Children.Add(row);
            rows.Add((letter, value, bar, row));
        }
        _updates.Add(m =>
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var d = m.Drives![i];
                var name = d.Name.TrimEnd('\\');
                int paren = name.LastIndexOf('(');
                rows[i].Letter.Text = paren >= 0 ? name[paren..].Trim('(', ')') : name;
                var parts = new List<string>();
                if (d.UsedPercent != null) parts.Add($"{d.UsedPercent} %");
                if (d.FreeGb != null) parts.Add($"{Format.Size(d.FreeGb.Value)} {L.T.DriveFree}");
                rows[i].Value.Text = parts.Count > 0 ? string.Join(" · ", parts) : "—";
                rows[i].Bar.Value = Math.Clamp(d.UsedPercent ?? 0, 0, 100);
                // Volume name, type and GB only in the tooltip.
                var details = new List<string> { name };
                if (d.Type != null) details.Add(d.Type);
                if (d.UsedGb != null && d.TotalGb != null) details.Add($"{Format.Num(d.UsedGb)} / {Format.Num(d.TotalGb)} GB");
                if (d.UsedPercent != null) details.Add($"{d.UsedPercent} %");
                rows[i].Row.ToolTip = string.Join(" · ", details);
            }
        });
        return card;
    }

    // One row per fan: name left (ellipsized), "861 RPM · 46 %" right.
    UIElement FansCard(int count)
    {
        var card = Card(L.T.CardFans, out var body, out _);
        var rows = new List<(TextBlock Name, TextBlock Value)>();
        for (int i = 0; i < count; i++)
        {
            var value = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var name = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            TipWhenTrimmed(name);
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(value, Dock.Right);
            line.Children.Add(value);
            line.Children.Add(name);
            body.Children.Add(line);
            rows.Add((name, value));
        }
        _updates.Add(m =>
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var f = m.Fans![i];
                var parts = new List<string>();
                if (f.Rpm != null) parts.Add($"{Format.Num(f.Rpm, "0")} RPM");
                if (f.Pwm != null) parts.Add($"{Format.Num(f.Pwm, "0")} %");
                rows[i].Name.Text = f.Name;
                rows[i].Value.Text = parts.Count > 0 ? string.Join(" · ", parts) : "—";
            }
        });
        return card;
    }

    // Per adapter: name + IP, then the up/down rates on their own line.
    UIElement NetworkCard(int count)
    {
        var card = Card(L.T.CardNetwork, out var body, out _);
        var rows = new List<(TextBlock Name, TextBlock Ip, TextBlock Rates, FrameworkElement Head)>();
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                var sep = new Border { Margin = new Thickness(0, 6, 0, 6) };
                sep.Style = (Style)Application.Current.FindResource("RowSep");
                body.Children.Add(sep);
            }
            var name = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            var ip = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var head = new DockPanel { Background = Brushes.Transparent };
            DockPanel.SetDock(ip, Dock.Right);
            head.Children.Add(ip);
            head.Children.Add(name);
            var rates = new TextBlock { FontSize = 12 };
            rates.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            body.Children.Add(head);
            body.Children.Add(rates);
            rows.Add((name, ip, rates, head));
        }
        _updates.Add(m =>
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var a = m.Network![i];
                rows[i].Name.Text = a.Name;
                rows[i].Ip.Text = a.IpAddress ?? "—";
                rows[i].Rates.Text = $"↑ {Format.Speed(a.UploadKbps)}   ↓ {Format.Speed(a.DownloadKbps)}";
                rows[i].Head.ToolTip = a.Mac != null ? $"{a.Name} · MAC {a.Mac}" : a.Name;
            }
        });
        return card;
    }

    UIElement SystemCard()
    {
        var card = Card(L.T.CardSystem, out var body, out _);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        string[] labels = [L.T.RowUptime, L.T.RowHost, L.T.RowOs, L.T.RowBoard];
        var values = new TextBlock[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = labels[i], Margin = new Thickness(0, 2, 12, 0) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            values[i] = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            TipWhenTrimmed(values[i]);
            Grid.SetRow(label, i);
            Grid.SetRow(values[i], i);
            Grid.SetColumn(values[i], 1);
            grid.Children.Add(label);
            grid.Children.Add(values[i]);
        }
        body.Children.Add(grid);
        _updates.Add(m =>
        {
            values[0].Text = Format.Uptime(m.System?.UptimeSec);
            values[1].Text = m.Host;
            values[2].Text = m.System?.OsVersion ?? "—";
            values[3].Text = m.Motherboard?.Name ?? "—";
        });
        return card;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Fluent status brushes: green below 60 °C, yellow below 80 °C, then red.
    static string? TempBrush(float? c) =>
        c == null ? null : c < 60 ? "SystemFillColorSuccessBrush" : c < 80 ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush";

    // "a · b · c" from the non-null parts; brush null means secondary text.
    static void SetSegments(TextBlock tb, (string? Text, string? Brush)[] parts)
    {
        tb.Inlines.Clear();
        foreach (var (text, brush) in parts)
        {
            if (text == null) continue;
            if (tb.Inlines.Count > 0) tb.Inlines.Add(Secondary(" · ", "TextFillColorTertiaryBrush"));
            tb.Inlines.Add(Secondary(text, brush ?? "TextFillColorSecondaryBrush"));
        }
        if (tb.Inlines.Count == 0) tb.Inlines.Add(Secondary("—", "TextFillColorTertiaryBrush"));

        static Run Secondary(string text, string brush)
        {
            var run = new Run(text);
            run.SetResourceReference(TextElement.ForegroundProperty, brush);
            return run;
        }
    }

    // Shows the full text as a tooltip, but only while it is cut off.
    static void TipWhenTrimmed(TextBlock tb)
    {
        tb.ToolTip = new ToolTip();
        tb.ToolTipOpening += (_, e) =>
        {
            string text = new TextRange(tb.ContentStart, tb.ContentEnd).Text;
            if (StagedText.TextWidth(tb, text) <= tb.ActualWidth + 0.5) { e.Handled = true; return; }
            ((ToolTip)tb.ToolTip).Content = text;
        };
    }
}

// Right-aligned info text that picks the longest of several shortening
// stages that fits (see Format.NameStages); the tooltip shows the full
// name once a shorter stage or an ellipsis is in use.
sealed class StagedText : Decorator
{
    public readonly TextBlock Text = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    IReadOnlyList<string> _stages = [];

    public StagedText() => Child = Text;

    public void SetStages(IReadOnlyList<string> stages)
    {
        if (stages.SequenceEqual(_stages)) return;
        _stages = stages;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (_stages.Count == 0) Text.Text = "";
        else
        {
            string pick = _stages.FirstOrDefault(s => TextWidth(Text, s) <= constraint.Width) ?? _stages[^1];
            Text.Text = pick;
            ToolTip = pick != _stages[0] || TextWidth(Text, pick) > constraint.Width ? _stages[0] : null;
        }
        return base.MeasureOverride(constraint);
    }

    internal static double TextWidth(TextBlock tb, string text) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tb).PixelsPerDip).Width;
}

// 60-sample load history: accent line with a soft fill below, newest value at
// the right edge marked with a dot.
sealed class Sparkline(Queue<float> data) : FrameworkElement
{
    const int HistoryLen = 60;

    protected override void OnRender(DrawingContext dc)
    {
        if (data.Count < 2 || ActualWidth <= 0) return;
        var accent = TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush ?? Brushes.DodgerBlue;
        var values = data.ToArray();
        double stepX = ActualWidth / (HistoryLen - 1);
        double x0 = (HistoryLen - values.Length) * stepX;
        double h = ActualHeight;
        Point P(int i) => new(x0 + i * stepX, h - 1 - Math.Min(values[i], 100f) / 100f * (h - 2));

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(0), false, false);
            for (int i = 1; i < values.Length; i++) ctx.LineTo(P(i), true, true);
        }
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(P(0).X, h), true, true);
            for (int i = 0; i < values.Length; i++) ctx.LineTo(P(i), false, true);
            ctx.LineTo(new Point(P(values.Length - 1).X, h), false, true);
        }
        var soft = new SolidColorBrush(accent.Color) { Opacity = 0.18 };
        dc.DrawGeometry(soft, null, area);
        dc.DrawGeometry(null, new Pen(accent, 1.4) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawEllipse(accent, null, P(values.Length - 1), 2.4, 2.4);
    }
}
