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
    readonly SaveBar _saveBar;
    readonly System.Windows.Forms.Timer _savedFlash = new() { Interval = 1200 };
    bool _dirty, _outputsDirty;

    // Update notification: title-bar text suffix + accent badge on the About
    // nav icon — no extra chrome inside the window (see SetUpdateAvailable).

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
        FormBorderStyle = FormBorderStyle.FixedSingle;   // fixed window per the mockup
        MaximizeBox = false;
        MinimizeBox = false;   // close (= hide to tray) is the only sensible action
        StartPosition = FormStartPosition.Manual;        // anchored near the tray on Show
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
            (L.T.NavDashboard, NavIcons.Dashboard),
            (L.T.NavOutputs,   NavIcons.Outputs),
            (L.T.NavSensors,   NavIcons.Sensors),
            (L.T.NavSettings,  NavIcons.Settings),
            (L.T.NavAbout,     NavIcons.About),
        ];
        _navButtons = new NavButton[navDefs.Length];
        for (int i = 0; i < navDefs.Length; i++)
        {
            int index = i;
            var btn = new NavButton
            {
                IconPainter = navDefs[i].Icon,
                Location = new Point(0, 8 + i * 39),
            };
            btn.Click += (_, _) => SelectPage(index);
            tips.SetToolTip(btn, navDefs[i].Tip);
            sidebar.Controls.Add(btn);
            _navButtons[i] = btn;
        }

        Controls.Add(_content);
        Controls.Add(sidebar);   // added after Fill so Left wins

        // ── floating save panel (bottom-right, only while dirty) ────────────
        _saveBar = new SaveBar();
        _saveBar.Button.Click += (_, _) => Save();
        _saveBar.SizeChanged += (_, _) => PositionSaveBar();
        Controls.Add(_saveBar);
        _saveBar.BringToFront();
        _savedFlash.Tick += (_, _) => { _savedFlash.Stop(); if (!_dirty) _saveBar.Visible = false; };

        Resize += (_, _) => PositionSaveBar();

        SelectPage(0);

        // Force handle creation so BeginInvoke works before the window is first shown.
        _ = Handle;

        // Coming back from a settings-triggered restart (theme change): reopen
        // the window on the Settings page instead of starting hidden in the tray.
        if (File.Exists(RestartMarker))
        {
            try { File.Delete(RestartMarker); } catch { }
            BeginInvoke(ShowSettings);   // runs once the message loop is up
        }
    }

    // One-shot marker surviving the Application.Restart round trip.
    static string RestartMarker => Path.Combine(Path.GetTempPath(), "PcMqttMonitor.reopen");

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

    public void ShowDashboard() { SelectPage(0); ShowAnchored(); }
    public void ShowSettings()  { SelectPage(3); ShowAnchored(); }
    void ShowAbout()            { SelectPage(4); ShowAnchored(); }

    // Tray-flyout placement: bottom-right of the working area (above the
    // taskbar) on whichever screen the cursor is on.
    void ShowAnchored()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 8, area.Bottom - Height - 8);
        Show();
        // A first Show() from a tray click doesn't reliably raise the window —
        // BringToFront alone can leave it behind the current foreground window.
        // The TopMost pulse forces it to the top of the z-order; Activate gives
        // it focus (permitted here — the user just clicked our tray icon).
        TopMost = true;
        TopMost = false;
        Activate();
    }

    // ── dirty / save ──────────────────────────────────────────────────────────

    void MarkDirty(bool outputs = false)
    {
        _dirty = true;
        _outputsDirty |= outputs;
        _savedFlash.Stop();
        _saveBar.Msg.Text = L.T.UnsavedChanges;
        _saveBar.Msg.ForeColor = Theme.Fg2;
        _saveBar.Button.Visible = true;
        _saveBar.PerformLayout();
        PositionSaveBar();
        _saveBar.Visible = true;
    }

    void Save()
    {
        _outputs.Apply();
        _sensors.Apply();
        bool needsRestart = _settings.Apply();
        ConfigLoader.Save(_configPath, _config);

        // A theme change restarts the whole app — the config is already saved,
        // the restarted instance picks the new color mode up at startup.
        if (needsRestart)
        {
            try { File.WriteAllText(RestartMarker, ""); } catch { }
            Application.Restart();
            return;
        }

        // Output settings need a sink rebuild; general/sensor settings apply
        // live through the shared config object.
        if (_outputsDirty)
            Program.RequestSinksReload();

        _dirty = false;
        _outputsDirty = false;
        _saveBar.Msg.Text = L.T.Saved;
        _saveBar.Msg.ForeColor = Theme.Good;
        _saveBar.Button.Visible = false;
        _saveBar.PerformLayout();
        PositionSaveBar();
        _savedFlash.Start();
    }

    void PositionSaveBar() => _saveBar.Location = new Point(
        ClientSize.Width - _saveBar.Width - 12,
        ClientSize.Height - _saveBar.Height - 12);

    // ── update pill ───────────────────────────────────────────────────────────

    public void SetUpdateAvailable(Version version)
    {
        // Right where the user asked for it: next to the version in the title
        // bar — plus an accent dot on the About icon as the clickable cue
        // (About carries the details and the download link).
        Text = $"PC MQTT Monitor  v{UpdateChecker.CurrentVersion.ToString(3)}  —  {string.Format(L.T.TitleUpdate, version.ToString(3))}";
        _navButtons[^1].Badge = true;
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

    // Called from TrayApp (any thread) — feeds the dashboard's startup
    // placeholder ("Waiting for system drivers…", "Opening sensors…") until
    // the first snapshot arrives; after that it is irrelevant and skipped.
    public void SetLoadingStatus(string status)
    {
        if (_lastMetrics != null || !IsHandleCreated) return;
        BeginInvoke(() => _dashboard.SetPlaceholder(status));
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
            Text = L.T.PausedBanner,
            AutoSize = true,
            ForeColor = Theme.Dark ? Color.FromArgb(240, 216, 137) : Color.FromArgb(102, 77, 3),
            Location = new Point(14, 10),
        };
        var resume = new Button
        {
            Text = L.T.Resume, AutoSize = true,
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
