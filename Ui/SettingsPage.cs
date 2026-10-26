using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

// The Settings page: general options as title+subtitle rows with the control
// on the right, split into two cards (general / updates), per the mockup.
sealed class SettingsPage : Panel
{
    readonly AppConfig _config;

    readonly InputBox _interval;
    readonly ToggleSwitch _autoStart, _debug, _updateCheck;
    readonly ChoiceBox _theme, _language;
    bool _autoStartInitial;

    static readonly string[] ThemeValues = ["system", "light", "dark"];
    static readonly string[] LangValues  = ["system", "en", "de"];

    public SettingsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        BackColor = Theme.WinBg;

        var title = new Label
        {
            Text = L.T.SettingsTitle, Font = Theme.Title, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(Theme.S(16), Theme.S(12))
        };
        Controls.Add(title);

        // Plain text field (no spinner arrows); red while the text is not a
        // number of seconds in range, and Save is blocked.
        _interval = new InputBox { Rule = t => TryParseInterval(t, out var s) && s is >= MinInterval and <= MaxInterval };
        _interval.Box.TextAlign = HorizontalAlignment.Center;
        _interval.Box.Text = FormatInterval(config.General.PublishIntervalSeconds);
        _interval.Box.TextChanged += (_, _) => markDirty();
        _autoStart   = MakeToggle(markDirty);
        _debug       = MakeToggle(markDirty);
        _updateCheck = MakeToggle(markDirty);
        _debug.SetChecked(config.General.DebugEnabled);
        _updateCheck.SetChecked(config.General.UpdateCheckEnabled);

        _theme = new ChoiceBox([L.T.ThemeSystem, L.T.ThemeLight, L.T.ThemeDark]);
        _theme.SelectedIndex = IndexOr0(ThemeValues, config.General.Theme);
        _theme.SelectedIndexChanged += (_, _) => markDirty();

        // Language names stay in their own language — the standard convention.
        _language = new ChoiceBox([L.T.LangSystem, "English", "Deutsch"]);
        _language.SelectedIndex = IndexOr0(LangValues, config.General.Language);
        _language.SelectedIndexChanged += (_, _) => markDirty();

        // One width for the three right-hand fields so their edges line up.
        int fieldW = Math.Max(_theme.Width, _language.Width);
        _theme.Width = _language.Width = _interval.Width = fieldW;

        var card1 = new SettingsCard { Location = new Point(Theme.S(16), Theme.PageTop) };
        card1.AddRow(L.T.SetInterval, L.T.SetIntervalSub, _interval);
        card1.AddRow(L.T.SetTheme, L.T.SetRestartSub, _theme);
        card1.AddRow(L.T.SetLanguage, L.T.SetRestartSub, _language);
        card1.AddRow(L.T.SetAutostart, L.T.SetAutostartSub, _autoStart);
        card1.AddRow(L.T.SetDebug, L.T.SetDebugSub, _debug);
        Controls.Add(card1);

        var card2 = new SettingsCard { Location = new Point(Theme.S(16), card1.Bottom + Theme.S(10)) };
        card2.AddRow(L.T.SetUpdates, L.T.SetUpdatesSub, _updateCheck);
        Controls.Add(card2);

        foreach (var card in new[] { card1, card2 })
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Resize += (_, _) => { card1.Width = Width - Theme.S(32); card2.Width = Width - Theme.S(32); };

        // schtasks.exe can take 1-2 s — read the autostart state off the UI thread.
        Task.Run(AutoStart.IsEnabled).ContinueWith(t =>
        {
            if (t.IsFaulted || IsDisposed) return;
            BeginInvoke(() =>
            {
                _autoStartInitial = t.Result;
                _autoStart.SetChecked(t.Result);
            });
        });
    }

    static ToggleSwitch MakeToggle(Action markDirty)
    {
        var t = new ToggleSwitch();
        t.CheckedChanged += (_, _) => markDirty();
        return t;
    }

    // Combo index of a config value; unknown values select "system" (index 0).
    static int IndexOr0(string[] values, string? value) =>
        Math.Max(0, Array.IndexOf(values, value?.ToLowerInvariant()));

    // Discard edits: reset every control to the saved config.
    public void Revert()
    {
        _interval.Box.Text = FormatInterval(_config.General.PublishIntervalSeconds);
        _debug.SetChecked(_config.General.DebugEnabled);
        _updateCheck.SetChecked(_config.General.UpdateCheckEnabled);
        _autoStart.SetChecked(_autoStartInitial);
        _theme.SelectedIndex = IndexOr0(ThemeValues, _config.General.Theme);
        _language.SelectedIndex = IndexOr0(LangValues, _config.General.Language);
    }

    // "1,0 s" in German, "1.0 s" in English.
    static string FormatInterval(double seconds) => seconds.ToString("0.0", L.Culture) + " s";

    const double MinInterval = 0.5, MaxInterval = 3600;

    // Accepts "1.5", "1,5" and a trailing "s".
    static bool TryParseInterval(string text, out double seconds) =>
        double.TryParse(text.Trim().TrimEnd('s', 'S').Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);

    // Interval as Apply would store it; the current value while the text is invalid.
    double IntervalValue() =>
        TryParseInterval(_interval.Box.Text, out var seconds)
            ? Math.Clamp(seconds, MinInterval, MaxInterval)
            : _config.General.PublishIntervalSeconds;

    public bool HasInvalidInput => _interval.Invalid;

    // True when the controls differ from the saved config.
    public bool HasChanges =>
        IntervalValue() != _config.General.PublishIntervalSeconds
        || _debug.Checked != _config.General.DebugEnabled
        || _updateCheck.Checked != _config.General.UpdateCheckEnabled
        || _autoStart.Checked != _autoStartInitial
        || !string.Equals(ThemeValues[_theme.SelectedIndex], _config.General.Theme, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(LangValues[_language.SelectedIndex], _config.General.Language, StringComparison.OrdinalIgnoreCase);

    // Returns true when the app must restart to apply (theme or language
    // change — WinForms can switch neither color mode nor strings at runtime).
    public bool Apply()
    {
        _config.General.PublishIntervalSeconds = IntervalValue();
        _interval.Box.Text = FormatInterval(_config.General.PublishIntervalSeconds);
        _config.General.DebugEnabled           = _debug.Checked;
        _config.General.UpdateCheckEnabled     = _updateCheck.Checked;

        if (_autoStart.Checked != _autoStartInitial)
        {
            var enable = _autoStart.Checked;
            _autoStartInitial = enable;
            // schtasks is slow — off the UI thread. On failure revert the
            // toggle (silently, SetChecked fires no dirty event) so the UI
            // never claims an autostart state that was not registered.
            Task.Run(() => AutoStart.Apply(enable)).ContinueWith(t =>
            {
                if (!t.IsFaulted && t.Result) return;
                AppLog.Write($"[error] autostart {(enable ? "register" : "remove")} failed (schtasks)");
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    _autoStartInitial = !enable;
                    _autoStart.SetChecked(!enable);
                });
            });
        }

        var theme = ThemeValues[_theme.SelectedIndex];
        bool themeChanged = !string.Equals(_config.General.Theme, theme, StringComparison.OrdinalIgnoreCase);
        _config.General.Theme = theme;

        var language = LangValues[_language.SelectedIndex];
        bool languageChanged = !string.Equals(_config.General.Language, language, StringComparison.OrdinalIgnoreCase);
        _config.General.Language = language;

        return themeChanged || languageChanged;
    }
}

// A card of settings rows: title + grey subtitle on the left, the control
// vertically centered on the right, thin separators between rows.
sealed class SettingsCard : CardPanel
{
    static readonly int RowH = Theme.S(60);
    int _rows;

    public void AddRow(string title, string subtitle, Control control)
    {
        int top = Theme.S(6) + _rows * RowH;
        var titleLbl = new Label
        {
            Text = title, ForeColor = Theme.Fg, AutoSize = true,
            Location = new Point(Theme.S(16), top + Theme.S(11))
        };
        var subLbl = new Label
        {
            Text = subtitle, ForeColor = Theme.Fg2, Font = Theme.Tiny, AutoSize = true,
            Location = new Point(Theme.S(16), top + Theme.S(31))
        };
        control.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        control.Location = new Point(Width - control.Width - Theme.S(16), top + (RowH - control.Height) / 2);
        Controls.Add(titleLbl);
        Controls.Add(subLbl);
        Controls.Add(control);

        if (_rows > 0)
        {
            var sep = new Panel
            {
                Height = 1, BackColor = Theme.CardBorder,
                Bounds = new Rectangle(Theme.S(14), top, Width - Theme.S(28), 1),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(sep);
        }

        _rows++;
        Height = Theme.S(12) + _rows * RowH;
    }
}
