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

    readonly OutputCard _mqttCard, _udpCard, _tcpCard;

    // MQTT fields
    readonly TextBox _host, _username, _password, _topic;
    readonly NumericUpDown _port;
    readonly CheckBox _useTls, _haDiscovery;
    // UDP fields
    readonly TextBox _udpHost;
    readonly NumericUpDown _udpPort;
    // TCP fields
    readonly NumericUpDown _tcpPort;

    // live MQTT state pushed in from the sink via MainWindow
    bool _mqttConnected;
    string _mqttBroker = "";

    public OutputsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        _markDirty = markDirty;
        BackColor = Theme.WinBg;

        var title = new Label
        {
            Text = "Outputs", Font = Theme.Title, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(16, 12)
        };
        Controls.Add(title);

        // ── MQTT ────────────────────────────────────────────────────────────
        _mqttCard = new OutputCard("MQTT", markDirty) { Open = true };
        _host     = _mqttCard.AddTextRow("Host", config.Mqtt.Host);
        _port     = _mqttCard.AddNumberRow("Port", config.Mqtt.Port, 1, 65535);
        _username = _mqttCard.AddTextRow("Username", config.Mqtt.Username);
        _password = _mqttCard.AddTextRow("Password", config.Mqtt.Password, password: true);
        _topic    = _mqttCard.AddTextRow("Topic root", config.Mqtt.TopicRoot);
        _useTls      = _mqttCard.AddCheckRow("Use TLS (typically port 8883)", config.Mqtt.UseTls);
        _haDiscovery = _mqttCard.AddCheckRow("Home Assistant MQTT Discovery", config.Mqtt.HaDiscoveryEnabled);
        _mqttCard.Toggle.SetChecked(config.Mqtt.Enabled);

        // ── UDP ─────────────────────────────────────────────────────────────
        _udpCard = new OutputCard("UDP", markDirty);
        _udpHost = _udpCard.AddTextRow("Host", config.Udp.Host);
        _udpPort = _udpCard.AddNumberRow("Port", config.Udp.Port, 1, 65535);
        _udpCard.Toggle.SetChecked(config.Udp.Enabled);

        // ── TCP ─────────────────────────────────────────────────────────────
        _tcpCard = new OutputCard("TCP", markDirty);
        _tcpPort = _tcpCard.AddNumberRow("Listen port", config.Tcp.ListenPort, 1, 65535);
        _tcpCard.Toggle.SetChecked(config.Tcp.Enabled);

        foreach (var card in new[] { _mqttCard, _udpCard, _tcpCard })
        {
            card.Toggle.CheckedChanged += (_, _) => { markDirty(); RefreshStatus(); };
            card.HeightChanged = Relayout;
            Controls.Add(card);
        }

        RefreshStatus();
    }

    protected override void OnResize(EventArgs eventargs) { Relayout(); base.OnResize(eventargs); }

    void Relayout()
    {
        int x = 16, w = Width - 32, y = 42;
        foreach (var card in new[] { _mqttCard, _udpCard, _tcpCard })
        {
            card.SetBounds(x, y, w, card.WantedHeight);
            y += card.WantedHeight + 10;
        }
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
            _mqttCard.SetStatus("Disabled", false);
        else if (string.IsNullOrWhiteSpace(_host.Text))
            _mqttCard.SetStatus("Not configured — set a host", false);
        else if (_mqttConnected)
            _mqttCard.SetStatus($"Connected — {_mqttBroker}", true);
        else
            _mqttCard.SetStatus("Not connected", false);

        if (!_udpCard.Toggle.Checked)
            _udpCard.SetStatus("Disabled", false);
        else if (string.IsNullOrWhiteSpace(_udpHost.Text))
            _udpCard.SetStatus("Not configured — set a host", false);
        else
            _udpCard.SetStatus($"Sending to {_udpHost.Text}:{(int)_udpPort.Value}", true);

        _tcpCard.SetStatus(_tcpCard.Toggle.Checked
            ? $"Listening on {(int)_tcpPort.Value}" : "Disabled",
            _tcpCard.Toggle.Checked);
    }

    // Write the edited values back into the shared config. Called on Save.
    public void Apply()
    {
        _config.Mqtt.Enabled            = _mqttCard.Toggle.Checked;
        _config.Mqtt.Host               = _host.Text.Trim();
        _config.Mqtt.Port               = (int)_port.Value;
        _config.Mqtt.Username           = _username.Text;
        _config.Mqtt.Password           = _password.Text;
        _config.Mqtt.TopicRoot          = _topic.Text.Trim();
        _config.Mqtt.UseTls             = _useTls.Checked;
        _config.Mqtt.HaDiscoveryEnabled = _haDiscovery.Checked;

        _config.Udp.Enabled = _udpCard.Toggle.Checked;
        _config.Udp.Host    = _udpHost.Text.Trim();
        _config.Udp.Port    = (int)_udpPort.Value;

        _config.Tcp.Enabled    = _tcpCard.Toggle.Checked;
        _config.Tcp.ListenPort = (int)_tcpPort.Value;

        RefreshStatus();
    }
}

// A collapsible sink card: fixed header (chevron, name, status dot + text,
// enable switch), body rows added by the page. Clicking the header toggles.
sealed class OutputCard : CardPanel
{
    const int HeadH = 40, RowStep = 30, LabelW = 104, BodyPad = 8;

    public readonly ToggleSwitch Toggle = new();
    public Action? HeightChanged;

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
        Resize += (_, _) => Toggle.Location = new Point(Width - Toggle.Width - 14, (HeadH - Toggle.Height) / 2);
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
        var box = new TextBox
        {
            Text = value,
            UseSystemPasswordChar = password,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.InputBg,
            ForeColor = Theme.Fg,
        };
        box.TextChanged += (_, _) => _markDirty();
        AddRow(label, box, stretch: true);
        return box;
    }

    public NumericUpDown AddNumberRow(string label, decimal value, decimal min, decimal max)
    {
        var num = new NumericUpDown
        {
            Minimum = min, Maximum = max, Value = value, Width = 90,
            BackColor = Theme.InputBg, ForeColor = Theme.Fg,
        };
        num.ValueChanged += (_, _) => _markDirty();
        AddRow(label, num, stretch: false);
        return num;
    }

    public CheckBox AddCheckRow(string text, bool value)
    {
        var box = new CheckBox
        {
            Text = text, Checked = value, AutoSize = true,
            ForeColor = Theme.Fg, Visible = _open,
        };
        box.CheckedChanged += (_, _) => _markDirty();
        box.Location = new Point(LabelW + 14, _bodyY + 2);
        Controls.Add(box);
        _bodyY += 26;
        return box;
    }

    void AddRow(string label, Control control, bool stretch)
    {
        var lbl = new Label
        {
            Text = label, ForeColor = Theme.Fg2, AutoSize = false,
            Bounds = new Rectangle(14, _bodyY + 4, LabelW - 14, 18),
            Visible = _open,
        };
        control.Location = new Point(LabelW + 14, _bodyY);
        control.Visible = _open;
        if (stretch)
        {
            control.Width = Width - LabelW - 28;
            control.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        }
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
            using var path = Theme.RoundedRect(new RectangleF(1, 1, Width - 2, HeadH - (_open ? 0 : 2)), 6);
            g.FillPath(hover, path);
        }

        // chevron
        using (var pen = new Pen(Theme.Fg3, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            float cx = 19, cy = HeadH / 2f;
            if (_open)
            {
                g.DrawLine(pen, cx - 3.5f, cy - 2, cx, cy + 2);
                g.DrawLine(pen, cx, cy + 2, cx + 3.5f, cy - 2);
            }
            else
            {
                g.DrawLine(pen, cx - 2, cy - 3.5f, cx + 2, cy);
                g.DrawLine(pen, cx + 2, cy, cx - 2, cy + 3.5f);
            }
        }

        TextRenderer.DrawText(g, _name, Theme.SemiBold, new Rectangle(30, 0, 52, HeadH), Theme.Fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        // status dot + text
        using (var dot = new SolidBrush(_statusOn ? Theme.Good : Theme.Fg3))
            g.FillEllipse(dot, 84, HeadH / 2f - 4, 8, 8);
        var statusRect = new Rectangle(98, 0, Width - 98 - Toggle.Width - 24, HeadH);
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
