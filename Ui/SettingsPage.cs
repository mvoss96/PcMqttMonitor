using System.Drawing;
using System.Windows.Forms;

// The Settings page: general options as title+subtitle rows with the control
// on the right, split into two cards (general / updates), per the mockup.
sealed class SettingsPage : Panel
{
    readonly AppConfig _config;

    readonly NumericUpDown _interval;
    readonly ToggleSwitch _autoStart, _debug, _updateCheck;
    bool _autoStartInitial;

    public SettingsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        BackColor = Theme.WinBg;

        var title = new Label
        {
            Text = "Settings", Font = Theme.Title, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(16, 12)
        };
        Controls.Add(title);

        _interval = new NumericUpDown
        {
            Minimum = 0.5m, Maximum = 3600, DecimalPlaces = 1, Increment = 0.5m,
            Value = (decimal)config.General.PublishIntervalSeconds,
            Width = 70, TextAlign = HorizontalAlignment.Right,
            BackColor = Theme.InputBg, ForeColor = Theme.Fg,
        };
        _interval.ValueChanged += (_, _) => markDirty();
        _autoStart   = MakeToggle(markDirty);
        _debug       = MakeToggle(markDirty);
        _updateCheck = MakeToggle(markDirty);
        _debug.SetChecked(config.General.DebugEnabled);
        _updateCheck.SetChecked(config.General.UpdateCheckEnabled);

        var card1 = new SettingsCard { Location = new Point(16, 42) };
        card1.AddRow("Publish interval", "Seconds between two measurements", _interval);
        card1.AddRow("Start with Windows", "Scheduled task with highest privileges", _autoStart);
        card1.AddRow("Debug logging", "Verbose entries in app.log", _debug);
        Controls.Add(card1);

        var card2 = new SettingsCard { Location = new Point(16, card1.Bottom + 10) };
        card2.AddRow("Notify about new versions", "Checks the GitHub releases daily", _updateCheck);
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

    public void Apply()
    {
        _config.General.PublishIntervalSeconds = (double)_interval.Value;
        _config.General.DebugEnabled           = _debug.Checked;
        _config.General.UpdateCheckEnabled     = _updateCheck.Checked;

        if (_autoStart.Checked != _autoStartInitial)
        {
            var enable = _autoStart.Checked;
            _autoStartInitial = enable;
            Task.Run(() => AutoStart.Apply(enable));   // schtasks is slow — off the UI thread
        }
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
