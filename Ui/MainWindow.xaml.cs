using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

// The main window: Fluent theme (Windows 11 controls, Mica backdrop), icon
// sidebar (Dashboard / Outputs / Sensors / Settings / About), one page at a
// time and a save footer while there are unsaved changes. Saving applies
// live: sinks are rebuilt via Program.RequestSinksReload, the theme switches
// in place; only a language change restarts the app.
public partial class MainWindow : Window
{
    readonly string _configPath;
    readonly AppConfig _config;
    readonly DashboardPage _dashboard = new();
    readonly OutputsPage _outputs;
    readonly SensorsPage _sensors;
    readonly SettingsPage _settings;
    readonly UIElement[] _pages;
    readonly Border _pauseBar;
    readonly Ellipse _aboutBadge = new() { Width = 6, Height = 6, Visibility = Visibility.Collapsed };
    bool _exiting;

    // Set by TrayApp (central pause switch).
    internal Action<bool>? PauseChangeRequested;

    // Page shown and window visible; read from the publish thread.
    volatile int _activePage = -1;
    MetricsSnapshot? _lastMetrics;

    internal MainWindow(string configPath, AppConfig config)
    {
        InitializeComponent();
        _configPath = configPath;
        _config = config;
        Title = $"PC MQTT Monitor  v{UpdateChecker.CurrentVersion.ToString(3)}";
        SaveButton.Content = L.T.Save;
        DiscardButton.Content = L.T.Discard;
        using (var icon = TrayApp.CreateIcon())
            Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        _outputs = new OutputsPage(config, UpdateSaveBar);
        _sensors = new SensorsPage(config, UpdateSaveBar);
        _settings = new SettingsPage(config, UpdateSaveBar);
        var about = new AboutPage(SetUpdateAvailable);

        _pauseBar = PauseBar();
        var dashboardHost = new DockPanel();
        DockPanel.SetDock(_pauseBar, Dock.Top);
        dashboardHost.Children.Add(_pauseBar);
        dashboardHost.Children.Add(_dashboard);
        _pages = [dashboardHost, _outputs, _sensors, _settings, about];

        (string Tip, int Glyph)[] nav =
        [
            (L.T.NavDashboard, Glyphs.Dashboard),
            (L.T.NavOutputs,   Glyphs.Outputs),
            (L.T.NavSensors,   Glyphs.Sensors),
            (L.T.NavSettings,  Glyphs.Settings),
            (L.T.NavAbout,     Glyphs.About),
        ];
        foreach (var n in nav)
        {
            var content = new Grid();
            content.Children.Add(Glyphs.Icon(n.Glyph, 16));
            Nav.Items.Add(new ListBoxItem { ToolTip = n.Tip, Content = content });
        }
        // Accent dot on the About icon while an update is available.
        _aboutBadge.SetResourceReference(Shape.FillProperty, "AccentFillColorDefaultBrush");
        _aboutBadge.HorizontalAlignment = HorizontalAlignment.Right;
        _aboutBadge.VerticalAlignment = VerticalAlignment.Top;
        _aboutBadge.Margin = new Thickness(0, -2, -4, 0);
        ((Grid)((ListBoxItem)Nav.Items[4]).Content).Children.Add(_aboutBadge);

        Nav.SelectedIndex = 0;
        IsVisibleChanged += (_, _) => UpdateActivePage();

        // Coming back from a language-change restart: reopen on Settings.
        if (File.Exists(RestartMarker))
        {
            try { File.Delete(RestartMarker); } catch { }
            Dispatcher.BeginInvoke(ShowSettings);
        }
    }

    // One-shot marker surviving the restart after a language change.
    static string RestartMarker => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PcMqttMonitor.reopen");

    // Fluent theme for the whole app; switches live.
    internal static void ApplyTheme(string? theme)
    {
#pragma warning disable WPF0001 // ThemeMode is marked experimental
        Application.Current.ThemeMode = theme?.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark"  => ThemeMode.Dark,
            _       => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }

    // Windows 11 info bar for the pause state, with a Resume button.
    Border PauseBar()
    {
        var resume = new Button { Content = L.T.Resume };
        resume.Click += (_, _) => PauseChangeRequested?.Invoke(false);
        var icon = Glyphs.Icon(Glyphs.Pause, 16);
        icon.Margin = new Thickness(0, 0, 10, 0);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        var text = new TextBlock { Text = L.T.PausedBanner, VerticalAlignment = VerticalAlignment.Center };
        var panel = new DockPanel();
        DockPanel.SetDock(resume, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        panel.Children.Add(resume);
        panel.Children.Add(icon);
        panel.Children.Add(text);
        var bar = new Border
        {
            Child = panel, Padding = new Thickness(14, 8, 8, 8), Margin = new Thickness(16, 16, 16, 0),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
        };
        bar.SetResourceReference(BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
        bar.SetResourceReference(BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return bar;
    }

    // ── pages ─────────────────────────────────────────────────────────────────

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex < 0) return;
        PageHost.Content = _pages[Nav.SelectedIndex];
        UpdateActivePage();
    }

    void UpdateActivePage()
    {
        _activePage = IsVisible ? Nav.SelectedIndex : -1;
        if (_activePage == 0 && _lastMetrics != null) _dashboard.SetMetrics(_lastMetrics);
    }

    internal void SelectPage(int index) => Nav.SelectedIndex = index;
    internal void ShowDashboard() { SelectPage(0); ShowAnchored(); }
    internal void ShowSettings()  { SelectPage(3); ShowAnchored(); }

    // Tray-flyout placement: bottom-right of the working area (above the
    // taskbar) on the screen under the cursor. Positioned in physical pixels
    // with SetWindowPos, twice: moving to a monitor with another DPI makes
    // WPF rescale the window, which changes its pixel size.
    void ShowAnchored()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        for (int pass = 0; pass < 2; pass++)
        {
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top, gap = (int)(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            SetWindowPos(hwnd, IntPtr.Zero, area.Right - w - gap, area.Bottom - h - gap, 0, 0, 0x0001 | 0x0004 /* NOSIZE | NOZORDER */);
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // A Show from a tray click does not reliably raise the window; the
        // Topmost pulse brings it to the front, Activate gives it focus.
        Topmost = true;
        Topmost = false;
        Activate();
    }

    // Close hides to the tray; the app ends through the tray menu.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    internal void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    // ── save footer ───────────────────────────────────────────────────────────

    // Shown while a page differs from the saved config or holds invalid
    // input; invalid input disables Save. Pages call this while they are
    // still being built, before the fields below are assigned.
    void UpdateSaveBar()
    {
        if (_outputs == null || _sensors == null || _settings == null) return;
        bool invalid = _outputs.HasInvalidInput;
        bool dirty = invalid || _outputs.HasChanges || _sensors.HasChanges || _settings.HasChanges;
        SaveBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !invalid;
        SaveIcon.Text = char.ConvertFromUtf32(invalid ? Glyphs.Warning : Glyphs.Edit);
        SaveIcon.SetResourceReference(TextBlock.ForegroundProperty,
            invalid ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        SaveText.Text = invalid ? L.T.SaveInvalidInput : L.T.UnsavedChanges;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();
    void Discard_Click(object sender, RoutedEventArgs e) => Revert();

    void Save()
    {
        if (_outputs.HasInvalidInput) return;
        bool outputsChanged = _outputs.HasChanges;
        _outputs.Apply();
        _sensors.Apply();
        bool languageChanged = _settings.Apply();
        ConfigLoader.Save(_configPath, _config);
        ApplyTheme(_config.General.Theme);

        // Strings are read once at startup; the restarted instance reads the
        // saved config and reopens on the Settings page.
        if (languageChanged && !DemoMode.Active)
        {
            try { File.WriteAllText(RestartMarker, ""); } catch { }
            Program.Restart();
            return;
        }
        if (outputsChanged) Program.RequestSinksReload();
        UpdateSaveBar();
    }

    void Revert()
    {
        _outputs.Revert();
        _sensors.Revert();
        _settings.Revert();
        UpdateSaveBar();
    }

    // ── state from the tray and the publish loop ─────────────────────────────

    internal void SetUpdateAvailable(Version version)
    {
        Title = $"PC MQTT Monitor  v{UpdateChecker.CurrentVersion.ToString(3)}  —  {string.Format(L.T.TitleUpdate, version.ToString(3))}";
        _aboutBadge.Visibility = Visibility.Visible;
    }

    // UI thread only (TrayApp's central pause switch).
    internal void SetPauseState(bool paused) =>
        _pauseBar.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;

    // Any thread. Only the visible page is refreshed.
    internal void SetLatestMetrics(MetricsSnapshot m)
    {
        _lastMetrics = m;
        int page = _activePage;
        if (page == 0)
            Dispatcher.BeginInvoke(() =>
            {
                try { _dashboard.SetMetrics(m); }
                catch (Exception ex) { AppLog.Write($"[UI] dashboard refresh failed: {ex}"); }
            });
        else if (page == 2)
            Dispatcher.BeginInvoke(_sensors.RefreshFanRpm);
    }

    // Any thread: startup phase on the dashboard until the first snapshot.
    internal void SetLoadingStatus(string status)
    {
        if (_lastMetrics == null) Dispatcher.BeginInvoke(() => _dashboard.SetPlaceholder(status));
    }

    // Any thread: the status line of a sink's Outputs card.
    internal void SetSinkStatus(string sink, StatusKind? kind, string text)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => _outputs.SetSinkStatus(sink, kind, text));
    }

    // ── hooks for DemoMode ────────────────────────────────────────────────────

    internal OutputsPage DemoOutputs => _outputs;
    internal void DemoFeed(MetricsSnapshot m) { _lastMetrics = m; _dashboard.SetMetrics(m); }
    internal void DemoSave() => Save();
    internal void DemoRevert() => Revert();
    internal void DemoMarkDirty() => _settings.DebugSwitch.IsChecked = !_settings.DebugSwitch.IsChecked;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
}

// Segoe Fluent Icons code points (Segoe MDL2 Assets on Windows 10 has the same ones).
static class Glyphs
{
    public const int Dashboard = 0xF0E2, Outputs = 0xE93E, Sensors = 0xE950, Settings = 0xE713,
                     About = 0xE946, Warning = 0xE7BA, Edit = 0xE70F, Pause = 0xE769,
                     ChevronDown = 0xE70D, ChevronUp = 0xE70E;

    static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock Icon(int glyph, double size) => new()
    {
        Text = char.ConvertFromUtf32(glyph), FontFamily = Font, FontSize = size,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
    };
}
