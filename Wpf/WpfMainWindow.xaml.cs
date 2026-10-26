using System.Windows;
using System.Windows.Controls;

// WPF prototype of the main window: Fluent theme (Windows 11 controls and
// Mica backdrop), icon sidebar, one page at a time and the save footer.
// Dashboard, Outputs and Settings are ported; Sensors and About show a
// placeholder.
public partial class WpfMainWindow : Window
{
    readonly AppConfig _config;
    readonly Action _saveConfig;
    readonly WpfDashboard _dashboard = new();
    readonly WpfOutputsPage _outputs;
    readonly WpfSettingsPage _settings;
    readonly UIElement[] _pages;

    internal WpfMainWindow(AppConfig config, Action saveConfig)
    {
        InitializeComponent();
        _config = config;
        _saveConfig = saveConfig;
        Title = $"PC MQTT Monitor  v{UpdateChecker.CurrentVersion.ToString(3)}";
        SaveButton.Content = L.T.Save;
        DiscardButton.Content = L.T.Discard;
        using (var icon = TrayApp.CreateIcon())
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        _outputs = new WpfOutputsPage(config, UpdateSaveBar);
        _settings = new WpfSettingsPage(config, UpdateSaveBar);
        _pages = [_dashboard, _outputs, Placeholder(L.T.NavSensors), _settings, Placeholder(L.T.NavAbout)];

        (string Tip, int Glyph)[] nav =
        [
            (L.T.NavDashboard, NavIcons.Dashboard),
            (L.T.NavOutputs,   NavIcons.Outputs),
            (L.T.NavSensors,   NavIcons.Sensors),
            (L.T.NavSettings,  NavIcons.Settings),
            (L.T.NavAbout,     NavIcons.About),
        ];
        foreach (var n in nav)
            Nav.Items.Add(new ListBoxItem
            {
                ToolTip = n.Tip,
                Content = new TextBlock { Text = char.ConvertFromUtf32(n.Glyph), FontFamily = new("Segoe Fluent Icons"), FontSize = 16 },
            });
        Nav.SelectedIndex = 0;
    }

    internal void SelectPage(int index) => Nav.SelectedIndex = index;

    // Fluent theme for the whole app; switches live, no restart needed.
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

    static UIElement Placeholder(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("PageTitle") });
        panel.Children.Add(new TextBlock { Text = "Not ported yet (WPF prototype)." });
        return panel;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex >= 0)
            PageHost.Content = _pages[Nav.SelectedIndex];
    }

    // ── data from the publish loop (any thread) ──────────────────────────────

    internal void SetLatestMetrics(MetricsSnapshot m) => Dispatcher.BeginInvoke(() => _dashboard.SetMetrics(m));
    internal void SetLoadingStatus(string status) => Dispatcher.BeginInvoke(() => _dashboard.SetPlaceholder(status));
    internal void SetSinkStatus(string sink, StatusKind? kind, string text) =>
        Dispatcher.BeginInvoke(() => _outputs.SetSinkStatus(sink, kind, text));

    // ── save footer ───────────────────────────────────────────────────────────

    // Shown while a page differs from the saved config or holds invalid
    // input; invalid input disables Save. Pages call this while they are
    // still being built, before the fields below are assigned.
    void UpdateSaveBar()
    {
        if (_outputs == null || _settings == null) return;
        bool invalid = _outputs.HasInvalidInput;
        bool dirty = invalid || _outputs.HasChanges || _settings.HasChanges;
        SaveBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = !invalid;
        SaveIcon.Text = char.ConvertFromUtf32(invalid ? NavIcons.Warning : NavIcons.Edit);
        SaveIcon.SetResourceReference(TextBlock.ForegroundProperty,
            invalid ? "SystemFillColorCriticalBrush" : "AccentTextFillColorPrimaryBrush");
        SaveText.Text = invalid ? L.T.SaveInvalidInput : L.T.UnsavedChanges;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_outputs.HasInvalidInput) return;
        _outputs.Apply();
        _settings.Apply();
        _saveConfig();
        ApplyTheme(_config.General.Theme);
        UpdateSaveBar();
    }

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        _outputs.Revert();
        _settings.Revert();
        UpdateSaveBar();
    }

    // ── hooks for the screenshot run ──────────────────────────────────────────

    internal WpfOutputsPage DemoOutputs => _outputs;
    internal void DemoMarkDirty() => _settings.DebugSwitch.IsChecked = !_settings.DebugSwitch.IsChecked;
}
