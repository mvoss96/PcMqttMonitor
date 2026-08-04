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
    readonly Panel _pauseBanner;

    // Set by TrayApp — routes pause/resume requests from the banner to the
    // central pause switch so tray menu, icon and banner stay in sync.
    public Action<bool>? PauseChangeRequested;

    // Owner-drawn dashboard — created on first reading, then updated via Invalidate().
    SensorsView? _sensorsView;

    // ── Settings tab ─────────────────────────────────────────────────────────────
    readonly string _configPath;
    readonly AppConfig _config;
    Label? _connStatus;         // live indicator in the Settings tab
    readonly TextBox _host;
    readonly NumericUpDown _port;
    readonly CheckBox _useTls;
    readonly TextBox _username;
    readonly TextBox _password;
    readonly TextBox _topic;
    readonly NumericUpDown _interval;
    readonly CheckBox _debugEnabled;
    readonly CheckBox _autoStart;
    readonly CheckBox _haDiscovery;
    readonly CheckBox _updateCheck;
    readonly List<(CheckBox Box, Action<SensorConfig, bool> Setter)> _sensorBoxes = new();

    // Settings-tab layout freeze (see constructor) — avoids a ~3 s relayout on every show.
    bool _settingsFrozen;
    Action? _freezeSettingsLayout;
    Action? _reflowSettingsLayout;

    // True while the window is shown AND the Sensors tab is active.  Maintained on the UI
    // thread and read from the background thread, so it must not touch live control state.
    volatile bool _sensorsTabActive;

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

        // Banner shown while publishing is paused (tray menu or the button here).
        // Added AFTER the fill-docked panel so DockStyle.Top wins the layout.
        _pauseBanner = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 38,
            BackColor = Color.FromArgb(255, 244, 199),
            Visible   = false,
            Padding   = new Padding(12, 0, 12, 0)
        };
        var pauseLabel = new Label
        {
            Text      = "Publishing is paused",
            AutoSize  = true,
            ForeColor = Color.FromArgb(102, 77, 3),
            Location  = new Point(12, 11)
        };
        var resumeBtn = new Button
        {
            Text     = "Resume",
            AutoSize = true,
            Anchor   = AnchorStyles.Top | AnchorStyles.Right
        };
        resumeBtn.Click += (_, _) => PauseChangeRequested?.Invoke(false);
        _pauseBanner.Controls.Add(pauseLabel);
        _pauseBanner.Controls.Add(resumeBtn);
        _pauseBanner.Resize += (_, _) =>
        {
            resumeBtn.Location = new Point(
                _pauseBanner.ClientSize.Width - resumeBtn.Width - 12,
                (_pauseBanner.ClientSize.Height - resumeBtn.Height) / 2);
        };

        var sensorsPage = new TabPage("Sensors");
        sensorsPage.Controls.Add(_sensorsOuter);
        sensorsPage.Controls.Add(_pauseBanner);
        _tabs.TabPages.Add(sensorsPage);
        ShowSensorsPlaceholder("Opening sensors — this may take a few seconds...");

        // ── Tab 2: Settings ──────────────────────────────────────────────────────
        var sensors = config.Sensors;

        _host         = new TextBox { Text = config.Mqtt.Host, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _port         = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = config.Mqtt.Port, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _useTls       = new CheckBox { Text = "Use TLS (typically port 8883)", Checked = config.Mqtt.UseTls, AutoSize = true };
        _username     = new TextBox { Text = config.Mqtt.Username, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _password     = new TextBox { Text = config.Mqtt.Password, Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true };
        _topic        = new TextBox { Text = config.Mqtt.TopicRoot, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _interval     = new NumericUpDown { Minimum = 0.5m, Maximum = 3600, Value = (decimal)config.General.PublishIntervalSeconds, DecimalPlaces = 1, Increment = 0.5m, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _debugEnabled = new CheckBox { Text = "Enable debug logging", Checked = config.General.DebugEnabled, AutoSize = true };
        _autoStart    = new CheckBox { Text = "Start with Windows",   Checked = false,                 AutoSize = true };
        _haDiscovery  = new CheckBox { Text = "Home Assistant MQTT Discovery", Checked = config.Mqtt.HaDiscoveryEnabled, AutoSize = true };
        _updateCheck  = new CheckBox { Text = "Notify about new versions (checks GitHub daily)", Checked = config.General.UpdateCheckEnabled, AutoSize = true };

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
        brokerTable.Controls.Add(_useTls);
        brokerTable.SetColumnSpan(_useTls, 2);
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
        generalTable.Controls.Add(_haDiscovery);
        generalTable.SetColumnSpan(_haDiscovery, 2);
        var discoveryHint = new Label
        {
            Text = "Auto-creates all sensors as one device in Home Assistant. Applies on Save; unchecking removes the device from HA.",
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 7.5f),
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(18, 0, 0, 4)
        };
        generalTable.Controls.Add(discoveryHint);
        generalTable.SetColumnSpan(discoveryHint, 2);
        generalTable.Controls.Add(_updateCheck);
        generalTable.SetColumnSpan(_updateCheck, 2);

        // Manual update check: button + inline result. The result label doubles
        // as a download link when a newer version is found.
        var updateStatus = new LinkLabel
        {
            Text     = $"Installed version: v{UpdateChecker.CurrentVersion.ToString(3)}",
            LinkArea = new LinkArea(0, 0),   // plain text until there is something to link to
            AutoSize = true,
            Margin   = new Padding(18, 4, 0, 0)
        };
        updateStatus.LinkClicked += (_, _) => TrayApp.OpenReleasesPage();

        var checkUpdateBtn = new Button { Text = "Check for updates", AutoSize = true, Margin = new Padding(18, 8, 0, 2) };
        checkUpdateBtn.Click += async (_, _) =>
        {
            checkUpdateBtn.Enabled = false;
            updateStatus.LinkArea  = new LinkArea(0, 0);
            updateStatus.Text      = "Checking...";
            try
            {
                var newer = await UpdateChecker.CheckAsync(CancellationToken.None);
                if (newer != null)
                {
                    const string linkText = "open download page";
                    updateStatus.Text     = $"Version {newer.ToString(3)} is available — {linkText}";
                    updateStatus.LinkArea = new LinkArea(updateStatus.Text.Length - linkText.Length, linkText.Length);
                }
                else
                {
                    updateStatus.Text = $"Up to date (v{UpdateChecker.CurrentVersion.ToString(3)})";
                }
            }
            catch (Exception ex)
            {
                updateStatus.Text = $"Check failed: {ex.Message}";
            }
            finally { checkUpdateBtn.Enabled = true; }
        };
        generalTable.Controls.Add(checkUpdateBtn);
        generalTable.SetColumnSpan(checkUpdateBtn, 2);
        generalTable.Controls.Add(updateStatus);
        generalTable.SetColumnSpan(updateStatus, 2);

        // ── Sensors section ──────────────────────────────────────────────────────
        // Each group: bold title with a horizontal rule extending to the right,
        // then checkboxes in a wrapping flow (auto-arrange, no rigid columns).
        var sensorsContainer = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, Padding = Padding.Empty
        };
        sensorsContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        FlowLayoutPanel AddGroup(string title)
        {
            // Title + extending separator on the same row
            var headerRow = new TableLayoutPanel
            {
                ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill,
                Margin = new Padding(0, 12, 0, 4), Padding = Padding.Empty
            };
            headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headerRow.Controls.Add(new Label
            {
                Text = title, AutoSize = true,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(60, 60, 60),
                Margin = new Padding(0, 0, 8, 0)
            });
            headerRow.Controls.Add(new Panel
            {
                Height = 1, Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(218, 218, 218),
                Margin = new Padding(0, 7, 0, 0)
            });
            sensorsContainer.Controls.Add(headerRow);

            var flow = new FlowLayoutPanel
            {
                AutoSize = true, Dock = DockStyle.Fill,
                WrapContents = true, FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(2, 0, 0, 0), Margin = Padding.Empty
            };
            sensorsContainer.Controls.Add(flow);
            return flow;
        }

        void AddBox(FlowLayoutPanel flow, string label,
            Func<SensorConfig, bool> getter, Action<SensorConfig, bool> setter)
        {
            var box = new CheckBox
            {
                Text = label, Checked = getter(sensors),
                AutoSize = true, Margin = new Padding(0, 2, 20, 2)
            };
            flow.Controls.Add(box);
            _sensorBoxes.Add((box, setter));
        }

        var cpuFlow = AddGroup("CPU");
        AddBox(cpuFlow, "Load",          c => c.CpuLoad,         (c, v) => c.CpuLoad = v);
        AddBox(cpuFlow, "Temperature",   c => c.CpuTemp,         (c, v) => c.CpuTemp = v);
        AddBox(cpuFlow, "Package Power", c => c.CpuPackagePower, (c, v) => c.CpuPackagePower = v);
        AddBox(cpuFlow, "Core Voltage",  c => c.CpuCoreVoltage,  (c, v) => c.CpuCoreVoltage = v);

        var gpuFlow = AddGroup("GPU");
        AddBox(gpuFlow, "Load",         c => c.GpuLoad,        (c, v) => c.GpuLoad = v);
        AddBox(gpuFlow, "Temperature",  c => c.GpuTemp,        (c, v) => c.GpuTemp = v);
        AddBox(gpuFlow, "Board Power",  c => c.GpuBoardPower,  (c, v) => c.GpuBoardPower = v);
        AddBox(gpuFlow, "Fan Speed",    c => c.GpuFanSpeed,    (c, v) => c.GpuFanSpeed = v);
        AddBox(gpuFlow, "Memory Load",  c => c.GpuMemoryLoad,  (c, v) => c.GpuMemoryLoad = v);
        AddBox(gpuFlow, "Memory Used",  c => c.GpuMemoryUsed,  (c, v) => c.GpuMemoryUsed = v);
        AddBox(gpuFlow, "Memory Total", c => c.GpuMemoryTotal, (c, v) => c.GpuMemoryTotal = v);

        var ramFlow = AddGroup("RAM");
        AddBox(ramFlow, "Load",  c => c.RamLoad,  (c, v) => c.RamLoad = v);
        AddBox(ramFlow, "Used",  c => c.RamUsed,  (c, v) => c.RamUsed = v);
        AddBox(ramFlow, "Total", c => c.RamTotal, (c, v) => c.RamTotal = v);

        var netFlow = AddGroup("Network");
        AddBox(netFlow, "Upload",   c => c.NetworkUpload,   (c, v) => c.NetworkUpload = v);
        AddBox(netFlow, "Download", c => c.NetworkDownload, (c, v) => c.NetworkDownload = v);

        var otherFlow = AddGroup("Other");
        AddBox(otherFlow, "Motherboard", c => c.MotherboardName, (c, v) => c.MotherboardName = v);
        AddBox(otherFlow, "Drives",      c => c.Drives,          (c, v) => c.Drives = v);
        AddBox(otherFlow, "Uptime",      c => c.Uptime,          (c, v) => c.Uptime = v);

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
        settingsMain.Controls.Add(sensorsContainer);

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

        // The Settings tab is built once and never changes structure, yet every time it
        // becomes visible WinForms runs ~30 expensive layout passes over the deeply nested
        // AutoSize TableLayoutPanels (~90 ms each → a ~3 s freeze on every switch).  Fix:
        // let it lay out ONCE, then keep its layout permanently suspended so subsequent
        // shows skip the cascade.  Width re-flow on window resize is handled in OnResize.
        _freezeSettingsLayout = () =>
        {
            if (_settingsFrozen) return;
            _settingsFrozen = true;
            settingsMain.SuspendLayout();
            settingsScroll.SuspendLayout();
        };
        _reflowSettingsLayout = () =>
        {
            if (!_settingsFrozen) return;
            // One layout pass at the new width, then re-freeze.
            settingsMain.ResumeLayout(true);
            settingsScroll.ResumeLayout(true);
            settingsMain.SuspendLayout();
            settingsScroll.SuspendLayout();
        };

        _tabs.Selected += (_, e) =>
        {
            UpdateSensorsTabActive();
            // Returning to the sensors tab: apply the most recent reading right away.
            if (e.TabPageIndex == 0 && _lastMetrics != null)
            {
                try { RefreshSensors(_lastMetrics); } catch { }
            }
            // First time the Settings tab is shown it lays out fully; freeze it afterward
            // so every later switch is instant.
            else if (e.TabPageIndex == 1 && !_settingsFrozen)
            {
                BeginInvoke(() => _freezeSettingsLayout());
            }
        };

        // Force handle creation so BeginInvoke works before the window is first shown.
        _ = Handle;

        // TabControl creates child handles lazily (only for the selected tab).
        // Pre-create the Settings tab controls up front.
        settingsPage.CreateControl();

        // Pre-warm the Settings layout while the window is still hidden, so even the FIRST
        // user-initiated open is instant.  We lay it out once now and freeze it; later shows
        // skip the expensive ~30-pass cascade entirely.
        BeginInvoke(() =>
        {
            if (_settingsFrozen || Visible) return;   // user already opened it — leave it alone
            _tabs.SelectedIndex = 1;
            settingsScroll.PerformLayout();
            _freezeSettingsLayout();
            _tabs.SelectedIndex = 0;                  // Sensors is the default tab
        });

        // Check autostart state off the UI thread — schtasks.exe can take 1-2 seconds.
        Task.Run(() => IsAutoStartEnabled())
            .ContinueWith(t =>
            {
                if (!t.IsFaulted && !IsDisposed)
                    BeginInvoke(() => _autoStart.Checked = t.Result);
            });
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

    MetricsSnapshot? _lastMetrics;

    public void SetLatestMetrics(MetricsSnapshot m)
    {
        _lastMetrics = m;
        if (!IsHandleCreated) return;
        // Only touch the sensor UI when it's actually visible. While the window is hidden
        // or the Settings tab is showing, updating it just burns the UI thread.  We read a
        // plain volatile flag here — never live control state — because we're off the UI thread.
        if (!_sensorsTabActive) return;
        BeginInvoke(() =>
        {
            try { RefreshSensors(m); }
            catch (Exception ex) { AppLog.Write($"[UI] RefreshSensors failed: {ex}"); }
        });
    }

    // Called by TrayApp's central pause switch. UI thread only (menu/button click).
    public void SetPauseState(bool paused) => _pauseBanner.Visible = paused;

    // Keep the background-thread-readable flag in sync.  Must be called on the UI thread.
    void UpdateSensorsTabActive() => _sensorsTabActive = Visible && _tabs.SelectedIndex == 0;

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateSensorsTabActive();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateSensorsTabActive();
        if (_tabs.SelectedIndex == 0 && _lastMetrics != null)
            RefreshSensors(_lastMetrics);
        // The pre-warm laid Settings out at the hidden design width; re-flow once now
        // that we know the real shown width so it's correct.
        _reflowSettingsLayout?.Invoke();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // The settings layout is normally frozen; re-flow it once at the new width.
        _reflowSettingsLayout?.Invoke();
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

    // A single owner-drawn dashboard control.  Replaces the old tree of dozens of nested
    // AutoSize TableLayoutPanels + Labels, whose layout engine made every per-second update
    // and tab switch cost hundreds of milliseconds.  Here a refresh is just: copy values,
    // call Invalidate() — one fast paint, no layout, no flicker.
    sealed class SensorsView : Control
    {
        // Geometry — mirrors the old TableLayoutPanel column widths / row heights.
        const int LabelW     = 120;
        const int ValueW     = 115;
        const int RowH       = 26;
        const int HeaderH    = 30;
        const int SepH       = 9;
        const int SectionGap = 20;
        const int BarVPad    = 9;

        static readonly Font  HeaderFont = new("Segoe UI", 9.5f, FontStyle.Bold);
        static readonly Color HeaderColor = Color.FromArgb(26, 26, 26);
        static readonly Color LabelColor  = Color.FromArgb(96, 96, 96);

        // Brushes are constant — cache them rather than allocating per row per paint.
        static readonly SolidBrush SepBrush   = new(Color.FromArgb(220, 220, 220));
        static readonly SolidBrush BarBgBrush = new(Color.FromArgb(224, 224, 224));
        static readonly SolidBrush BarFgBrush = new(Color.FromArgb(0, 120, 212));

        sealed class Row
        {
            public readonly string Label;
            public readonly bool   HasBar;
            public readonly Func<MetricsSnapshot, (int? Pct, string? Value, Color Color)> Get;
            public int?   Pct;
            public string Value = "—";
            public Color  Color = SystemColors.ControlText;
            public Row(RowDef d) { Label = d.Label; HasBar = d.HasBar; Get = d.GetData; }
        }
        sealed class Section
        {
            public readonly string  Title;
            public readonly string? Subtitle;
            public readonly List<Row> Rows;
            public Section(string title, string? subtitle, List<Row> rows)
                { Title = title; Subtitle = subtitle; Rows = rows; }
        }

        List<Section> _sections = new();
        string? _fingerprint;

        public SensorsView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Window;
        }

        public void SetMetrics(MetricsSnapshot m)
        {
            var fp = MakeFingerprint(m);
            if (_fingerprint != fp)
                Rebuild(m, fp);
            else
                foreach (var s in _sections)
                    foreach (var r in s.Rows)
                    {
                        var (p, v, c) = r.Get(m);
                        r.Pct = p; r.Value = v ?? "—"; r.Color = c;
                    }
            Invalidate();
        }

        void Rebuild(MetricsSnapshot m, string fp)
        {
            _sections = new List<Section>();
            void Add(string title, string? sub, IReadOnlyList<RowDef> defs)
                => _sections.Add(new Section(title, sub, defs.Select(d => new Row(d)).ToList()));

            if (m.Cpu     != null)    Add("CPU",     m.Cpu.Name, CpuRowDefs());
            if (m.Gpu     != null)    Add("GPU",     m.Gpu.Name, GpuRowDefs());
            if (m.Ram     != null)    Add("RAM",     null,        RamRowDefs());
            if (m.Drives?.Count > 0)  Add("Drives",  null,        DriveRowDefs(m.Drives));
            if (m.Network != null)    Add("Network", null,        NetworkRowDefs());
            if (m.System  != null)    Add("System",  null,        SystemRowDefs());

            _fingerprint = fp;
            foreach (var s in _sections)
                foreach (var r in s.Rows)
                {
                    var (p, v, c) = r.Get(m);
                    r.Pct = p; r.Value = v ?? "—"; r.Color = c;
                }
            Height = ContentHeight();   // so the parent AutoScroll panel can scroll us
        }

        int ContentHeight()
        {
            int h = 0;
            foreach (var s in _sections)
                h += HeaderH + SepH + s.Rows.Count * RowH + SectionGap;
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int w = Width, y = 0;

            const TextFormatFlags leftMid  = TextFormatFlags.Left  | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            const TextFormatFlags rightMid = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;
            const TextFormatFlags botLeft  = TextFormatFlags.Left  | TextFormatFlags.Bottom         | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis;

            foreach (var s in _sections)
            {
                var headerText = s.Subtitle != null ? $"{s.Title}  ·  {s.Subtitle}" : s.Title;
                TextRenderer.DrawText(g, headerText, HeaderFont,
                    new Rectangle(0, y, w, HeaderH - 3), HeaderColor, botLeft);
                y += HeaderH;

                g.FillRectangle(SepBrush, 0, y, w, 2);
                y += SepH;

                foreach (var r in s.Rows)
                {
                    TextRenderer.DrawText(g, r.Label, Font,
                        new Rectangle(0, y, LabelW - 8, RowH), LabelColor, leftMid);

                    if (r.HasBar)
                    {
                        int barX = LabelW;
                        int barW = Math.Max(0, w - LabelW - ValueW);
                        int barY = y + BarVPad;
                        int barH = RowH - BarVPad * 2;
                        g.FillRectangle(BarBgBrush, barX, barY, barW, barH);
                        int pct = Math.Clamp(r.Pct ?? 0, 0, 100);
                        if (pct > 0)
                            g.FillRectangle(BarFgBrush, barX, barY, barW * pct / 100, barH);
                    }

                    TextRenderer.DrawText(g, r.Value, Font,
                        new Rectangle(w - ValueW, y, ValueW, RowH), r.Color, rightMid);
                    y += RowH;
                }
                y += SectionGap;
            }
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
        ReplaceSensorsContent(lbl);
    }

    // Safely swap the single child of _sensorsOuter.
    // Control.Dispose() removes the control from its parent's Controls collection —
    // iterating while disposing would throw InvalidOperationException (swallowed by BeginInvoke).
    void ReplaceSensorsContent(Control newContent)
    {
        _sensorsOuter.SuspendLayout();
        var old = _sensorsOuter.Controls.Cast<Control>().ToArray();
        _sensorsOuter.Controls.Clear();          // remove all before disposing
        _sensorsOuter.Controls.Add(newContent);
        _sensorsOuter.ResumeLayout();
        foreach (var c in old) c.Dispose();      // safe: already removed from collection
    }

    // Which top-level sections are present — used to detect structural changes.
    // Drive NAMES (not just the count) must be part of the fingerprint: swapping one
    // USB drive for another keeps the count identical but needs a row rebuild.
    static string MakeFingerprint(MetricsSnapshot m) =>
        $"{m.Cpu != null}|{m.Gpu != null}|{m.Ram != null}|{string.Join(",", m.Drives?.Select(d => d.Name) ?? [])}|{m.Network != null}|{m.System != null}";

    // Called every refresh interval.  Owner-drawn — just hand the new reading to the
    // dashboard, which copies values and invalidates (one fast paint, no layout).
    void RefreshSensors(MetricsSnapshot m)
    {
        if (_sensorsView == null)
        {
            _sensorsView = new SensorsView { Dock = DockStyle.Top };
            _sensorsView.Width = _sensorsOuter.ClientSize.Width - _sensorsOuter.Padding.Horizontal;
            ReplaceSensorsContent(_sensorsView);
        }
        _sensorsView.SetMetrics(m);
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
    record RowDef(string Label, bool HasBar, Func<MetricsSnapshot, (int? Pct, string? Value, Color Color)> GetData);

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

    static IReadOnlyList<RowDef> SystemRowDefs() =>
    [
        new("Uptime", false, m => UptimeRow(m.System?.UptimeSec)),
    ];

    static (int?, string?, Color) UptimeRow(int? sec)
    {
        if (sec == null) return (null, null, SystemColors.ControlText);
        var t = TimeSpan.FromSeconds(sec.Value);
        var text = t.Days  > 0 ? $"{t.Days}d {t.Hours}h {t.Minutes}m"
                 : t.Hours > 0 ? $"{t.Hours}h {t.Minutes}m"
                 :               $"{t.Minutes}m {t.Seconds}s";
        return (null, text, SystemColors.ControlText);
    }

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
        _config.General.PublishIntervalSeconds = (double)_interval.Value;
        _config.General.DebugEnabled           = _debugEnabled.Checked;
        _config.Mqtt.HaDiscoveryEnabled        = _haDiscovery.Checked;
        _config.General.UpdateCheckEnabled     = _updateCheck.Checked;
        foreach (var (box, setter) in _sensorBoxes)
            setter(_config.Sensors, box.Checked);

        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));

        // schtasks.exe takes 1-2 s — run it off the UI thread so Save doesn't freeze.
        var enableAutoStart = _autoStart.Checked;
        Task.Run(() => ApplyAutoStart(enableAutoStart));

        MessageBox.Show("Settings applied.",
            "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Test & Apply (MQTT Broker settings) ──────────────────────────────────────
    // Tests the connection with the entered credentials; on success saves those
    // fields and restarts the app. Settings are NOT saved on failure.

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
            var optionsBuilder = factory.CreateClientOptionsBuilder()
                .WithTcpServer(_host.Text.Trim(), (int)_port.Value)
                .WithCredentials(_username.Text, _password.Text);
            if (_useTls.Checked)
                optionsBuilder = optionsBuilder.WithTlsOptions(o => o.UseTls());
            var options = optionsBuilder.Build();

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
        _config.Mqtt.Enabled   = true;
        _config.Mqtt.Host      = _host.Text.Trim();
        _config.Mqtt.Port      = (int)_port.Value;
        _config.Mqtt.UseTls    = _useTls.Checked;
        _config.Mqtt.Username  = _username.Text;
        _config.Mqtt.Password  = _password.Text;
        _config.Mqtt.TopicRoot = _topic.Text.Trim();

        File.WriteAllText(_configPath,
            JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));

        Application.Restart();
    }
}
