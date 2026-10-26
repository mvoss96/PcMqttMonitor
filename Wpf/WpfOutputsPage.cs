using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

// WPF port of OutputsPage: one expandable card per sink with the enable
// switch and the live status in its header. Input rules, status priority and
// save/revert logic are the same as in the WinForms page, whose static rule
// and COM port helpers are reused.
sealed class WpfOutputsPage : UserControl
{
    readonly AppConfig _config;
    readonly OutputExpander _mqtt, _udp, _tcp, _serial;
    readonly TextBox _host, _port, _username, _topic, _udpHost, _udpPort, _tcpPort, _baud;
    readonly PasswordBox _password;
    readonly ComboBox _serialPort;
    readonly CheckBox _tls, _discovery;

    // Last state each running sink reported, keyed by sink name ("MQTT", …).
    readonly Dictionary<string, (StatusKind Kind, string Text)> _reported = new();

    // Checked fields: the card status shows Problem while the rule fails.
    readonly (OutputExpander Card, TextBox Box, Func<string, bool> Rule, string Problem)[] _checks;

    public WpfOutputsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        void Changed() { markDirty(); RefreshStatus(); }

        _mqtt = new OutputExpander("MQTT", Changed) { IsExpanded = true };
        _host = _mqtt.AddText(L.T.FieldHost, Changed);
        _port = _mqtt.AddText(L.T.FieldPort, Changed);
        _username = _mqtt.AddText(L.T.FieldUsername, Changed);
        _password = new PasswordBox();
        _password.PasswordChanged += (_, _) => Changed();
        _mqtt.AddRow(L.T.FieldPassword, _password);
        _topic = _mqtt.AddText(L.T.FieldTopicRoot, Changed);
        _tls = _mqtt.AddCheck(L.T.FieldUseTls, Changed);
        _discovery = _mqtt.AddCheck(L.T.FieldDiscovery, Changed);

        _udp = new OutputExpander("UDP", Changed);
        _udpHost = _udp.AddText(L.T.FieldHost, Changed);
        _udpPort = _udp.AddText(L.T.FieldPort, Changed);

        _tcp = new OutputExpander("TCP", Changed);
        _tcpPort = _tcp.AddText(L.T.FieldListenPort, Changed);

        _serial = new OutputExpander("Serial", Changed);
        // Editable: a port that is not plugged in can still be typed. The list
        // is read on every open because USB adapters come and go.
        _serialPort = new ComboBox { IsEditable = true };
        _serialPort.DropDownOpened += (_, _) => _serialPort.ItemsSource = OutputsPage.AvailableComPorts();
        _serialPort.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Changed()));
        _serial.AddRow(L.T.FieldPort, _serialPort);
        _baud = _serial.AddText(L.T.FieldBaudRate, Changed);

        _checks =
        [
            (_mqtt, _host, OutputsPage.ValidHost, L.T.OutInvalidIp), (_mqtt, _port, OutputsPage.ValidPort, L.T.OutInvalidPort),
            (_udp, _udpHost, OutputsPage.ValidHost, L.T.OutInvalidIp), (_udp, _udpPort, OutputsPage.ValidPort, L.T.OutInvalidPort),
            (_tcp, _tcpPort, OutputsPage.ValidPort, L.T.OutInvalidPort),
            (_serial, _baud, OutputsPage.ValidBaud, L.T.OutInvalidBaud),
        ];

        var stack = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        Grid.SetIsSharedSizeScope(stack, true);
        var title = new TextBlock { Text = L.T.OutputsTitle, Style = (Style)Application.Current.FindResource("PageTitle") };
        stack.Children.Add(title);
        foreach (var card in new[] { _mqtt, _udp, _tcp, _serial })
        {
            card.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(card);
        }
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Revert();
    }

    // Called when a sink reports a state change; null kind means it was disposed.
    public void SetSinkStatus(string sink, StatusKind? kind, string text)
    {
        if (kind == null) _reported.Remove(sink);
        else _reported[sink] = (kind.Value, text);
        RefreshStatus();
    }

    public bool HasInvalidInput => _checks.Any(c => !c.Rule(c.Box.Text));

    void RefreshStatus()
    {
        foreach (var c in _checks)
            c.Card.SetInvalid(c.Box, !c.Rule(c.Box.Text));
        ShowStatus(_mqtt, "MQTT", Missing(_host.Text, L.T.OutNoHost) ?? Problem(_mqtt));
        ShowStatus(_udp, "UDP", Missing(_udpHost.Text, L.T.OutNoHost) ?? Problem(_udp));
        ShowStatus(_tcp, "TCP", Problem(_tcp));
        ShowStatus(_serial, "Serial", Missing(_serialPort.Text, L.T.OutNoComPort) ?? Problem(_serial));
    }

    static (string, StatusKind)? Missing(string text, string message) =>
        string.IsNullOrWhiteSpace(text) ? (message, StatusKind.Off) : null;

    (string, StatusKind)? Problem(OutputExpander card)
    {
        foreach (var c in _checks)
            if (c.Card == card && !c.Rule(c.Box.Text))
                return (c.Problem, StatusKind.Error);
        return null;
    }

    // Switched off, then a problem in the fields, then what the running sink
    // reported. A sink that is enabled but not saved yet has reported nothing.
    void ShowStatus(OutputExpander card, string sink, (string Text, StatusKind Kind)? problem)
    {
        if (card.Enabled.IsChecked != true) card.SetStatus(L.T.OutDisabled, StatusKind.Off);
        else if (problem is { } p) card.SetStatus(p.Text, p.Kind);
        else if (_reported.TryGetValue(sink, out var r)) card.SetStatus(r.Text, r.Kind);
        else card.SetStatus(L.T.OutNotActive, StatusKind.Off);
    }

    // Write the edited values into the shared config. Called on Save.
    public void Apply()
    {
        var m = _config.Mqtt;
        m.Enabled = _mqtt.Enabled.IsChecked == true;
        m.Host = _host.Text.Trim();
        m.Port = PortOr(_port, m.Port);
        m.Username = _username.Text;
        m.Password = _password.Password;
        m.TopicRoot = _topic.Text.Trim();
        m.UseTls = _tls.IsChecked == true;
        m.HaDiscoveryEnabled = _discovery.IsChecked == true;

        _config.Udp.Enabled = _udp.Enabled.IsChecked == true;
        _config.Udp.Host = _udpHost.Text.Trim();
        _config.Udp.Port = PortOr(_udpPort, _config.Udp.Port);

        _config.Tcp.Enabled = _tcp.Enabled.IsChecked == true;
        _config.Tcp.ListenPort = PortOr(_tcpPort, _config.Tcp.ListenPort);

        _config.Serial.Enabled = _serial.Enabled.IsChecked == true;
        _config.Serial.Port = OutputsPage.ExtractComPort(_serialPort.Text);
        _config.Serial.Baud = BaudOr(_baud, _config.Serial.Baud);
        Revert();   // normalizes the fields to the saved values
    }

    // Refill every field from the saved config.
    public void Revert()
    {
        var m = _config.Mqtt;
        _mqtt.Enabled.IsChecked = m.Enabled;
        _host.Text = m.Host;
        _port.Text = m.Port.ToString();
        _username.Text = m.Username;
        _password.Password = m.Password;
        _topic.Text = m.TopicRoot;
        _tls.IsChecked = m.UseTls;
        _discovery.IsChecked = m.HaDiscoveryEnabled;

        _udp.Enabled.IsChecked = _config.Udp.Enabled;
        _udpHost.Text = _config.Udp.Host;
        _udpPort.Text = _config.Udp.Port.ToString();

        _tcp.Enabled.IsChecked = _config.Tcp.Enabled;
        _tcpPort.Text = _config.Tcp.ListenPort.ToString();

        _serial.Enabled.IsChecked = _config.Serial.Enabled;
        _serialPort.Text = _config.Serial.Port;
        _baud.Text = _config.Serial.Baud.ToString();
        RefreshStatus();
    }

    // True when Apply would write something different into the config.
    public bool HasChanges
    {
        get
        {
            var m = _config.Mqtt;
            return _mqtt.Enabled.IsChecked != m.Enabled
                || _host.Text.Trim() != m.Host
                || PortOr(_port, m.Port) != m.Port
                || _username.Text != m.Username
                || _password.Password != m.Password
                || _topic.Text.Trim() != m.TopicRoot
                || _tls.IsChecked != m.UseTls
                || _discovery.IsChecked != m.HaDiscoveryEnabled
                || _udp.Enabled.IsChecked != _config.Udp.Enabled
                || _udpHost.Text.Trim() != _config.Udp.Host
                || PortOr(_udpPort, _config.Udp.Port) != _config.Udp.Port
                || _tcp.Enabled.IsChecked != _config.Tcp.Enabled
                || PortOr(_tcpPort, _config.Tcp.ListenPort) != _config.Tcp.ListenPort
                || _serial.Enabled.IsChecked != _config.Serial.Enabled
                || OutputsPage.ExtractComPort(_serialPort.Text) != _config.Serial.Port
                || BaudOr(_baud, _config.Serial.Baud) != _config.Serial.Baud;
        }
    }

    static int PortOr(TextBox box, int fallback) => OutputsPage.ValidPort(box.Text) ? int.Parse(box.Text.Trim()) : fallback;
    static int BaudOr(TextBox box, int fallback) => OutputsPage.ValidBaud(box.Text) ? int.Parse(box.Text.Trim()) : fallback;

    // Hooks for the screenshot run.
    internal void DemoSetText(string field, string text) => (field == "udp.host" ? _udpHost : _host).Text = text;
    internal void DemoExpandAll() { foreach (var c in new[] { _mqtt, _udp, _tcp, _serial }) c.IsExpanded = true; }
}

// One sink card in the style of a Windows 11 Settings expander: header with
// name, status dot and text, enable switch and chevron; clicking the header
// opens the body with the label/field rows below a separator.
sealed class OutputExpander : Border
{
    public readonly CheckBox Enabled;
    readonly Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _chevron = new() { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    readonly Grid _body = new() { Margin = new Thickness(16, 6, 16, 6) };
    readonly Border _bodyHost;
    bool _expanded;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            _bodyHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Text = char.ConvertFromUtf32(value ? 0xE70E : 0xE70D);   // ChevronUp / ChevronDown
        }
    }

    public OutputExpander(string name, Action changed)
    {
        Style = (Style)Application.Current.FindResource("Card");
        Enabled = new CheckBox { Style = (Style)Application.Current.FindResource("Switch"), Margin = new Thickness(12, 0, 0, 0) };
        Enabled.Checked += (_, _) => changed();
        Enabled.Unchecked += (_, _) => changed();

        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Width = 64, VerticalAlignment = VerticalAlignment.Center };
        var head = new DockPanel { MinHeight = 52, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        var headHost = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(16, 0, 16, 0), Child = head };
        DockPanel.SetDock(title, Dock.Left);
        DockPanel.SetDock(_dot, Dock.Left);
        DockPanel.SetDock(_chevron, Dock.Right);
        DockPanel.SetDock(Enabled, Dock.Right);
        head.Children.Add(title);
        head.Children.Add(_dot);
        head.Children.Add(_chevron);
        head.Children.Add(Enabled);
        head.Children.Add(_status);
        // The switch handles its own clicks; everywhere else the header toggles.
        head.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, Enabled)) return;
            IsExpanded = !IsExpanded;
        };
        headHost.MouseEnter += (_, _) => headHost.SetResourceReference(BackgroundProperty, "SubtleFillColorSecondaryBrush");
        headHost.MouseLeave += (_, _) => headHost.Background = Brushes.Transparent;

        // One label width across all cards (shared size scope on the page).
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _bodyHost = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = _body };
        _bodyHost.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var stack = new StackPanel();
        stack.Children.Add(headHost);
        stack.Children.Add(_bodyHost);
        Child = stack;
        IsExpanded = false;
    }

    static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var cur = d; cur != null; cur = VisualTreeHelper.GetParent(cur))
            if (cur == ancestor) return true;
        return false;
    }

    public TextBox AddText(string label, Action changed)
    {
        var box = new TextBox();
        box.TextChanged += (_, _) => changed();
        AddRow(label, box);
        return box;
    }

    public CheckBox AddCheck(string text, Action changed)
    {
        var box = new CheckBox { Content = text };
        box.Checked += (_, _) => changed();
        box.Unchecked += (_, _) => changed();
        AddRow("", box);
        return box;
    }

    public void AddRow(string label, FrameworkElement field)
    {
        int row = _body.RowDefinitions.Count;
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 16, 2) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        field.Margin = new Thickness(0, 2, 0, 2);
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        _body.Children.Add(text);
        _body.Children.Add(field);
    }

    // Red border while the field's rule fails.
    public void SetInvalid(TextBox box, bool invalid)
    {
        if (invalid) box.SetResourceReference(BorderBrushProperty, "SystemFillColorCriticalBrush");
        else box.ClearValue(BorderBrushProperty);
    }

    // Green when running, red on errors, grey when off.
    public void SetStatus(string text, StatusKind kind)
    {
        _status.Text = text;
        _dot.SetResourceReference(Shape.FillProperty, kind switch
        {
            StatusKind.Good => "SystemFillColorSuccessBrush",
            StatusKind.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush",
        });
        _status.SetResourceReference(TextBlock.ForegroundProperty,
            kind == StatusKind.Error ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
    }
}
