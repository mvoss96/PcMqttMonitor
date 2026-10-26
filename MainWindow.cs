using System.Drawing;
using System.Windows.Forms;

// The main window: a 48px icon sidebar (Dashboard / Outputs / Sensors /
// Settings / About), one page visible at a time, and a save footer below the
// page only while there are unsaved changes. Saving applies live — sinks are
// rebuilt via Program.RequestSinksReload, never an app restart.
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

    readonly InfoBar _pauseBanner;
    public Action<bool>? PauseChangeRequested;   // set by TrayApp (central pause switch)

    // save footer
    readonly SaveFooter _saveBar;
    readonly System.Windows.Forms.Timer _savedFlash = new() { Interval = 1200 };
    bool _dirty;

    // Update notification: title-bar text suffix + accent badge on the About
    // nav icon — no extra chrome inside the window (see SetUpdateAvailable).

    // True while the window is shown AND the respective page is active. Read
    // from the publish thread, so they must be plain volatile flags, not
    // control state.
    volatile bool _dashboardActive;
    volatile bool _sensorsActive;
    MetricsSnapshot? _lastMetrics;

    public MainWindow(string configPath, AppConfig config)
    {
        _configPath = configPath;
        _config = config;

        var version = UpdateChecker.CurrentVersion;
        Text = $"PC MQTT Monitor  v{version.ToString(3)}";
        Icon = TrayApp.CreateIcon();
        // 500x700 per the approved mockup — hardware names next to the card
        // titles rely on the staged shortening in DashboardView.NameStages.
        Size = new Size(Theme.S(500), Theme.S(700));
        FormBorderStyle = FormBorderStyle.FixedSingle;   // fixed window per the mockup
        AutoScaleMode = AutoScaleMode.None;   // all DPI scaling is explicit via Theme.S
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
        _pauseBanner = new InfoBar(L.T.PausedBanner, L.T.Resume) { Visible = false };
        _pauseBanner.Action.Click += (_, _) => PauseChangeRequested?.Invoke(false);
        var dashboardHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.WinBg };
        dashboardHost.Controls.Add(_dashboard);
        dashboardHost.Controls.Add(_pauseBanner);   // added after Fill so Top wins

        _outputs  = new OutputsPage(config, UpdateSaveBar) { Dock = DockStyle.Fill };
        _sensors  = new SensorsPage(config, UpdateSaveBar) { Dock = DockStyle.Fill };
        _settings = new SettingsPage(config, UpdateSaveBar) { Dock = DockStyle.Fill };
        _about    = new AboutPage(v => SetUpdateAvailable(v)) { Dock = DockStyle.Fill };

        _pages = [dashboardHost, _outputs, _sensors, _settings, _about];
        foreach (var page in _pages)
        {
            page.Visible = false;
            _content.Controls.Add(page);
        }

        // ── sidebar ─────────────────────────────────────────────────────────
        var sidebar = new Panel { Dock = DockStyle.Left, Width = Theme.S(48), BackColor = Theme.WinBg };
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.CardBorder);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        var tips = new ToolTip { InitialDelay = 400, ReshowDelay = 100 };
        (string Tip, int Icon)[] navDefs =
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
                Glyph = Theme.Glyph(navDefs[i].Icon),
                Location = new Point(0, Theme.S(8) + i * Theme.S(39)),
            };
            btn.Click += (_, _) => SelectPage(index);
            tips.SetToolTip(btn, navDefs[i].Tip);
            sidebar.Controls.Add(btn);
            _navButtons[i] = btn;
        }

        Controls.Add(_content);
        Controls.Add(sidebar);   // added after Fill so Left wins

        // ── save footer (below the page, only while dirty) ─────────────────
        _saveBar = new SaveFooter();
        _saveBar.Button.Click += (_, _) => Save();
        _saveBar.Revert.Click += (_, _) => Revert();
        _content.Controls.Add(_saveBar);   // added after the Fill pages so Bottom wins
        _savedFlash.Tick += (_, _) => { _savedFlash.Stop(); if (!_dirty) _saveBar.Visible = false; };

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

    // ── hooks for DemoMode (CI screenshots) ───────────────────────────────────

    internal void DemoSelectPage(int index) => SelectPage(index);

    internal void DemoFeed(MetricsSnapshot m)
    {
        _lastMetrics = m;
        _dashboard.SetMetrics(m);
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

    public void ShowDashboard() { SelectPage(0); ShowAnchored(); }
    public void ShowSettings()  { SelectPage(3); ShowAnchored(); }
    void ShowAbout()            { SelectPage(4); ShowAnchored(); }

    // Tray-flyout placement: bottom-right of the working area (above the
    // taskbar) on whichever screen the cursor is on.
    void ShowAnchored()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - Theme.S(8), area.Bottom - Height - Theme.S(8));
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

    bool HasInvalidInput => _outputs.HasInvalidInput || _settings.HasInvalidInput;

    // Called on every edit. The bar shows while some page differs from the
    // saved config or holds invalid input — an invalid value would otherwise
    // compare equal to the saved one and the bar would silently disappear.
    void UpdateSaveBar()
    {
        bool invalid = HasInvalidInput;
        _dirty = invalid || _outputs.HasChanges || _sensors.HasChanges || _settings.HasChanges;
        if (!_dirty)
        {
            if (!_savedFlash.Enabled) _saveBar.Visible = false;
            return;
        }
        _savedFlash.Stop();
        // Invalid input disables Save; the red field shows which one.
        _saveBar.Msg.Text = invalid ? L.T.SaveInvalidInput : L.T.UnsavedChanges;
        _saveBar.Msg.ForeColor = invalid ? Theme.Crit : Theme.Fg2;
        _saveBar.Button.Enabled = !invalid;
        _saveBar.ButtonsShown = true;
        _saveBar.Visible = true;
    }

    // Discard all unsaved edits on every page.
    void Revert()
    {
        _outputs.Revert();
        _sensors.Revert();
        _settings.Revert();
        UpdateSaveBar();
    }

    void Save()
    {
        if (HasInvalidInput)
            return;

        // Output settings need a sink rebuild; general/sensor settings apply
        // live through the shared config object.
        bool outputsChanged = _outputs.HasChanges;
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

        if (outputsChanged)
            Program.RequestSinksReload();

        _dirty = false;
        _saveBar.Msg.Text = L.T.Saved;
        _saveBar.Msg.ForeColor = Theme.Good;
        _saveBar.ButtonsShown = false;
        _savedFlash.Start();
    }

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
        if (!IsHandleCreated) return;
        if (_dashboardActive)
            BeginInvoke(() =>
            {
                try { _dashboard.SetMetrics(m); }
                catch (Exception ex) { AppLog.Write($"[UI] dashboard refresh failed: {ex}"); }
            });
        else if (_sensorsActive)
            BeginInvoke(() =>
            {
                try { _sensors.RefreshFanRpm(); }
                catch (Exception ex) { AppLog.Write($"[UI] fan rpm refresh failed: {ex}"); }
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

    // Called from the sinks (any thread) — feeds the status line of their card.
    // Sinks also report when disposed, which can happen while the app shuts down.
    public void SetSinkStatus(string sink, StatusKind? kind, string text)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => _outputs.SetSinkStatus(sink, kind, text)); }
        catch (InvalidOperationException) { }   // window handle already destroyed
    }

    // Called by TrayApp's central pause switch. UI thread only.
    public void SetPauseState(bool paused) => _pauseBanner.Visible = paused;

    void UpdateDashboardActive()
    {
        _dashboardActive = Visible && _pages[0].Visible;
        _sensorsActive   = Visible && _pages[2].Visible;
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
