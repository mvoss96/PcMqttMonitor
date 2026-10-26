using System.Windows.Controls;

// The Settings page: general options as title + subtitle rows with the
// control on the right. The theme applies on Save without a restart; a
// language change restarts the app.
public partial class SettingsPage : UserControl
{
    readonly AppConfig _config;
    readonly double[] _intervalValues;
    bool _autoStartInitial;

    static readonly double[] IntervalPresets = [0.5, 1, 2, 5, 10, 30, 60];
    static readonly string[] ThemeValues = ["system", "light", "dark"];
    static readonly string[] LangValues  = ["system", "en", "de"];

    internal SettingsPage(AppConfig config, Action markDirty)
    {
        InitializeComponent();
        DataContext = L.T;
        _config = config;
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
        || ThemeChoice.SelectedIndex != IndexOr0(ThemeValues, _config.General.Theme)
        || LanguageChoice.SelectedIndex != IndexOr0(LangValues, _config.General.Language);

    // Write the controls into the config. Returns true when the language
    // changed, which needs a restart.
    public bool Apply()
    {
        _config.General.PublishIntervalSeconds = IntervalValue();
        _config.General.DebugEnabled = DebugSwitch.IsChecked == true;
        _config.General.UpdateCheckEnabled = UpdateSwitch.IsChecked == true;
        _config.General.Theme = ThemeValues[ThemeChoice.SelectedIndex];

        bool enable = AutoStartSwitch.IsChecked == true;
        if (enable != _autoStartInitial)
        {
            _autoStartInitial = enable;
            // schtasks is slow: off the UI thread. On failure the switch goes
            // back, so the UI never claims a task that was not registered.
            if (!DemoMode.Active)
                Task.Run(() => AutoStart.Apply(enable)).ContinueWith(t =>
                {
                    if (!t.IsFaulted && t.Result) return;
                    AppLog.Write($"[error] autostart {(enable ? "register" : "remove")} failed (schtasks)");
                    Dispatcher.Invoke(() =>
                    {
                        _autoStartInitial = !enable;
                        AutoStartSwitch.IsChecked = !enable;
                    });
                });
        }

        int lang = LanguageChoice.SelectedIndex;
        bool languageChanged = lang != IndexOr0(LangValues, _config.General.Language);
        _config.General.Language = LangValues[lang];
        return languageChanged;
    }
}
