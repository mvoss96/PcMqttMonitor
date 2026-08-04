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
    readonly ComboBox _theme, _language;
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
            AutoSize = true, Location = new Point(16, 12)
        };
        Controls.Add(title);

        // Plain text field (no spinner arrows) — validated and clamped on Save.
        _interval = new InputBox();
        _interval.Box.TextAlign = HorizontalAlignment.Center;
        _interval.Box.Text = config.General.PublishIntervalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        _interval.Box.TextChanged += (_, _) => markDirty();
        _autoStart   = MakeToggle(markDirty);
        _debug       = MakeToggle(markDirty);
        _updateCheck = MakeToggle(markDirty);
        _debug.SetChecked(config.General.DebugEnabled);
        _updateCheck.SetChecked(config.General.UpdateCheckEnabled);

        // Standard rendering on purpose — the dark color mode themes the native
        // ComboBox correctly; FlatStyle/custom colors break its arrow drawing.
        _theme = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
        };
        _theme.Items.AddRange([L.T.ThemeSystem, L.T.ThemeLight, L.T.ThemeDark]);
        int themeIdx = Array.IndexOf(ThemeValues, config.General.Theme?.ToLowerInvariant());
        _theme.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;
        _theme.SelectedIndexChanged += (_, _) => markDirty();

        // Language names stay in their own language — the standard convention.
        _language = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
        };
        _language.Items.AddRange([L.T.LangSystem, "English", "Deutsch"]);
        int langIdx = Array.IndexOf(LangValues, config.General.Language?.ToLowerInvariant());
        _language.SelectedIndex = langIdx >= 0 ? langIdx : 0;
        _language.SelectedIndexChanged += (_, _) => markDirty();

        var card1 = new SettingsCard { Location = new Point(16, 42) };
        card1.AddRow(L.T.SetInterval, L.T.SetIntervalSub, _interval);
        card1.AddRow(L.T.SetTheme, L.T.SetRestartSub, _theme);
        card1.AddRow(L.T.SetLanguage, L.T.SetRestartSub, _language);
        card1.AddRow(L.T.SetAutostart, L.T.SetAutostartSub, _autoStart);
        card1.AddRow(L.T.SetDebug, L.T.SetDebugSub, _debug);
        Controls.Add(card1);

        var card2 = new SettingsCard { Location = new Point(16, card1.Bottom + 10) };
        card2.AddRow(L.T.SetUpdates, L.T.SetUpdatesSub, _updateCheck);
        Controls.Add(card2);

        foreach (var card in new[] { card1, card2 })
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Resize += (_, _) => { card1.Width = Width - 32; card2.Width = Width - 32; };

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

    // Returns true when the app must restart to apply (theme or language
    // change — WinForms can switch neither color mode nor strings at runtime).
    public bool Apply()
    {
        // Accept both "1.5" and "1,5"; keep the previous value on garbage input.
        if (double.TryParse(_interval.Box.Text.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            _config.General.PublishIntervalSeconds = Math.Clamp(seconds, 0.5, 3600);
        _interval.Box.Text = _config.General.PublishIntervalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        _config.General.DebugEnabled           = _debug.Checked;
        _config.General.UpdateCheckEnabled     = _updateCheck.Checked;

        if (_autoStart.Checked != _autoStartInitial)
        {
            var enable = _autoStart.Checked;
            _autoStartInitial = enable;
            Task.Run(() => AutoStart.Apply(enable));   // schtasks is slow — off the UI thread
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
    const int RowH = 48;
    int _rows;

    public void AddRow(string title, string subtitle, Control control)
    {
        int top = 6 + _rows * RowH;
        var titleLbl = new Label
        {
            Text = title, ForeColor = Theme.Fg, AutoSize = true,
            Location = new Point(14, top + 8)
        };
        var subLbl = new Label
        {
            Text = subtitle, ForeColor = Theme.Fg3, Font = Theme.Tiny, AutoSize = true,
            Location = new Point(14, top + 26)
        };
        control.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        control.Location = new Point(Width - control.Width - 14, top + (RowH - control.Height) / 2);
        Controls.Add(titleLbl);
        Controls.Add(subLbl);
        Controls.Add(control);

        if (_rows > 0)
        {
            var sep = new Panel
            {
                Height = 1, BackColor = Theme.CardBorder,
                Bounds = new Rectangle(14, top, Width - 28, 1),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            Controls.Add(sep);
        }

        _rows++;
        Height = 12 + _rows * RowH;
    }
}
