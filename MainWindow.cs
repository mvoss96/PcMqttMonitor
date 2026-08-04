using System.Drawing;
using System.Windows.Forms;

// The main window: a 48px icon sidebar (Dashboard / Outputs / Sensors /
// Settings / About), one page visible at a time, a floating save panel that
// appears bottom-right only while there are unsaved changes, and an "update
// available" pill that jumps to the About page. Saving applies live — sinks
// are rebuilt via Program.RequestSinksReload, never an app restart.
sealed class MainWindow : Form
{
    readonly string _configPath;
    readonly AppConfig _config;

    readonly Panel _content;
    readonly NavButton[] _navButtons;
    readonly Control[] _pages;

    readonly DashboardView _dashboard;
    readonly OutputsPage _outputs;
    readonly SensorsPage _sensors;
    readonly SettingsPage _settings;
    readonly AboutPage _about;

    readonly Panel _pauseBanner;
    public Action<bool>? PauseChangeRequested;   // set by TrayApp (central pause switch)

    // floating save panel
    readonly CardPanel _saveBar;
    readonly Label _saveMsg;
    readonly PillButton _saveBtn;
    readonly System.Windows.Forms.Timer _savedFlash = new() { Interval = 1200 };
    bool _dirty, _outputsDirty;

    // "update available" pill, top-right over the content
    readonly PillButton _updatePill;

    // True while the window is shown AND the dashboard page is active. Read from
    // the publish thread, so it must be a plain volatile flag, not control state.
    volatile bool _dashboardActive;
    MetricsSnapshot? _lastMetrics;

    public MainWindow(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _config = config;

        var version = UpdateChecker.CurrentVersion;
        Text = $"PC MQTT Monitor  v{version.ToString(3)}";
        Icon = TrayApp.CreateIcon();
        Size = new Size(500, 700);
        MinimumSize = new Size(460, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = Theme.WinBg;
        Font = Theme.Base;
        DoubleBuffered = true;

        // ── content pages ───────────────────────────────────────────────────
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WinBg };

        _dashboard = new DashboardView { Dock = DockStyle.Fill };
        _pauseBanner = MakePauseBanner();
        var dashboardHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WinBg };
        dashboardHost.Controls.Add(_dashboard);
        dashboardHost.Controls.Add(_pauseBanner);   // added after Fill so Top wins

        _outputs  = new OutputsPage(config, () => MarkDirty(outputs: true)) { Dock = DockStyle.Fill };
        _sensors  = new SensorsPage(config, () => MarkDirty()) { Dock = DockStyle.Fill };
        _settings = new SettingsPage(config, () => MarkDirty()) { Dock = DockStyle.Fill };
        _about    = new AboutPage(v => SetUpdateAvailable(v)) { Dock = DockStyle.Fill };

        _pages = [dashboardHost, _outputs, _sensors, _settings, _about];
        foreach (var page in _pages)
        {
            page.Visible = false;
            _content.Controls.Add(page);
        }

        // ── sidebar ─────────────────────────────────────────────────────────
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 48, BackColor = Theme.WinBg };
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.CardBorder);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        var tips = new ToolTip { InitialDelay = 400, ReshowDelay = 100 };
        (string Tip, Action<Graphics, RectangleF, Color> Icon)[] navDefs =
        [
            ("Dashboard", NavIcons.Dashboard),
            ("Outputs",   NavIcons.Outputs),
            ("Sensors",   NavIcons.Sensors),
            ("Settings",  NavIcons.Settings),
            ("About",     NavIcons.About),
        ];
        _navButtons = new NavButton[navDefs.Length];
        for (int i = 0; i < navDefs.Length; i++)
        {
            int index = i;
            var btn = new NavButton
            {
                IconPainter = navDefs[i].Icon,
                Location = new Point(5, 8 + i * 39),
            };
            btn.Click += (_, _) => SelectPage(index);
            tips.SetToolTip(btn, navDefs[i].Tip);
            sidebar.Controls.Add(btn);
            _navButtons[i] = btn;
        }

        Controls.Add(_content);
        Controls.Add(sidebar);   // added after Fill so Left wins

        // ── floating save panel (bottom-right, only while dirty) ────────────
        _saveMsg = new Label
        {
            Text = "Unsaved changes", ForeColor = Theme.Fg2, Font = Theme.Small,
            AutoSize = true, BackColor = Theme.CardBg,
        };
        _saveBtn = new PillButton { Text = "Save", Size = new Size(64, 26) };
        _saveBtn.Click += (_, _) => Save();
        _saveBar = new CardPanel { Size = new Size(0, 42), Visible = false };
        _saveBar.Controls.Add(_saveMsg);
        _saveBar.Controls.Add(_saveBtn);
        LayoutSaveBar();
        Controls.Add(_saveBar);
        _saveBar.BringToFront();
        _savedFlash.Tick += (_, _) => { _savedFlash.Stop(); if (!_dirty) _saveBar.Visible = false; };

        // ── update pill ─────────────────────────────────────────────────────
        _updatePill = new PillButton
        {
            Soft = true, Visible = false, Height = 22, Font = Theme.Tiny,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _updatePill.Click += (_, _) => ShowAbout();
        Controls.Add(_updatePill);
        _updatePill.BringToFront();

        Resize += (_, _) => LayoutSaveBar();

        SelectPage(0);

        // Force handle creation so BeginInvoke works before the window is first shown.
        _ = Handle;
    }

    // ── page switching ────────────────────────────────────────────────────────

    void SelectPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            _navButtons[i].Active = i == index;
            _pages[i].Visible = i == index;
        }
        UpdateDashboardActive();
        if (index == 0 && _lastMetrics != null)
            _dashboard.SetMetrics(_lastMetrics);
    }

    public void ShowDashboard() { SelectPage(0); Show(); BringToFront(); }
    public void ShowSettings()  { SelectPage(3); Show(); BringToFront(); }
    void ShowAbout()            { SelectPage(4); Show(); BringToFront(); }

    // ── dirty / save ──────────────────────────────────────────────────────────

    void MarkDirty(bool outputs = false)
    {
        _dirty = true;
        _outputsDirty |= outputs;
        _savedFlash.Stop();
        _saveMsg.Text = "Unsaved changes";
        _saveMsg.ForeColor = Theme.Fg2;
        _saveBtn.Visible = true;
        LayoutSaveBar();
        _saveBar.Visible = true;
    }

    void Save()
    {
        _outputs.Apply();
        _sensors.Apply();
        _settings.Apply();
        ConfigLoader.Save(_configPath, _config);

        // Output settings need a sink rebuild; general/sensor settings apply
        // live through the shared config object.
        if (_outputsDirty)
            Program.RequestSinksReload();

        _dirty = false;
        _outputsDirty = false;
        _saveMsg.Text = "✓ Saved";
        _saveMsg.ForeColor = Theme.Good;
        _saveBtn.Visible = false;
        LayoutSaveBar();
        _savedFlash.Start();
    }

    void LayoutSaveBar()
    {
        int msgW = TextRenderer.MeasureText(_saveMsg.Text, _saveMsg.Font).Width;
        int w = 14 + msgW + (_saveBtn.Visible ? 10 + _saveBtn.Width : 2) + 8;
        _saveBar.Size = new Size(w, 42);
        _saveMsg.Location = new Point(14, (42 - _saveMsg.Height) / 2);
        _saveBtn.Location = new Point(w - _saveBtn.Width - 8, (42 - _saveBtn.Height) / 2);
        _saveBar.Location = new Point(ClientSize.Width - w - 12, ClientSize.Height - 42 - 12);
    }

    // ── update pill ───────────────────────────────────────────────────────────

    public void SetUpdateAvailable(Version version)
    {
        _updatePill.Text = $"v{version.ToString(3)} available";
        int w = TextRenderer.MeasureText(_updatePill.Text, _updatePill.Font).Width + 20;
        _updatePill.SetBounds(ClientSize.Width - w - 12, 8, w, 22);
        _updatePill.Visible = true;
    }

    // ── data from the publish loop / tray ─────────────────────────────────────

    public void SetLatestMetrics(MetricsSnapshot m)
    {
        _lastMetrics = m;
        if (!IsHandleCreated || !_dashboardActive) return;
        BeginInvoke(() =>
        {
            try { _dashboard.SetMetrics(m); }
            catch (Exception ex) { AppLog.Write($"[UI] dashboard refresh failed: {ex}"); }
        });
    }

    // Called from the MQTT sink (any thread) — feeds the Outputs page status line.
    public void SetConnectionStatus(bool connected, string broker)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() => _outputs.SetMqttStatus(connected, broker));
    }

    // Called by TrayApp's central pause switch. UI thread only.
    public void SetPauseState(bool paused) => _pauseBanner.Visible = paused;

    void UpdateDashboardActive() => _dashboardActive = Visible && _pages[0].Visible;

    Panel MakePauseBanner()
    {
        var banner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Theme.Dark ? Color.FromArgb(74, 59, 18) : Color.FromArgb(255, 244, 199),
            Visible = false,
        };
        var label = new Label
        {
            Text = "Publishing is paused",
            AutoSize = true,
            ForeColor = Theme.Dark ? Color.FromArgb(240, 216, 137) : Color.FromArgb(102, 77, 3),
            Location = new Point(14, 10),
        };
        var resume = new Button
        {
            Text = "Resume", AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            ForeColor = label.ForeColor,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        resume.FlatAppearance.BorderColor = label.ForeColor;
        resume.Click += (_, _) => PauseChangeRequested?.Invoke(false);
        banner.Controls.Add(label);
        banner.Controls.Add(resume);
        banner.Resize += (_, _) => resume.Location = new Point(
            banner.ClientSize.Width - resume.Width - 12,
            (banner.ClientSize.Height - resume.Height) / 2);
        return banner;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateDashboardActive();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateDashboardActive();
        if (_pages[0].Visible && _lastMetrics != null)
            _dashboard.SetMetrics(_lastMetrics);
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
}
