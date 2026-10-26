using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// The Outputs page: one collapsible card per sink (MQTT / UDP / TCP) with an
// enable switch and a live status line in the header. Edits mark the page
// dirty; MainWindow's floating save panel persists them and reloads the sinks.
sealed class OutputsPage : Panel
{
    readonly AppConfig _config;
    readonly Action _markDirty;

    readonly OutputCard _mqttCard, _udpCard, _tcpCard, _serialCard;

    // MQTT fields
    readonly TextBox _host, _username, _password, _topic, _port;
    readonly FlatCheck _useTls, _haDiscovery;
    // UDP fields
    readonly TextBox _udpHost, _udpPort;
    // TCP fields
    readonly TextBox _tcpPort;
    // Serial fields
    readonly TextBox _serialPort;
    readonly TextBox _serialBaud;

    // Last state each running sink reported, keyed by sink name ("MQTT", …).
    readonly Dictionary<string, (StatusKind Kind, string Text)> _reported = new();

    // Checked fields: the card status shows Problem while the field is invalid.
    readonly (OutputCard Card, TextBox Box, string Problem)[] _checks;

    // manual scrolling (slim overlay rail, no native scrollbar)
    readonly Label _title;
    ScrollRail? _rail;
    int _scroll, _contentH;

    public OutputsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        _markDirty = markDirty;
        BackColor = Theme.WinBg;
        // With every card expanded the content exceeds the window. Scrolling is
        // manual with a slim overlay rail in the right margin (matching the
        // dashboard) — the native scrollbar would shrink the client area and
        // shift every card to the left.
        SetStyle(ControlStyles.Selectable, true);
        MouseEnter += (_, _) => Select();

        _title = new Label
        {
            Text = L.T.OutputsTitle, Font = Theme.Title, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(Theme.S(16), Theme.S(12))
        };
        Controls.Add(_title);

        // ── MQTT ────────────────────────────────────────────────────────────
        _mqttCard = new OutputCard("MQTT", markDirty) { Open = true };
        _host     = _mqttCard.AddTextRow(L.T.FieldHost, config.Mqtt.Host);
        _port     = _mqttCard.AddPortRow(L.T.FieldPort, config.Mqtt.Port);
        _username = _mqttCard.AddTextRow(L.T.FieldUsername, config.Mqtt.Username);
        _password = _mqttCard.AddTextRow(L.T.FieldPassword, config.Mqtt.Password, password: true);
        _topic    = _mqttCard.AddTextRow(L.T.FieldTopicRoot, config.Mqtt.TopicRoot);
        _useTls      = _mqttCard.AddCheckRow(L.T.FieldUseTls, config.Mqtt.UseTls);
        _haDiscovery = _mqttCard.AddCheckRow(L.T.FieldDiscovery, config.Mqtt.HaDiscoveryEnabled);
        _mqttCard.Toggle.SetChecked(config.Mqtt.Enabled);

        // ── UDP ─────────────────────────────────────────────────────────────
        _udpCard = new OutputCard("UDP", markDirty);
        _udpHost = _udpCard.AddTextRow(L.T.FieldHost, config.Udp.Host);
        _udpPort = _udpCard.AddPortRow(L.T.FieldPort, config.Udp.Port);
        _udpCard.Toggle.SetChecked(config.Udp.Enabled);

        // ── TCP ─────────────────────────────────────────────────────────────
        _tcpCard = new OutputCard("TCP", markDirty);
        _tcpPort = _tcpCard.AddPortRow(L.T.FieldListenPort, config.Tcp.ListenPort);
        _tcpCard.Toggle.SetChecked(config.Tcp.Enabled);

        // ── Serial ──────────────────────────────────────────────────────────
        _serialCard = new OutputCard("Serial", markDirty);
        // Enumerated on every open — USB adapters come and go.
        _serialPort = _serialCard.AddSuggestRow(L.T.FieldPort, config.Serial.Port, AvailableComPorts);
        _serialBaud = _serialCard.AddPortRow(L.T.FieldBaudRate, config.Serial.Baud);
        _serialCard.Toggle.SetChecked(config.Serial.Enabled);

        // Input rules, checked live while typing.
        InputBox.Of(_host).Rule = InputBox.Of(_udpHost).Rule = ValidHost;
        InputBox.Of(_port).Rule = InputBox.Of(_udpPort).Rule = InputBox.Of(_tcpPort).Rule = ValidPort;
        InputBox.Of(_serialBaud).Rule = ValidBaud;
        _checks =
        [
            (_mqttCard, _host, L.T.OutInvalidIp),   (_mqttCard, _port, L.T.OutInvalidPort),
            (_udpCard, _udpHost, L.T.OutInvalidIp), (_udpCard, _udpPort, L.T.OutInvalidPort),
            (_tcpCard, _tcpPort, L.T.OutInvalidPort),
            (_serialCard, _serialBaud, L.T.OutInvalidBaud),
        ];
        foreach (var check in _checks)
            check.Box.TextChanged += (_, _) => RefreshStatus();
        _serialPort.TextChanged += (_, _) => RefreshStatus();

        foreach (var card in new[] { _mqttCard, _udpCard, _tcpCard, _serialCard })
        {
            card.Toggle.CheckedChanged += (_, _) => { markDirty(); RefreshStatus(); };
            card.HeightChanged = Relayout;
            Controls.Add(card);
        }

        _rail = new ScrollRail(this);
        Controls.Add(_rail);
        _rail.BringToFront();

        RefreshStatus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        SetScroll(_scroll - e.Delta / 120 * Theme.S(48));
        base.OnMouseWheel(e);
    }

    void SetScroll(int value)
    {
        int clamped = Math.Clamp(value, 0, Math.Max(0, _contentH - Height));
        if (clamped == _scroll) return;
        _scroll = clamped;
        Relayout();
    }

    protected override void OnResize(EventArgs eventargs) { Relayout(); base.OnResize(eventargs); }

    void Relayout()
    {
        int x = Theme.S(16), w = Width - Theme.S(32), y = Theme.PageTop - _scroll;
        _title.Top = Theme.S(12) - _scroll;
        foreach (var card in new[] { _mqttCard, _udpCard, _tcpCard, _serialCard })
        {
            card.SetBounds(x, y, w, card.WantedHeight);
            y += card.WantedHeight + Theme.S(10);
        }
        _contentH = y + _scroll + Theme.S(6);
        // a collapse may have shrunk the content below the current offset
        if (_scroll > Math.Max(0, _contentH - Height))
            _scroll = Math.Max(0, _contentH - Height);
        _rail?.Invalidate();
    }

    // Slim overlay scrollbar living in the page's 16px right margin — draws
    // over empty space only, so its appearance never shifts the cards.
    sealed class ScrollRail : Control
    {
        readonly OutputsPage _page;
        bool _drag, _hover;
        int _dragY, _dragStart;

        public ScrollRail(OutputsPage page)
        {
            _page = page;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint, true);
            BackColor = Theme.WinBg;
            Width = Theme.S(10);
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
            page.Resize += (_, _) => Bounds = new Rectangle(page.Width - Width, 0, Width, page.Height);
        }

        int Max => Math.Max(0, _page._contentH - _page.Height);

        Rectangle Thumb()
        {
            if (Max == 0 || _page._contentH <= 0) return Rectangle.Empty;
            int trackH = Height - Theme.S(4);
            int thumbH = Math.Max(Theme.S(30), trackH * _page.Height / _page._contentH);
            int thumbY = Theme.S(2) + (trackH - thumbH) * _page._scroll / Max;
            return new Rectangle(Theme.S(2), thumbY, Width - Theme.S(5), thumbH);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.WinBg);
            var thumb = Thumb();
            if (thumb.IsEmpty) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_drag || _hover ? Theme.ScrollHover : Theme.Scroll);
            using var path = Theme.RoundedRect(thumb, thumb.Width / 2f);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var thumb = Thumb();
            if (thumb.IsEmpty) return;
            if (!thumb.Contains(e.Location))
            {
                int trackH = Height - Theme.S(4) - thumb.Height;
                if (trackH > 0)
                    _page.SetScroll((e.Y - Theme.S(2) - thumb.Height / 2) * Max / trackH);
            }
            _drag = true;
            _dragY = e.Y;
            _dragStart = _page._scroll;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_drag)
            {
                var thumb = Thumb();
                int trackH = Height - Theme.S(4) - thumb.Height;
                if (trackH > 0)
                    _page.SetScroll(_dragStart + (e.Y - _dragY) * Max / trackH);
            }
            else if (!_hover) { _hover = true; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e) { _drag = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    // Called by MainWindow when a sink reports a state change; null kind means
    // the sink was disposed.
    public void SetSinkStatus(string sink, StatusKind? kind, string text)
    {
        if (kind == null) _reported.Remove(sink);
        else _reported[sink] = (kind.Value, text);
        RefreshStatus();
    }

    // Hooks for DemoMode scenarios.
    internal OutputCard DemoCard(string sink) => sink switch
    {
        "MQTT" => _mqttCard, "UDP" => _udpCard, "TCP" => _tcpCard, _ => _serialCard,
    };
    internal TextBox DemoField(string name) => name switch
    {
        "udp.host" => _udpHost, "tcp.port" => _tcpPort, "serial.port" => _serialPort, _ => _host,
    };

    // True while any checked field is invalid, enabled card or not; MainWindow
    // refuses to save in that case.
    public bool HasInvalidInput => _checks.Any(c => InputBox.Of(c.Box).Invalid);

    // Host field rule. Empty is allowed here; the card shows "set a host".
    // - Starts like an IPv4 address (digits-only first part): must be a
    //   complete one, so "192.168.2.411" and "192.168.2.255eeee" are rejected.
    // - Contains ':': must be an IPv6 address.
    // - Otherwise a host name: dot-separated parts of letters, digits and
    //   inner hyphens (RFC 1123). Whether it resolves shows up on connecting.
    internal static bool ValidHost(string text)
    {
        var host = text.Trim();
        if (host.Length == 0)
            return true;
        if (host.Contains(':'))
            return System.Net.IPAddress.TryParse(host, out var v6)
                && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        var parts = host.Split('.');
        if (parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit))
            return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit))
                && System.Net.IPAddress.TryParse(host, out _);
        return parts.All(p => p.Length is > 0 and <= 63
            && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            && p[0] != '-' && p[^1] != '-');
    }

    internal static bool ValidPort(string text) => int.TryParse(text.Trim(), out var p) && p is >= 1 and <= 65535;
    internal static bool ValidBaud(string text) => int.TryParse(text.Trim(), out var b) && b >= 1;

    void RefreshStatus()
    {
        ShowStatus(_mqttCard, "MQTT", Missing(_host, L.T.OutNoHost) ?? Problem(_mqttCard));
        ShowStatus(_udpCard, "UDP", Missing(_udpHost, L.T.OutNoHost) ?? Problem(_udpCard));
        ShowStatus(_tcpCard, "TCP", Problem(_tcpCard));
        ShowStatus(_serialCard, "Serial", Missing(_serialPort, L.T.OutNoComPort) ?? Problem(_serialCard));
    }

    static (string, StatusKind)? Missing(TextBox box, string text) =>
        string.IsNullOrWhiteSpace(box.Text) ? (text, StatusKind.Off) : null;

    // The first invalid field of the card, if any.
    (string, StatusKind)? Problem(OutputCard card)
    {
        foreach (var c in _checks)
            if (c.Card == card && InputBox.Of(c.Box).Invalid)
                return (c.Problem, StatusKind.Error);
        return null;
    }

    // One rule for every card: switched off, then a problem in the fields,
    // then whatever the running sink reported. A sink that is not running
    // yet (enabled but not saved) has not reported anything.
    void ShowStatus(OutputCard card, string sink, (string Text, StatusKind Kind)? problem)
    {
        if (!card.Toggle.Checked)
            card.SetStatus(L.T.OutDisabled, StatusKind.Off);
        else if (problem is { } p)
            card.SetStatus(p.Text, p.Kind);
        else if (_reported.TryGetValue(sink, out var r))
            card.SetStatus(r.Text, r.Kind);
        else
            card.SetStatus(L.T.OutNotActive, StatusKind.Off);
    }

    // Write the edited values back into the shared config. Called on Save.
    public void Apply()
    {
        _config.Mqtt.Enabled            = _mqttCard.Toggle.Checked;
        _config.Mqtt.Host               = _host.Text.Trim();
        _config.Mqtt.Port               = ParsePort(_port, _config.Mqtt.Port);
        _config.Mqtt.Username           = _username.Text;
        _config.Mqtt.Password           = _password.Text;
        _config.Mqtt.TopicRoot          = _topic.Text.Trim();
        _config.Mqtt.UseTls             = _useTls.Checked;
        _config.Mqtt.HaDiscoveryEnabled = _haDiscovery.Checked;

        _config.Udp.Enabled = _udpCard.Toggle.Checked;
        _config.Udp.Host    = _udpHost.Text.Trim();
        _config.Udp.Port    = ParsePort(_udpPort, _config.Udp.Port);

        _config.Tcp.Enabled    = _tcpCard.Toggle.Checked;
        _config.Tcp.ListenPort = ParsePort(_tcpPort, _config.Tcp.ListenPort);

        _config.Serial.Enabled = _serialCard.Toggle.Checked;
        _config.Serial.Port    = ExtractComPort(_serialPort.Text);
        _config.Serial.Baud    = ParseBaud(_serialBaud, _config.Serial.Baud);

        RefreshStatus();
    }

    // Discard edits: refill every field from the saved config.
    public void Revert()
    {
        var m = _config.Mqtt;
        _mqttCard.Toggle.SetChecked(m.Enabled);
        _host.Text         = m.Host;
        _port.Text         = m.Port.ToString();
        _username.Text     = m.Username;
        _password.Text     = m.Password;
        _topic.Text        = m.TopicRoot;
        _useTls.Checked    = m.UseTls;
        _haDiscovery.Checked = m.HaDiscoveryEnabled;

        _udpCard.Toggle.SetChecked(_config.Udp.Enabled);
        _udpHost.Text = _config.Udp.Host;
        _udpPort.Text = _config.Udp.Port.ToString();

        _tcpCard.Toggle.SetChecked(_config.Tcp.Enabled);
        _tcpPort.Text = _config.Tcp.ListenPort.ToString();

        _serialCard.Toggle.SetChecked(_config.Serial.Enabled);
        _serialPort.Text = _config.Serial.Port;
        _serialBaud.Text = _config.Serial.Baud.ToString();

        RefreshStatus();
    }

    // True when the fields hold values that Apply would write differently
    // into the config — same normalization as Apply.
    public bool HasChanges
    {
        get
        {
            var m = _config.Mqtt;
            return _mqttCard.Toggle.Checked != m.Enabled
                || _host.Text.Trim() != m.Host
                || PortOr(_port, m.Port) != m.Port
                || _username.Text != m.Username
                || _password.Text != m.Password
                || _topic.Text.Trim() != m.TopicRoot
                || _useTls.Checked != m.UseTls
                || _haDiscovery.Checked != m.HaDiscoveryEnabled
                || _udpCard.Toggle.Checked != _config.Udp.Enabled
                || _udpHost.Text.Trim() != _config.Udp.Host
                || PortOr(_udpPort, _config.Udp.Port) != _config.Udp.Port
                || _tcpCard.Toggle.Checked != _config.Tcp.Enabled
                || PortOr(_tcpPort, _config.Tcp.ListenPort) != _config.Tcp.ListenPort
                || _serialCard.Toggle.Checked != _config.Serial.Enabled
                || ExtractComPort(_serialPort.Text) != _config.Serial.Port
                || BaudOr(_serialBaud, _config.Serial.Baud) != _config.Serial.Baud;
        }
    }

    // Valid port or the previous value.
    static int PortOr(TextBox box, int fallback) =>
        int.TryParse(box.Text.Trim(), out var port) && port is >= 1 and <= 65535 ? port : fallback;

    static int BaudOr(TextBox box, int fallback) =>
        int.TryParse(box.Text.Trim(), out var baud) && baud >= 1 ? baud : fallback;

    // Same as PortOr/BaudOr, and the field is normalized to the result.
    static int ParsePort(TextBox box, int fallback)
    {
        var port = PortOr(box, fallback);
        box.Text = port.ToString();
        return port;
    }

    static int ParseBaud(TextBox box, int fallback)
    {
        var baud = BaudOr(box, fallback);
        box.Text = baud.ToString();
        return baud;
    }

    // Dropdown entries like "COM9 — Silicon Labs CP210x USB to UART Bridge":
    // friendly names come from WMI (Win32_PnPEntity), matched to the raw port
    // list; ports without a PnP entry stay plain. "COM2" before "COM10".
    internal static string[] AvailableComPorts()
    {
        var ports = System.IO.Ports.SerialPort.GetPortNames()
            .Distinct()
            .OrderBy(p => p.Length).ThenBy(p => p)
            .ToList();

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var device in searcher.Get())
            {
                if (device["Name"] is not string name) continue;   // e.g. "… Bridge (COM9)"
                var match = System.Text.RegularExpressions.Regex.Match(name, @"\((COM\d+)\)");
                if (match.Success)
                    names[match.Groups[1].Value] = name[..match.Index].Trim();
            }
        }
        catch { /* WMI unavailable — plain port names still work */ }

        return ports
            .Select(p => names.TryGetValue(p, out var n) && n.Length > 0 ? $"{p} — {n}" : p)
            .ToArray();
    }

    // The combo shows "COM9 — <description>", the config stores only "COM9".
    internal static string ExtractComPort(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            text.Trim(), @"^COM\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToUpperInvariant() : text.Trim().ToUpperInvariant();
    }
}


// A collapsible sink card: fixed header (chevron, name, status dot + text,
// enable switch), body rows added by the page. Clicking the header toggles.
sealed class OutputCard : CardPanel
{
    static readonly int HeadH = Theme.S(48), RowStep = Theme.S(40), BodyPad = Theme.S(8);

    // Label column fits the longest localized field label ("Benutzername").
    static readonly int LabelW = Math.Max(Theme.S(104),
        new[] { L.T.FieldHost, L.T.FieldPort, L.T.FieldUsername, L.T.FieldPassword,
                L.T.FieldTopicRoot, L.T.FieldListenPort, L.T.FieldBaudRate }
            .Max(t => TextRenderer.MeasureText(t, Theme.Base).Width) + Theme.S(28));

    public readonly ToggleSwitch Toggle = new();
    public Action? HeightChanged;

    // Full-width body controls, resized explicitly on card resize. Anchoring
    // would compute from the card's tiny default width, where the target width
    // is negative and gets clamped — the anchor delta is then permanently off.
    readonly List<Control> _stretch = new();

    readonly Action _markDirty;
    readonly string _name;
    string _status = "";
    StatusKind _statusKind;
    bool _open, _headHover;
    int _bodyY = HeadH + BodyPad;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Open
    {
        get => _open;
        set
        {
            _open = value;
            // Body controls are only meaningful while open — hide them when
            // collapsed so they can't paint outside the shrunken card.
            foreach (Control c in Controls)
                if (c != Toggle) c.Visible = _open;
            HeightChanged?.Invoke();
            Invalidate();
        }
    }

    public int WantedHeight => _open ? _bodyY + BodyPad : HeadH;

    public OutputCard(string name, Action markDirty)
    {
        _name = name;
        _markDirty = markDirty;
        Toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        Controls.Add(Toggle);
        Resize += (_, _) =>
        {
            Toggle.Location = new Point(Width - Toggle.Width - Theme.S(14), (HeadH - Toggle.Height) / 2);
            foreach (var c in _stretch)
                c.Width = Width - LabelW - Theme.S(28);
        };
    }

    public void SetStatus(string text, StatusKind kind)
    {
        if (_status == text && _statusKind == kind) return;
        _status = text;
        _statusKind = kind;
        Invalidate(new Rectangle(0, 0, Width, HeadH));
    }

    // ── body construction ────────────────────────────────────────────────────

    public TextBox AddTextRow(string label, string value, bool password = false)
    {
        var input = new InputBox();
        input.Box.Text = value;
        input.Box.UseSystemPasswordChar = password;
        input.Box.TextChanged += (_, _) => _markDirty();
        AddRow(label, input, stretch: true);
        return input.Box;
    }

    public TextBox AddPortRow(string label, int value)
    {
        var input = new InputBox();
        input.Box.Text = value.ToString();
        input.Box.TextChanged += (_, _) => _markDirty();
        AddRow(label, input, stretch: true);   // full width, same as the text fields
        return input.Box;
    }

    // Text field with a suggestion list (e.g. the COM ports present right now).
    // Stays editable so a port that is not plugged in can still be typed.
    public TextBox AddSuggestRow(string label, string value, Func<string[]> suggestions)
    {
        var input = new InputBox { Suggestions = suggestions };
        input.Box.Text = value;
        input.Box.TextChanged += (_, _) => _markDirty();
        AddRow(label, input, stretch: true);
        return input.Box;
    }

    public FlatCheck AddCheckRow(string text, bool value)
    {
        var box = new FlatCheck { Text = text, Visible = _open };
        box.Checked = value;
        box.CheckedChanged += (_, _) => _markDirty();
        box.Location = new Point(LabelW + Theme.S(14), _bodyY + Theme.S(3));
        Controls.Add(box);
        _bodyY += Theme.S(28);
        return box;
    }

    void AddRow(string label, Control control, bool stretch)
    {
        var lbl = new Label
        {
            Text = label, ForeColor = Theme.Fg2, AutoSize = false,
            Bounds = new Rectangle(Theme.S(14), _bodyY + Theme.S(6), LabelW - Theme.S(14), Theme.S(20)),
            Visible = _open,
        };
        control.Location = new Point(LabelW + Theme.S(14), _bodyY);
        control.Visible = _open;
        if (stretch)
            _stretch.Add(control);   // width follows the card in the Resize handler
        Controls.Add(lbl);
        Controls.Add(control);
        _bodyY += RowStep;
    }

    // ── header interaction ───────────────────────────────────────────────────

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Y < HeadH) Open = !_open;
        base.OnMouseClick(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hover = e.Y < HeadH;
        if (hover != _headHover) { _headHover = hover; Invalidate(new Rectangle(0, 0, Width, HeadH)); }
        Cursor = hover ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_headHover) { _headHover = false; Invalidate(new Rectangle(0, 0, Width, HeadH)); }
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);   // card background + border
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_headHover)
        {
            using var hover = new SolidBrush(Theme.Hover);
            using var path = Theme.RoundedRect(new RectangleF(1, 1, Width - 2, HeadH - (_open ? 0 : 2)), Theme.SF(7));
            g.FillPath(hover, path);
        }

        TextRenderer.DrawText(g, Theme.Glyph(_open ? NavIcons.ChevronDown : NavIcons.ChevronRight), Theme.IconsSmall,
            new Rectangle(Theme.S(10), 0, Theme.S(18), HeadH), Theme.Fg2,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, _name, Theme.SemiBold, new Rectangle(Theme.S(34), 0, Theme.S(60), HeadH), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // status dot + text: green when running, red on errors, grey when off
        var statusColor = _statusKind switch
        {
            StatusKind.Good  => Theme.Good,
            StatusKind.Error => Theme.Crit,
            _                => Theme.Fg3,
        };
        using (var dot = new SolidBrush(statusColor))
            g.FillEllipse(dot, Theme.SF(98), HeadH / 2f - Theme.SF(4), Theme.SF(8), Theme.SF(8));
        int statusX = Theme.S(112);
        var statusRect = new Rectangle(statusX, 0, Width - statusX - Toggle.Width - Theme.S(24), HeadH);
        TextRenderer.DrawText(g, _status, Theme.Small, statusRect,
            _statusKind == StatusKind.Error ? Theme.Crit : Theme.Fg2,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // separator above the body
        if (_open)
        {
            using var pen = new Pen(Theme.CardBorder);
            g.DrawLine(pen, 1, HeadH, Width - 2, HeadH);
        }
    }
}
