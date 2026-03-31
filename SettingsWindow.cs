using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using MQTTnet;

// GUI editor for config.json — broker settings, sensor toggles, and a connection test.
sealed class SettingsWindow : Form
{
    readonly string _configPath;
    readonly AppConfig _config;

    // Broker fields
    readonly TextBox _host;
    readonly NumericUpDown _port;
    readonly TextBox _username;
    readonly TextBox _password;
    readonly TextBox _topic;
    readonly NumericUpDown _interval;
    readonly CheckBox _debugEnabled;

    // Each sensor checkbox paired with the setter that writes its value back to SensorConfig.
    readonly List<(CheckBox Box, Action<SensorConfig, bool> Setter)> _sensorBoxes = new();

    public SettingsWindow(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _config = config;

        Text = "PC MQTT Monitor — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        Padding = new Padding(12);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var sensors = config.Sensors ?? new SensorConfig();

        // ── Broker group ────────────────────────────────────────────────────────
        _host     = new TextBox { Text = config.BrokerHost, Width = 220 };
        _port     = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = config.BrokerPort, Width = 220 };
        _username = new TextBox { Text = config.Username, Width = 220 };
        _password = new TextBox { Text = config.Password, Width = 220, UseSystemPasswordChar = true };
        _topic    = new TextBox { Text = config.Topic, Width = 220 };
        _interval = new NumericUpDown { Minimum = 0.5m, Maximum = 3600, Value = (decimal)config.PublishIntervalSeconds, DecimalPlaces = 1, Increment = 0.5m, Width = 220 };
        _debugEnabled = new CheckBox { Text = "Enable debug logging", Checked = config.DebugEnabled, AutoSize = true };

        var brokerTable = MakeTable(cols: 2);
        brokerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        brokerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddLabeledRow(brokerTable, "Host",        _host);
        AddLabeledRow(brokerTable, "Port",        _port);
        AddLabeledRow(brokerTable, "Username",    _username);
        AddLabeledRow(brokerTable, "Password",    _password);
        AddLabeledRow(brokerTable, "Topic",       _topic);
        AddLabeledRow(brokerTable, "Interval (s)", _interval);
        // Debug checkbox spans both columns
        brokerTable.Controls.Add(_debugEnabled);
        brokerTable.SetColumnSpan(_debugEnabled, 2);

        var brokerGroup = MakeGroup("MQTT Broker", brokerTable);

        // ── Sensors group ────────────────────────────────────────────────────────
        var sensorsTable = MakeTable(cols: 2);
        sensorsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sensorsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddSensorBox(sensorsTable, sensors, "CPU Load",          c => c.CpuLoad,         (c, v) => c.CpuLoad = v);
        AddSensorBox(sensorsTable, sensors, "CPU Temp",          c => c.CpuTemp,         (c, v) => c.CpuTemp = v);
        AddSensorBox(sensorsTable, sensors, "CPU Package Power", c => c.CpuPackagePower, (c, v) => c.CpuPackagePower = v);
        AddSensorBox(sensorsTable, sensors, "CPU Core Voltage",  c => c.CpuCoreVoltage,  (c, v) => c.CpuCoreVoltage = v);
        AddSensorBox(sensorsTable, sensors, "GPU Load",          c => c.GpuLoad,         (c, v) => c.GpuLoad = v);
        AddSensorBox(sensorsTable, sensors, "GPU Temp",          c => c.GpuTemp,         (c, v) => c.GpuTemp = v);
        AddSensorBox(sensorsTable, sensors, "GPU Board Power",   c => c.GpuBoardPower,   (c, v) => c.GpuBoardPower = v);
        AddSensorBox(sensorsTable, sensors, "GPU Fan Speed",     c => c.GpuFanSpeed,     (c, v) => c.GpuFanSpeed = v);
        AddSensorBox(sensorsTable, sensors, "GPU Memory Load",   c => c.GpuMemoryLoad,   (c, v) => c.GpuMemoryLoad = v);
        AddSensorBox(sensorsTable, sensors, "GPU Memory Used",   c => c.GpuMemoryUsed,   (c, v) => c.GpuMemoryUsed = v);
        AddSensorBox(sensorsTable, sensors, "GPU Memory Total",  c => c.GpuMemoryTotal,  (c, v) => c.GpuMemoryTotal = v);
        AddSensorBox(sensorsTable, sensors, "RAM Load",          c => c.RamLoad,         (c, v) => c.RamLoad = v);
        AddSensorBox(sensorsTable, sensors, "RAM Used",          c => c.RamUsed,         (c, v) => c.RamUsed = v);
        AddSensorBox(sensorsTable, sensors, "RAM Total",         c => c.RamTotal,        (c, v) => c.RamTotal = v);
        AddSensorBox(sensorsTable, sensors, "Motherboard",       c => c.MotherboardName, (c, v) => c.MotherboardName = v);
        AddSensorBox(sensorsTable, sensors, "Drives",            c => c.Drives,          (c, v) => c.Drives = v);

        var sensorsGroup = MakeGroup("Sensors", sensorsTable);

        // ── Buttons ──────────────────────────────────────────────────────────────
        var testBtn   = new Button { Text = "Test Connection", AutoSize = true };
        var saveBtn   = new Button { Text = "Save", Width = 75 };
        var cancelBtn = new Button { Text = "Cancel", Width = 75, DialogResult = DialogResult.Cancel };

        testBtn.Click += OnTestConnection;
        saveBtn.Click += OnSave;

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 6, 0, 0)
        };
        buttonRow.Controls.AddRange(new Control[] { cancelBtn, saveBtn, testBtn });

        // ── Main layout ──────────────────────────────────────────────────────────
        var main = MakeTable(cols: 1);
        main.Controls.Add(brokerGroup);
        main.Controls.Add(sensorsGroup);
        main.Controls.Add(buttonRow);
        Controls.Add(main);

        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    static TableLayoutPanel MakeTable(int cols) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = cols,
        Padding = new Padding(4)
    };

    static GroupBox MakeGroup(string title, Control content) => new()
    {
        Text = title,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8),
        Controls = { content }
    };

    static void AddLabeledRow(TableLayoutPanel table, string label, Control control)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 6, 3)
        });
        table.Controls.Add(control);
    }

    void AddSensorBox(TableLayoutPanel table, SensorConfig sensors, string label,
        Func<SensorConfig, bool> getter, Action<SensorConfig, bool> setter)
    {
        var box = new CheckBox { Text = label, Checked = getter(sensors), AutoSize = true };
        table.Controls.Add(box);
        _sensorBoxes.Add((box, setter));
    }

    // ── Save ──────────────────────────────────────────────────────────────────────

    void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_host.Text))
        {
            MessageBox.Show("Broker host cannot be empty.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(_topic.Text))
        {
            MessageBox.Show("Topic cannot be empty.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.BrokerHost              = _host.Text.Trim();
        _config.BrokerPort              = (int)_port.Value;
        _config.Username                = _username.Text;
        _config.Password                = _password.Text;
        _config.Topic                   = _topic.Text.Trim();
        _config.PublishIntervalSeconds  = (double)_interval.Value;
        _config.DebugEnabled            = _debugEnabled.Checked;

        _config.Sensors ??= new SensorConfig();
        foreach (var (box, setter) in _sensorBoxes)
            setter(_config.Sensors, box.Checked);

        var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);

        MessageBox.Show(
            "Settings saved. Restart the app to apply changes.",
            "Settings Saved",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

        Hide();
    }

    // ── Test Connection ───────────────────────────────────────────────────────────

    async void OnTestConnection(object? sender, EventArgs e)
    {
        var btn = (Button)sender!;
        btn.Enabled = false;
        btn.Text = "Testing...";

        try
        {
            var factory = new MqttClientFactory();
            using var client = factory.CreateMqttClient();
            var options = factory.CreateClientOptionsBuilder()
                .WithTcpServer(_host.Text.Trim(), (int)_port.Value)
                .WithCredentials(_username.Text, _password.Text)
                .Build();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(options, timeout.Token);
            await client.DisconnectAsync();

            MessageBox.Show(
                $"Connected to {_host.Text}:{_port.Value} successfully.",
                "Test Connection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Connection failed:\n{ex.Message}",
                "Test Connection",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            btn.Enabled = true;
            btn.Text = "Test Connection";
        }
    }

    // Closing hides the window so it can be reopened from the tray.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
