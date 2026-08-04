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
    readonly ComboBox _serialPort;
    readonly TextBox _serialBaud;

    // live MQTT state pushed in from the sink via MainWindow
    bool _mqttConnected;
    string _mqttBroker = "";

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
        _serialPort = _serialCard.AddComboRow(L.T.FieldPort, config.Serial.Port, AvailableComPorts());
        // Re-enumerate on every open — USB adapters come and go.
        _serialPort.DropDown += (_, _) =>
        {
            var current = _serialPort.Text;
            _serialPort.Items.Clear();
            _serialPort.Items.AddRange(AvailableComPorts());
            _serialPort.Text = current;
        };
        _serialBaud = _serialCard.AddPortRow(L.T.FieldBaudRate, config.Serial.Baud);
        _serialCard.Toggle.SetChecked(config.Serial.Enabled);

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
        int x = Theme.S(16), w = Width - Theme.S(32), y = Theme.S(42) - _scroll;
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

    // Called by MainWindow when the MQTT sink reports a connection change,
    // and locally whenever a toggle flips.
    public void SetMqttStatus(bool connected, string broker)
    {
        _mqttConnected = connected;
        _mqttBroker = broker;
        RefreshStatus();
    }

    void RefreshStatus()
    {
        if (!_mqttCard.Toggle.Checked)
            _mqttCard.SetStatus(L.T.OutDisabled, false);
        else if (string.IsNullOrWhiteSpace(_host.Text))
            _mqttCard.SetStatus(L.T.OutNoHost, false);
        else if (_mqttConnected)
            _mqttCard.SetStatus(string.Format(L.T.StatusConnected, _mqttBroker), true);
        else
            _mqttCard.SetStatus(L.T.OutNotConnected, false);

        if (!_udpCard.Toggle.Checked)
            _udpCard.SetStatus(L.T.OutDisabled, false);
        else if (string.IsNullOrWhiteSpace(_udpHost.Text))
            _udpCard.SetStatus(L.T.OutNoHost, false);
        else
            _udpCard.SetStatus(string.Format(L.T.OutSendingTo, _udpHost.Text, _udpPort.Text), true);

        _tcpCard.SetStatus(_tcpCard.Toggle.Checked
            ? string.Format(L.T.OutListeningOn, _tcpPort.Text) : L.T.OutDisabled,
            _tcpCard.Toggle.Checked);

        if (!_serialCard.Toggle.Checked)
            _serialCard.SetStatus(L.T.OutDisabled, false);
        else if (string.IsNullOrWhiteSpace(_serialPort.Text))
            _serialCard.SetStatus(L.T.OutNoComPort, false);
        else
            _serialCard.SetStatus(string.Format(L.T.OutSendingCom, ExtractComPort(_serialPort.Text), _serialBaud.Text), true);
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

    // Valid port or the previous value — the field is normalized either way.
    static int ParsePort(TextBox box, int fallback)
    {
        if (!int.TryParse(box.Text.Trim(), out var port) || port is < 1 or > 65535)
            port = fallback;
        box.Text = port.ToString();
        return port;
    }

    static int ParseBaud(TextBox box, int fallback)
    {
        if (!int.TryParse(box.Text.Trim(), out var baud) || baud < 1)
            baud = fallback;
        box.Text = baud.ToString();
        return baud;
    }

    // Dropdown entries like "COM9 — Silicon Labs CP210x USB to UART Bridge":
    // friendly names come from WMI (Win32_PnPEntity), matched to the raw port
    // list; ports without a PnP entry stay plain. "COM2" before "COM10".
    static object[] AvailableComPorts()
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
            .Cast<object>()
            .ToArray();
    }

    // The combo shows "COM9 — <description>", the config stores only "COM9".
    static string ExtractComPort(string text)
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
    static readonly int HeadH = Theme.S(40), RowStep = Theme.S(36),
                        LabelW = Theme.S(104), BodyPad = Theme.S(8);

    public readonly ToggleSwitch Toggle = new();
    public Action? HeightChanged;

    // Full-width body controls, resized explicitly on card resize. Anchoring
    // would compute from the card's tiny default width, where the target width
    // is negative and gets clamped — the anchor delta is then permanently off.
    readonly List<Control> _stretch = new();

    readonly Action _markDirty;
    readonly string _name;
    string _status = "";
    bool _statusOn, _open, _headHover;
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

    public void SetStatus(string text, bool on)
    {
        if (_status == text && _statusOn == on) return;
        _status = text;
        _statusOn = on;
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

    // Native rendering on purpose — same reason as the theme selector in
    // SettingsPage: the dark color mode themes the ComboBox correctly, custom
    // colors break its arrow drawing. Editable so a port that is not currently
    // plugged in can still be typed.
    public ComboBox AddComboRow(string label, string value, object[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        combo.Items.AddRange(items);
        combo.Text = value;
        combo.TextChanged += (_, _) => _markDirty();
        AddRow(label, combo, stretch: true);
        return combo;
    }

    public FlatCheck AddCheckRow(string text, bool value)
    {
        var box = new FlatCheck { Text = text, Visible = _open };
        box.Checked = value;
        box.CheckedChanged += (_, _) => _markDirty();
        box.Location = new Point(LabelW + Theme.S(14), _bodyY + Theme.S(2));
        Controls.Add(box);
        _bodyY += Theme.S(26);
        return box;
    }

    void AddRow(string label, Control control, bool stretch)
    {
        var lbl = new Label
        {
            Text = label, ForeColor = Theme.Fg2, AutoSize = false,
            Bounds = new Rectangle(Theme.S(14), _bodyY + Theme.S(6), LabelW - Theme.S(14), Theme.S(18)),
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
            using var path = Theme.RoundedRect(new RectangleF(1, 1, Width - 2, HeadH - (_open ? 0 : 2)), Theme.SF(6));
            g.FillPath(hover, path);
        }

        // chevron
        using (var pen = new Pen(Theme.Fg3, Theme.SF(1.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = Theme.SF(19), cy = HeadH / 2f;
            float a = Theme.SF(3.5f), b = Theme.SF(2);
            if (_open)
            {
                g.DrawLine(pen, cx - a, cy - b, cx, cy + b);
                g.DrawLine(pen, cx, cy + b, cx + a, cy - b);
            }
            else
            {
                g.DrawLine(pen, cx - b, cy - a, cx + b, cy);
                g.DrawLine(pen, cx + b, cy, cx - b, cy + a);
            }
        }

        TextRenderer.DrawText(g, _name, Theme.SemiBold, new Rectangle(Theme.S(30), 0, Theme.S(52), HeadH), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // status dot + text
        using (var dot = new SolidBrush(_statusOn ? Theme.Good : Theme.Fg3))
            g.FillEllipse(dot, Theme.SF(84), HeadH / 2f - Theme.SF(4), Theme.SF(8), Theme.SF(8));
        int statusX = Theme.S(98);
        var statusRect = new Rectangle(statusX, 0, Width - statusX - Toggle.Width - Theme.S(24), HeadH);
        TextRenderer.DrawText(g, _status, Theme.Small, statusRect, Theme.Fg2,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // separator above the body
        if (_open)
        {
            using var pen = new Pen(Theme.CardBorder);
            g.DrawLine(pen, 1, HeadH, Width - 2, HeadH);
        }
    }
}
