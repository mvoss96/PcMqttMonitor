using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using MQTTnet;

// Single window with two tabs: live sensor readings and settings.
sealed class MainWindow : Form
{
    readonly TabControl _tabs;

    // ── Sensors tab ──────────────────────────────────────────────────────────────
    readonly TextBox _sensorsText;

    // ── Settings tab ─────────────────────────────────────────────────────────────
    readonly string _configPath;
    readonly AppConfig _config;
    readonly TextBox _host;
    readonly NumericUpDown _port;
    readonly TextBox _username;
    readonly TextBox _password;
    readonly TextBox _topic;
    readonly NumericUpDown _interval;
    readonly CheckBox _debugEnabled;
    readonly List<(CheckBox Box, Action<SensorConfig, bool> Setter)> _sensorBoxes = new();

    public MainWindow(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _config = config;

        Text = "PC MQTT Monitor";
        Size = new Size(460, 580);
        MinimumSize = new Size(380, 460);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(_tabs);

        // ── Tab 1: Sensors ───────────────────────────────────────────────────────
        _sensorsText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Consolas", 9f),
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window
        };
        var sensorsPage = new TabPage("Sensors");
        sensorsPage.Controls.Add(_sensorsText);
        _tabs.TabPages.Add(sensorsPage);

        // ── Tab 2: Settings ──────────────────────────────────────────────────────
        var sensors = config.Sensors ?? new SensorConfig();

        _host         = new TextBox { Text = config.BrokerHost, Width = 220 };
        _port         = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = config.BrokerPort, Width = 220 };
        _username     = new TextBox { Text = config.Username, Width = 220 };
        _password     = new TextBox { Text = config.Password, Width = 220, UseSystemPasswordChar = true };
        _topic        = new TextBox { Text = config.TopicRoot, Width = 220 };
        _interval     = new NumericUpDown { Minimum = 0.5m, Maximum = 3600, Value = (decimal)config.PublishIntervalSeconds, DecimalPlaces = 1, Increment = 0.5m, Width = 220 };
        _debugEnabled = new CheckBox { Text = "Enable debug logging", Checked = config.DebugEnabled, AutoSize = true };

        var brokerTable = MakeTable(cols: 2);
        brokerTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        brokerTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddLabeledRow(brokerTable, "Host",         _host);
        AddLabeledRow(brokerTable, "Port",         _port);
        AddLabeledRow(brokerTable, "Username",     _username);
        AddLabeledRow(brokerTable, "Password",     _password);
        AddLabeledRow(brokerTable, "Topic Root",    _topic);
        AddLabeledRow(brokerTable, "Interval (s)", _interval);
        brokerTable.Controls.Add(_debugEnabled);
        brokerTable.SetColumnSpan(_debugEnabled, 2);

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

        var testBtn   = new Button { Text = "Test Connection", AutoSize = true };
        var saveBtn   = new Button { Text = "Save", Width = 75 };
        var cancelBtn = new Button { Text = "Cancel", Width = 75 };
        testBtn.Click   += OnTestConnection;
        saveBtn.Click   += OnSave;
        cancelBtn.Click += (_, _) => Hide();

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4)
        };
        buttonRow.Controls.AddRange(new Control[] { cancelBtn, saveBtn, testBtn });

        var settingsScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8)
        };

        var settingsLayout = MakeTable(cols: 1);
        settingsLayout.Controls.Add(MakeGroup("MQTT Broker", brokerTable));
        settingsLayout.Controls.Add(MakeGroup("Sensors", sensorsTable));
        settingsScroll.Controls.Add(settingsLayout);

        var settingsPage = new TabPage("Settings");
        settingsPage.Controls.Add(buttonRow);
        settingsPage.Controls.Add(settingsScroll);
        _tabs.TabPages.Add(settingsPage);
    }

    // ── Public API ────────────────────────────────────────────────────────────────

    public void ShowSensorsTab()
    {
        _tabs.SelectedIndex = 0;
        Show();
        BringToFront();
    }

    public void ShowSettingsTab()
    {
        _tabs.SelectedIndex = 1;
        Show();
        BringToFront();
    }

    // Called from the MQTT loop thread — use BeginInvoke to update on the UI thread.
    public void UpdateMetrics(MqttMetrics m)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (Visible && _tabs.SelectedIndex == 0)
                _sensorsText.Text = FormatMetrics(m);
        });
    }

    // Store latest metrics so the sensors tab shows data immediately when opened.
    MqttMetrics? _lastMetrics;
    public void SetLatestMetrics(MqttMetrics m)
    {
        _lastMetrics = m;
        UpdateMetrics(m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_tabs.SelectedIndex == 0 && _lastMetrics != null)
            _sensorsText.Text = FormatMetrics(_lastMetrics);
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

    // ── Sensors formatting ────────────────────────────────────────────────────────

    static string FormatMetrics(MqttMetrics m)
    {
        var sb = new StringBuilder();

        if (m.Cpu != null)
        {
            sb.AppendLine($"CPU: {m.Cpu.Name}");
            AddRow(sb, "Load",          Format(m.Cpu.Load, "%"));
            AddRow(sb, "Temperature",   Format(m.Cpu.TempC, "°C", "0.#"));
            AddRow(sb, "Package Power", Format(m.Cpu.PackagePowerW, "W", "0.#"));
            AddRow(sb, "Core Voltage",  Format(m.Cpu.CoreVoltageV, "V", "0.###"));
            sb.AppendLine();
        }

        if (m.Gpu != null)
        {
            sb.AppendLine($"GPU: {m.Gpu.Name}");
            AddRow(sb, "Load",        Format(m.Gpu.Load, "%"));
            AddRow(sb, "Temperature", Format(m.Gpu.TempC, "°C", "0.#"));
            AddRow(sb, "Board Power", Format(m.Gpu.BoardPowerW, "W", "0.#"));
            AddRow(sb, "Fan",         Format(m.Gpu.FanRpm, "RPM", "0"));
            AddRow(sb, "VRAM Load",   Format(m.Gpu.MemoryLoad, "%"));
            AddRow(sb, "VRAM Used",   Format(m.Gpu.MemoryUsedMb, "MB", "0"));
            AddRow(sb, "VRAM Total",  Format(m.Gpu.MemoryTotalMb, "MB", "0"));
            sb.AppendLine();
        }

        if (m.Ram != null)
        {
            sb.AppendLine("RAM");
            AddRow(sb, "Load",  Format(m.Ram.Load, "%"));
            AddRow(sb, "Used",  Format(m.Ram.UsedGb, "GB", "0.#"));
            AddRow(sb, "Total", Format(m.Ram.TotalGb, "GB", "0.#"));
            sb.AppendLine();
        }

        if (m.Motherboard != null)
        {
            sb.AppendLine($"Motherboard: {m.Motherboard.Name}");
            sb.AppendLine();
        }

        if (m.Drives != null)
        {
            sb.AppendLine("Drives");
            foreach (var d in m.Drives)
            {
                sb.AppendLine($"  {d.Name}");
                AddRow(sb, "  Used",  Format(d.UsedGb,  "GB", "0.#"), indent: 4);
                AddRow(sb, "  Free",  Format(d.FreeGb,  "GB", "0.#"), indent: 4);
                AddRow(sb, "  Total", Format(d.TotalGb, "GB", "0.#"), indent: 4);
            }
        }

        return sb.ToString();
    }

    static void AddRow(StringBuilder sb, string label, string? value, int indent = 2)
    {
        if (value == null) return;
        sb.AppendLine($"{new string(' ', indent)}{label,-16}{value}");
    }

    static string? Format(int? v, string unit)
        => v == null ? null : $"{v} {unit}";

    static string? Format(float? v, string unit, string fmt = "0.##")
        => v == null ? null : $"{v.Value.ToString(fmt, CultureInfo.InvariantCulture)} {unit}";

    // ── Settings helpers ──────────────────────────────────────────────────────────

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
            MessageBox.Show("Topic Root cannot be empty.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _config.BrokerHost             = _host.Text.Trim();
        _config.BrokerPort             = (int)_port.Value;
        _config.Username               = _username.Text;
        _config.Password               = _password.Text;
        _config.TopicRoot              = _topic.Text.Trim();
        _config.PublishIntervalSeconds = (double)_interval.Value;
        _config.DebugEnabled           = _debugEnabled.Checked;

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
}
