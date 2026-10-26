using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;

// WPF prototype of the main window: Fluent theme (Windows 11 controls and
// Mica backdrop), icon sidebar, one page at a time and the save footer.
// Only the Settings page is ported; the other entries show a placeholder.
public partial class WpfMainWindow : Window
{
    readonly WpfSettingsPage _settings;
    readonly UIElement[] _pages;

    internal WpfMainWindow(AppConfig config, Action saveConfig)
    {
        InitializeComponent();
        Title = $"PC MQTT Monitor  v{UpdateChecker.CurrentVersion.ToString(3)}";
        SaveButton.Content = L.T.Save;
        DiscardButton.Content = L.T.Discard;
        using (var icon = TrayApp.CreateIcon())
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());

        ApplyTheme(config.General.Theme);
        _settings = new WpfSettingsPage(config, UpdateSaveBar, ApplyTheme, saveConfig);

        (string Tip, int Glyph)[] nav =
        [
            (L.T.NavDashboard, NavIcons.Dashboard),
            (L.T.NavOutputs,   NavIcons.Outputs),
            (L.T.NavSensors,   NavIcons.Sensors),
            (L.T.NavSettings,  NavIcons.Settings),
            (L.T.NavAbout,     NavIcons.About),
        ];
        _pages = nav.Select((n, i) => i == 3 ? (UIElement)_settings : Placeholder(n.Tip)).ToArray();
        foreach (var n in nav)
            Nav.Items.Add(new ListBoxItem
            {
                ToolTip = n.Tip,
                Content = new TextBlock { Text = char.ConvertFromUtf32(n.Glyph), FontFamily = new("Segoe Fluent Icons"), FontSize = 16 },
            });

        Nav.SelectedIndex = 3;
    }

    internal void SelectPage(int index) => Nav.SelectedIndex = index;

    // Fluent theme for the whole app; switches live, no restart needed.
    static void ApplyTheme(string? theme)
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
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("TitleTextBlockStyle") });
        panel.Children.Add(new TextBlock
        {
            Text = "Not ported yet (WPF prototype).", Margin = new Thickness(0, 12, 0, 0),
            Style = (Style)Application.Current.FindResource("BodyTextBlockStyle"),
        });
        return panel;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedIndex >= 0)
            PageHost.Content = _pages[Nav.SelectedIndex];
    }

    // Shown while the page differs from the saved config.
    void UpdateSaveBar()
    {
        SaveBar.Visibility = _settings.HasChanges ? Visibility.Visible : Visibility.Collapsed;
        SaveIcon.Text = char.ConvertFromUtf32(NavIcons.Edit);
        SaveText.Text = L.T.UnsavedChanges;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.Apply();
        UpdateSaveBar();
    }

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        _settings.Revert();
        UpdateSaveBar();
    }

    // Flip one switch so the save footer shows (screenshots).
    internal void DemoMarkDirty() => _settings.DebugSwitch.IsChecked = !_settings.DebugSwitch.IsChecked;
}
