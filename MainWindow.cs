using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using MQTTnet;

// Single window with two tabs: live sensor readings and settings.
sealed class MainWindow : Form
{
    readonly TabControl _tabs;

    // ── Sensors tab ──────────────────────────────────────────────────────────────
    readonly Panel _sensorsOuter;

    // Built once; on each refresh we only update label text/colors and bar widths.
    Action<MqttMetrics>? _sensorUpdater;
    string? _sensorFingerprint; // structural hash — rebuild only when hardware changes

    // ── Settings tab ─────────────────────────────────────────────────────────────
    readonly string _configPath;
    readonly AppConfig _config;
    Label? _connStatus;         // live indicator in the Settings tab
    readonly TextBox _host;
    readonly NumericUpDown _port;
    readonly TextBox _username;
    readonly TextBox _password;
    readonly TextBox _topic;
    readonly NumericUpDown _interval;
    readonly CheckBox _debugEnabled;
    readonly CheckBox _autoStart;
    readonly List<(CheckBox Box, Action<SensorConfig, bool> Setter)> _sensorBoxes = new();

    public MainWindow(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _config = config;

        var version = typeof(MainWindow).Assembly.GetName().Version;
        Text = version != null ? $"PC MQTT Monitor  v{version.ToString(3)}" : "PC MQTT Monitor";
        Icon = TrayApp.CreateIcon();
        Size = new Size(500, 700);
        MinimumSize = new Size(420, 500);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _tabs = new TabControl { Dock = DockStyle.Fill };
        Controls.Add(_tabs);

        // ── Tab 1: Sensors ───────────────────────────────────────────────────────
        _sensorsOuter = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = SystemColors.Window,
            Padding = new Padding(16, 12, 16, 12)
        };
        // Reflect DoubleBuffered=true — Panel exposes it only as protected.
        typeof(Panel)
            .GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_sensorsOuter, true);
        _sensorsOuter.ClientSizeChanged += (_, _) =>
        {
            if (_sensorsOuter.Controls.Count > 0)
                _sensorsOuter.Controls[0].Width =
                    _sensorsOuter.ClientSize.Width - _sensorsOuter.Padding.Horizontal;
        };

        var sensorsPage = new TabPage("Sensors");
        sensorsPage.Controls.Add(_sensorsOuter);
        _tabs.TabPages.Add(sensorsPage);
        ShowSensorsPlaceholder("Opening sensors — this may take a few seconds...");

        // ── Tab 2: Settings ──────────────────────────────────────────────────────
        var sensors = config.Sensors ?? new SensorConfig();

        _host         = new TextBox { Text = config.BrokerHost, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _port         = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = config.BrokerPort, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _username     = new TextBox { Text = config.Username, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _password     = new TextBox { Text = config.Password, Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true };
        _topic        = new TextBox { Text = config.TopicRoot, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _interval     = new NumericUpDown { Minimum = 0.5m, Maximum = 3600, Value = (decimal)config.PublishIntervalSeconds, DecimalPlaces = 1, Increment = 0.5m, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _debugEnabled = new CheckBox { Text = "Enable debug logging", Checked = config.DebugEnabled, AutoSize = true };
        _autoStart    = new CheckBox { Text = "Start with Windows",   Checked = IsAutoStartEnabled(),  AutoSize = true };

        // ── MQTT Broker section ──────────────────────────────────────────────────
        _connStatus = new Label
        {
            Text = "○  Not connected",
            ForeColor = Color.Gray,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };
        var brokerTable = MakeSettingsTable();
        brokerTable.Controls.Add(_connStatus);
        brokerTable.SetColumnSpan(_connStatus, 2);
        AddRow(brokerTable, "Host",       _host);
        AddRow(brokerTable, "Port",       _port);
        AddRow(brokerTable, "Username",   _username);
        AddRow(brokerTable, "Password",   _password);
        AddRow(brokerTable, "Topic root", _topic);

        var testBtn = new Button { Text = "Test && Apply", AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
        testBtn.Click += OnTestAndApply;
        brokerTable.Controls.Add(testBtn);
        brokerTable.SetColumnSpan(testBtn, 2);

        var mqttHint = new Label
        {
            Text = "Tests connection, saves these settings and restarts the app.",
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 7.5f),
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 4)
        };
        brokerTable.Controls.Add(mqttHint);
        brokerTable.SetColumnSpan(mqttHint, 2);

        // ── General section ──────────────────────────────────────────────────────
        var generalTable = MakeSettingsTable();
        AddRow(generalTable, "Interval (s)", _interval);
        generalTable.Controls.Add(_autoStart);
        generalTable.SetColumnSpan(_autoStart, 2);
        generalTable.Controls.Add(_debugEnabled);
        generalTable.SetColumnSpan(_debugEnabled, 2);

        // ── Sensors section ──────────────────────────────────────────────────────
        var sensorsTable = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = Padding.Empty
        };
        sensorsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        sensorsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddSensorGroup(sensorsTable, "CPU");
        AddSensorBox(sensorsTable, sensors, "Load",          c => c.CpuLoad,         (c, v) => c.CpuLoad = v);
        AddSensorBox(sensorsTable, sensors, "Temperature",   c => c.CpuTemp,         (c, v) => c.CpuTemp = v);
        AddSensorBox(sensorsTable, sensors, "Package power", c => c.CpuPackagePower, (c, v) => c.CpuPackagePower = v);
        AddSensorBox(sensorsTable, sensors, "Core voltage",  c => c.CpuCoreVoltage,  (c, v) => c.CpuCoreVoltage = v);

        AddSensorGroup(sensorsTable, "GPU");
        AddSensorBox(sensorsTable, sensors, "Load",         c => c.GpuLoad,        (c, v) => c.GpuLoad = v);
        AddSensorBox(sensorsTable, sensors, "Temperature",  c => c.GpuTemp,        (c, v) => c.GpuTemp = v);
        AddSensorBox(sensorsTable, sensors, "Board power",  c => c.GpuBoardPower,  (c, v) => c.GpuBoardPower = v);
        AddSensorBox(sensorsTable, sensors, "Fan speed",    c => c.GpuFanSpeed,    (c, v) => c.GpuFanSpeed = v);
        AddSensorBox(sensorsTable, sensors, "Memory load",  c => c.GpuMemoryLoad,  (c, v) => c.GpuMemoryLoad = v);
        AddSensorBox(sensorsTable, sensors, "Memory used",  c => c.GpuMemoryUsed,  (c, v) => c.GpuMemoryUsed = v);
        AddSensorBox(sensorsTable, sensors, "Memory total", c => c.GpuMemoryTotal, (c, v) => c.GpuMemoryTotal = v);

        AddSensorGroup(sensorsTable, "RAM");
        AddSensorBox(sensorsTable, sensors, "Load",  c => c.RamLoad,  (c, v) => c.RamLoad = v);
        AddSensorBox(sensorsTable, sensors, "Used",  c => c.RamUsed,  (c, v) => c.RamUsed = v);
        AddSensorBox(sensorsTable, sensors, "Total", c => c.RamTotal, (c, v) => c.RamTotal = v);

        AddSensorGroup(sensorsTable, "Other");
        AddSensorBox(sensorsTable, sensors, "Motherboard",       c => c.MotherboardName,  (c, v) => c.MotherboardName  = v);
        AddSensorBox(sensorsTable, sensors, "Drives",            c => c.Drives,           (c, v) => c.Drives           = v);
        AddSensorBox(sensorsTable, sensors, "Network Upload",    c => c.NetworkUpload,    (c, v) => c.NetworkUpload    = v);
        AddSensorBox(sensorsTable, sensors, "Network Download",  c => c.NetworkDownload,  (c, v) => c.NetworkDownload  = v);

        // ── Outer layout ─────────────────────────────────────────────────────────
        var settingsMain = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = Padding.Empty
        };
        settingsMain.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsMain.Controls.Add(MakeSectionHeader("MQTT Broker"));
        settingsMain.Controls.Add(MakeSeparator());
        settingsMain.Controls.Add(brokerTable);
        settingsMain.Controls.Add(MakeSectionHeader("General"));
        settingsMain.Controls.Add(MakeSeparator());
        settingsMain.Controls.Add(generalTable);
        settingsMain.Controls.Add(MakeSectionHeader("Sensors to publish"));
        settingsMain.Controls.Add(MakeSeparator());
        settingsMain.Controls.Add(sensorsTable);

        var saveBtn  = new Button { Text = "Save", Width = 75 };
        var closeBtn = new Button { Text = "Close", Width = 75 };
        saveBtn.Click  += OnSave;
        closeBtn.Click += (_, _) => Hide();

        var buttonRow = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Padding = new Padding(4)
        };
        buttonRow.Controls.AddRange(new Control[] { closeBtn, saveBtn });

        var settingsScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };
        settingsScroll.Controls.Add(settingsMain);

        var settingsPage = new TabPage("Settings");
        settingsPage.Controls.Add(buttonRow);
        settingsPage.Controls.Add(settingsScroll);
        _tabs.TabPages.Add(settingsPage);

        // Force handle creation so BeginInvoke works before the window is first shown.
        _ = Handle;
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

    MqttMetrics? _lastMetrics;

    public void SetLatestMetrics(MqttMetrics m)
    {
        _lastMetrics = m;
        if (!IsHandleCreated) return;
        BeginInvoke(() => RefreshSensors(m));
    }

    public void UpdateMetrics(MqttMetrics m)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (Visible && _tabs.SelectedIndex == 0)
                RefreshSensors(m);
        });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_tabs.SelectedIndex == 0 && _lastMetrics != null)
            RefreshSensors(_lastMetrics);
    }

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

    // ── Sensors UI ────────────────────────────────────────────────────────────────

    // Custom-drawn, double-buffered progress bar — avoids child-panel resize flicker.
    sealed class ProgressPanel : Panel
    {
        int _percent;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Percent
        {
            get => _percent;
            set { _percent = Math.Clamp(value, 0, 100); Invalidate(); }
        }
        public ProgressPanel() { DoubleBuffered = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(224, 224, 224));
            if (_percent > 0)
                e.Graphics.FillRectangle(
                    new SolidBrush(Color.FromArgb(0, 120, 212)),
                    0, 0, Width * _percent / 100, Height);
        }
    }

    void ShowSensorsPlaceholder(string message)
    {
        var lbl = new Label
        {
            Text      = message,
            ForeColor = Color.Gray,
            Font      = new Font("Segoe UI", 10f),
            AutoSize  = false,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        foreach (Control c in _sensorsOuter.Controls) c.Dispose();
        _sensorsOuter.Controls.Clear();
        _sensorsOuter.Controls.Add(lbl);
    }

    // Which top-level sections are present — used to detect structural changes.
    static string MakeFingerprint(MqttMetrics m) =>
        $"{m.Cpu != null}|{m.Gpu != null}|{m.Ram != null}|{m.Drives?.Count ?? 0}|{m.Network != null}";

    // Called every refresh interval.  Only rebuilds controls when hardware
    // appears/disappears; otherwise updates labels and bar widths in-place.
    void RefreshSensors(MqttMetrics m)
    {
        var fp = MakeFingerprint(m);
        if (_sensorUpdater == null || fp != _sensorFingerprint)
            BuildSensorView(m, fp);   // first run or hardware changed
        else
            _sensorUpdater(m);        // fast path — no control creation
    }

    // Builds the full control tree and captures per-row update delegates.
    void BuildSensorView(MqttMetrics m, string fp)
    {
        var updaters = new List<Action<MqttMetrics>>();

        var main = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = Padding.Empty,
            Margin = Padding.Empty
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (m.Cpu    != null) main.Controls.Add(BuildSection("CPU",    m.Cpu.Name, CpuRowDefs(),           updaters));
        if (m.Gpu    != null) main.Controls.Add(BuildSection("GPU",    m.Gpu.Name, GpuRowDefs(),           updaters));
        if (m.Ram    != null) main.Controls.Add(BuildSection("RAM",    null,        RamRowDefs(),           updaters));
        if (m.Drives?.Count > 0) main.Controls.Add(BuildSection("Drives",  null, DriveRowDefs(m.Drives), updaters));
        if (m.Network != null)   main.Controls.Add(BuildSection("Network", null, NetworkRowDefs(),        updaters));

        main.Width = _sensorsOuter.ClientSize.Width - _sensorsOuter.Padding.Horizontal;

        _sensorsOuter.SuspendLayout();
        foreach (Control c in _sensorsOuter.Controls) c.Dispose();
        _sensorsOuter.Controls.Clear();
        _sensorsOuter.Controls.Add(main);
        _sensorsOuter.ResumeLayout();

        _sensorFingerprint = fp;
        _sensorUpdater = metrics => { foreach (var u in updaters) u(metrics); };

        // Populate initial values immediately after building.
        _sensorUpdater(m);
    }

    // Builds one section (e.g. "CPU") and registers per-row updaters.
    // A row def that says HasBar=true gets a flat progress bar in the middle column.
    static TableLayoutPanel BuildSection(string category, string? subtitle,
        IReadOnlyList<RowDef> rowDefs, List<Action<MqttMetrics>> updaters)
    {
        var tl = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 20),
            Padding = Padding.Empty
        };
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); // label
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,  100)); // bar / spacer
        tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115)); // value

        // Header
        var headerText = subtitle != null ? $"{category}  ·  {subtitle}" : category;
        var header = new Label
        {
            Text = headerText,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 26, 26),
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 3)
        };
        tl.Controls.Add(header);
        tl.SetColumnSpan(header, 3);
        tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        // Separator
        var sep = new Panel { BackColor = Color.FromArgb(220, 220, 220), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        tl.Controls.Add(sep);
        tl.SetColumnSpan(sep, 3);
        tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 9));

        // One row per metric
        foreach (var def in rowDefs)
        {
            tl.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

            tl.Controls.Add(new Label
            {
                Text = def.Label,
                ForeColor = Color.FromArgb(96, 96, 96),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 8, 2)
            });

            // Bar (middle column) — ProgressPanel is custom-drawn and double-buffered,
            // so updating Percent just calls Invalidate() with no child-resize flicker.
            if (def.HasBar)
            {
                var bar = new ProgressPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 9, 0, 9) };
                tl.Controls.Add(bar);

                var capturedBar = bar;
                var capturedDef = def;
                var valueLbl    = AddValueLabel(tl);

                updaters.Add(metrics =>
                {
                    var (pct, val, col) = capturedDef.GetData(metrics);
                    capturedBar.Percent = pct ?? 0;
                    valueLbl.Text       = val ?? "—";
                    valueLbl.ForeColor  = col;
                });
            }
            else
            {
                tl.Controls.Add(new Panel()); // empty spacer

                var capturedDef = def;
                var valueLbl    = AddValueLabel(tl);

                updaters.Add(metrics =>
                {
                    var (_, val, col) = capturedDef.GetData(metrics);
                    valueLbl.Text      = val ?? "—";
                    valueLbl.ForeColor = col;
                });
            }
        }

        return tl;
    }

    // Adds and returns the right-aligned value label for a row.
    static Label AddValueLabel(TableLayoutPanel tl)
    {
        var lbl = new Label
        {
            Text = "—",
            ForeColor = SystemColors.ControlText,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(6, 0, 0, 2)
        };
        tl.Controls.Add(lbl);
        return lbl;
    }

    // Temperature threshold colouring: green → amber → red.
    static Color TempColor(float? c) =>
        c == null ? SystemColors.ControlText :
        c <  60   ? Color.FromArgb( 16, 124,  16) :
        c <  80   ? Color.FromArgb(196,  98,   0) :
                    Color.FromArgb(196,  43,  28);

    // ── Row definitions ───────────────────────────────────────────────────────────

    // A row definition: static label text, whether it has a progress bar,
    // and a delegate that extracts (bar %, display value, colour) from metrics.
    record RowDef(string Label, bool HasBar, Func<MqttMetrics, (int? Pct, string? Value, Color Color)> GetData);

    static (int?, string?, Color) IntVal(int? v, string unit) =>
        v == null ? (null, null, SystemColors.ControlText)
                  : (v, $"{v} {unit}", SystemColors.ControlText);

    static (int?, string?, Color) FloatVal(float? v, string unit, string fmt = "0.#", Color? col = null) =>
        v == null ? (null, null, SystemColors.ControlText)
                  : (null, $"{v.Value.ToString(fmt, CultureInfo.InvariantCulture)} {unit}", col ?? SystemColors.ControlText);

    static IReadOnlyList<RowDef> CpuRowDefs() =>
    [
        new("Load",          true,  m => IntVal  (m.Cpu?.Load,          "%"                                          )),
        new("Temperature",   false, m => FloatVal(m.Cpu?.TempC,         "°C", "0.#",   TempColor(m.Cpu?.TempC)       )),
        new("Package Power", false, m => FloatVal(m.Cpu?.PackagePowerW, "W",  "0.#"                                  )),
        new("Core Voltage",  false, m => FloatVal(m.Cpu?.CoreVoltageV,  "V",  "0.###"                                )),
    ];

    static IReadOnlyList<RowDef> GpuRowDefs() =>
    [
        new("Load",        true,  m => IntVal  (m.Gpu?.Load,        "%"                                        )),
        new("Temperature", false, m => FloatVal(m.Gpu?.TempC,       "°C", "0.#", TempColor(m.Gpu?.TempC)      )),
        new("Board Power", false, m => FloatVal(m.Gpu?.BoardPowerW, "W",  "0.#"                                )),
        new("Fan",         false, m => FloatVal(m.Gpu?.FanRpm,      "RPM","0"                                  )),
        new("VRAM Load",   true,  m => IntVal  (m.Gpu?.MemoryLoad,  "%"                                        )),
        new("VRAM",        false, m => GpuVramRow(m.Gpu)                                                        ),
    ];

    static (int?, string?, Color) GpuVramRow(GpuMetrics? g)
    {
        if (g == null || (g.MemoryUsedMb == null && g.MemoryTotalMb == null))
            return (null, null, SystemColors.ControlText);
        var used  = g.MemoryUsedMb  != null ? $"{g.MemoryUsedMb.Value  / 1024f:0.#}" : "?";
        var total = g.MemoryTotalMb != null ? $"{g.MemoryTotalMb.Value / 1024f:0.#}" : "?";
        return (null, $"{used} / {total} GB", SystemColors.ControlText);
    }

    static IReadOnlyList<RowDef> RamRowDefs() =>
    [
        new("Load",        true,  m => IntVal(m.Ram?.Load, "%")),
        new("Used / Total",false, m => RamUsageRow(m.Ram)),
    ];

    static (int?, string?, Color) RamUsageRow(RamMetrics? r)
    {
        if (r == null || (r.UsedGb == null && r.TotalGb == null))
            return (null, null, SystemColors.ControlText);
        var used  = r.UsedGb  != null ? $"{r.UsedGb.Value.ToString("0.#",  CultureInfo.InvariantCulture)}" : "?";
        var total = r.TotalGb != null ? $"{r.TotalGb.Value.ToString("0.#", CultureInfo.InvariantCulture)}" : "?";
        return (null, $"{used} / {total} GB", SystemColors.ControlText);
    }

    static IReadOnlyList<RowDef> DriveRowDefs(List<StorageMetrics> drives) =>
        drives.Select(d => new RowDef(
            d.Name.TrimEnd('\\'),
            true,
            m =>
            {
                var drive = m.Drives?.FirstOrDefault(x => x.Name == d.Name);
                if (drive == null) return (null, null, SystemColors.ControlText);

                string text;
                if (drive.UsedGb != null && drive.TotalGb != null)
                {
                    var used  = drive.UsedGb.Value.ToString("0.#",  CultureInfo.InvariantCulture);
                    var total = drive.TotalGb.Value.ToString("0.#", CultureInfo.InvariantCulture);
                    text = $"{used} / {total} GB";
                }
                else if (drive.UsedGb != null)
                    text = $"{drive.UsedGb.Value.ToString("0.#", CultureInfo.InvariantCulture)} GB used";
                else if (drive.TotalGb != null)
                    text = $"{drive.TotalGb.Value.ToString("0.#", CultureInfo.InvariantCulture)} GB";
                else
                    return (null, null, SystemColors.ControlText);

                return (drive.UsedPercent, text, SystemColors.ControlText);

            }
        )).ToArray();

    static IReadOnlyList<RowDef> NetworkRowDefs() =>
    [
        new("Upload",   false, m => NetSpeedRow(m.Network?.UploadKbps)),
        new("Download", false, m => NetSpeedRow(m.Network?.DownloadKbps)),
    ];

    static (int?, string?, Color) NetSpeedRow(float? kbps)
    {
        if (kbps == null) return (null, null, SystemColors.ControlText);
        var text = kbps.Value >= 1024
            ? $"{kbps.Value / 1024f:0.##} MB/s"
            : $"{kbps.Value:0.#} KB/s";
        return (null, text, SystemColors.ControlText);
    }

    // ── Settings helpers ──────────────────────────────────────────────────────────

    // Two-column table: fixed label column on the left, stretching input on the right.
    static TableLayoutPanel MakeSettingsTable()
    {
        var t = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 4, 0, 8)
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static Label MakeSectionHeader(string title) => new()
    {
        Text = title,
        Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        ForeColor = Color.FromArgb(26, 26, 26),
        AutoSize = true,
        Margin = new Padding(0, 14, 0, 2)
    };

    static Panel MakeSeparator() => new()
    {
        Height = 1,
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(218, 218, 218),
        Margin = new Padding(0, 0, 0, 6)
    };

    static void AddRow(TableLayoutPanel table, string label, Control control)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 8, 3)
        });
        table.Controls.Add(control);
    }

    // Adds a full-width italic sub-heading row to the sensor checkbox grid.
    static void AddSensorGroup(TableLayoutPanel table, string title)
    {
        var lbl = new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = Color.FromArgb(80, 80, 80),
            AutoSize = true,
            Margin = new Padding(2, 8, 0, 2)
        };
        table.Controls.Add(lbl);
        table.SetColumnSpan(lbl, 2);
    }

    void AddSensorBox(TableLayoutPanel table, SensorConfig sensors, string label,
        Func<SensorConfig, bool> getter, Action<SensorConfig, bool> setter)
    {
        var box = new CheckBox { Text = label, Checked = getter(sensors), AutoSize = true, Margin = new Padding(4, 1, 4, 1) };
        table.Controls.Add(box);
        _sensorBoxes.Add((box, setter));
    }

    // Called from TrayApp (any thread) to update the status dot in the Settings tab.
    public void SetConnectionStatus(bool connected, string broker)
    {
        if (_connStatus == null) return;
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (connected)
            {
                _connStatus.Text      = $"●  Connected  ({broker})";
                _connStatus.ForeColor = Color.FromArgb(16, 124, 16);
            }
            else
            {
                _connStatus.Text      = "○  Not connected";
                _connStatus.ForeColor = Color.Gray;
            }
        });
    }

    // ── Save (General + Sensors only) ────────────────────────────────────────────

    void OnSave(object? sender, EventArgs e)
    {
        _config.PublishIntervalSeconds = (double)_interval.Value;
        _config.DebugEnabled           = _debugEnabled.Checked;
        _config.Sensors ??= new SensorConfig();
        foreach (var (box, setter) in _sensorBoxes)
            setter(_config.Sensors, box.Checked);

        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));

        ApplyAutoStart(_autoStart.Checked);

        MessageBox.Show("Settings applied.",
            "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Test & Apply (MQTT Broker settings) ──────────────────────────────────────
    // Tests the connection with the entered credentials; on success saves those
    // fields and signals the loop to reconnect. Settings are NOT saved on failure.

    // ── Autostart helpers ─────────────────────────────────────────────────────────
    // The app requires elevation (requireAdministrator manifest), so the HKCU\Run
    // registry key does not work — Windows silently skips elevated apps at logon.
    // Task Scheduler with /RL HIGHEST is the correct solution for elevated autostart.

    const string AutoStartTaskName = "PcMqttMonitor";

    static bool IsAutoStartEnabled()
    {
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "schtasks.exe",
            Arguments              = $"/Query /TN \"{AutoStartTaskName}\"",
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        });
        proc?.WaitForExit();
        return proc?.ExitCode == 0;
    }

    static void ApplyAutoStart(bool enable)
    {
        string args;
        if (enable)
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            // /SC ONLOGON  — trigger: current user logs on
            // /RL HIGHEST  — run with highest available privileges (elevation)
            // /DELAY       — small delay so the desktop and network are ready
            // /F           — overwrite if the task already exists
            args = $"/Create /F /TN \"{AutoStartTaskName}\" /TR \"\\\"{exePath}\\\"\" " +
                   $"/SC ONLOGON /RU \"{Environment.UserName}\" /RL HIGHEST /DELAY 0000:10";
        }
        else
        {
            args = $"/Delete /F /TN \"{AutoStartTaskName}\"";
        }

        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "schtasks.exe",
            Arguments       = args,
            CreateNoWindow  = true,
            UseShellExecute = false,
        });
        proc?.WaitForExit();
    }

    async void OnTestAndApply(object? sender, EventArgs e)
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

        var btn = (Button)sender!;
        btn.Enabled = false;
        btn.Text    = "Testing...";

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
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Connection failed:\n\n{ex.Message}\n\nSettings were not saved.",
                "Test & Apply", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally
        {
            btn.Enabled = true;
            btn.Text    = "Test & Apply";
        }

        // Connection succeeded — persist the MQTT settings and restart.
        _config.BrokerHost = _host.Text.Trim();
        _config.BrokerPort = (int)_port.Value;
        _config.Username   = _username.Text;
        _config.Password   = _password.Text;
        _config.TopicRoot  = _topic.Text.Trim();

        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));

        Application.Restart();
    }
}
