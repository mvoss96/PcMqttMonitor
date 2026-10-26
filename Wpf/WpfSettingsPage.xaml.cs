using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

// WPF port of SettingsPage: same options and the same save/revert logic.
// The theme applies on Save without restarting the app.
public partial class WpfSettingsPage : UserControl
{
    readonly AppConfig _config;
    readonly Action<string?> _applyTheme;
    readonly Action _saveConfig;
    readonly double[] _intervalValues;
    bool _autoStartInitial;

    static readonly double[] IntervalPresets = [0.5, 1, 2, 5, 10, 30, 60];
    static readonly string[] ThemeValues = ["system", "light", "dark"];
    static readonly string[] LangValues  = ["system", "en", "de"];

    internal WpfSettingsPage(AppConfig config, Action markDirty, Action<string?> applyTheme, Action saveConfig)
    {
        InitializeComponent();
        DataContext = L.T;
        _config = config;
        _applyTheme = applyTheme;
        _saveConfig = saveConfig;
        ThemeSub.Text = L.T.SetThemeLiveSub;

        _intervalValues = IntervalPresets.Append(config.General.PublishIntervalSeconds)
            .Distinct().Order().ToArray();
        Interval.ItemsSource = _intervalValues.Select(s => s.ToString("0.#", L.Culture) + " s").ToArray();
        ThemeChoice.ItemsSource = new[] { L.T.ThemeSystem, L.T.ThemeLight, L.T.ThemeDark };
        LanguageChoice.ItemsSource = new[] { L.T.LangSystem, "English", "Deutsch" };
        Revert();

        foreach (var c in new[] { Interval, ThemeChoice, LanguageChoice })
            c.SelectionChanged += (_, _) => markDirty();
        foreach (var s in new[] { AutoStartSwitch, DebugSwitch, UpdateSwitch })
        {
            s.Checked += (_, _) => markDirty();
            s.Unchecked += (_, _) => markDirty();
        }

        // schtasks.exe can take 1-2 s: read the autostart state off the UI thread.
        Task.Run(AutoStart.IsEnabled).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Dispatcher.Invoke(() =>
            {
                _autoStartInitial = t.Result;
                AutoStartSwitch.IsChecked = t.Result;
            });
        });
    }

    static int IndexOr0(string[] values, string? value) =>
        Math.Max(0, Array.IndexOf(values, value?.ToLowerInvariant()));

    double IntervalValue() => _intervalValues[Interval.SelectedIndex];

    // Reset every control to the saved config.
    public void Revert()
    {
        Interval.SelectedIndex = Array.IndexOf(_intervalValues, _config.General.PublishIntervalSeconds);
        ThemeChoice.SelectedIndex = IndexOr0(ThemeValues, _config.General.Theme);
        LanguageChoice.SelectedIndex = IndexOr0(LangValues, _config.General.Language);
        DebugSwitch.IsChecked = _config.General.DebugEnabled;
        UpdateSwitch.IsChecked = _config.General.UpdateCheckEnabled;
        AutoStartSwitch.IsChecked = _autoStartInitial;
    }

    public bool HasChanges =>
        IntervalValue() != _config.General.PublishIntervalSeconds
        || DebugSwitch.IsChecked != _config.General.DebugEnabled
        || UpdateSwitch.IsChecked != _config.General.UpdateCheckEnabled
        || AutoStartSwitch.IsChecked != _autoStartInitial
        || ThemeValues[ThemeChoice.SelectedIndex] != _config.General.Theme?.ToLowerInvariant()
        || LangValues[LanguageChoice.SelectedIndex] != _config.General.Language?.ToLowerInvariant();

    // Write the controls into the config and save it. Autostart is not
    // registered from the prototype; a language change still needs a restart.
    public void Apply()
    {
        _config.General.PublishIntervalSeconds = IntervalValue();
        _config.General.DebugEnabled = DebugSwitch.IsChecked == true;
        _config.General.UpdateCheckEnabled = UpdateSwitch.IsChecked == true;
        _autoStartInitial = AutoStartSwitch.IsChecked == true;
        _config.General.Theme = ThemeValues[ThemeChoice.SelectedIndex];
        _config.General.Language = LangValues[LanguageChoice.SelectedIndex];
        _saveConfig();
        _applyTheme(_config.General.Theme);
    }
}
