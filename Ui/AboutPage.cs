using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

// The About page: logo, name, version, author/license — and the update check,
// which runs automatically every time the page is opened. A found update is
// reported back so MainWindow can show its title-area pill.
sealed class AboutPage : Panel
{
    readonly Label _updStatus;
    readonly LinkLabel _updLink;
    readonly Action<Version> _updateFound;
    CancellationTokenSource? _checkCts;

    public AboutPage(Action<Version> updateFound)
    {
        _updateFound = updateFound;
        BackColor = Theme.WinBg;

        var logo = new LogoBox { Size = new Size(Theme.S(52), Theme.S(52)) };
        var name = new Label
        {
            Text = "PC MQTT Monitor", Font = Theme.Title,
            ForeColor = Theme.Fg, AutoSize = true
        };
        var version = new Label
        {
            Text = string.Format(L.T.AboutVersion, UpdateChecker.CurrentVersion.ToString(3)),
            ForeColor = Theme.Fg2, AutoSize = true
        };
        _updStatus = new Label { ForeColor = Theme.Fg3, AutoSize = true };
        _updLink = new LinkLabel
        {
            Text = L.T.AboutDownload, AutoSize = true, Visible = false,
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent,
        };
        _updLink.LinkClicked += (_, _) => TrayApp.OpenReleasesPage();

        var repo = new LinkLabel
        {
            Text = "github.com/mvoss96/PcMqttMonitor", AutoSize = true,
            LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent,
        };
        repo.LinkClicked += (_, _) => OpenUrl($"https://github.com/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}");

        var license = new Label
        {
            Text = $"© {DateTime.Now.Year} Marcus Voß — MIT License",
            ForeColor = Theme.Fg3, Font = Theme.Small, AutoSize = true
        };
        var deps = new Label
        {
            Text = L.T.AboutDeps,
            ForeColor = Theme.Fg3, Font = Theme.Small, AutoSize = true
        };

        Controls.AddRange([logo, name, version, _updStatus, _updLink, repo, license, deps]);
    }

    static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) { AppLog.Write($"[ui] open url failed: {ex.Message}"); }
    }

    // Vertical stack, everything horizontally centered.
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        int y = Theme.S(64);
        foreach (Control c in Controls)
        {
            if (c == _updLink && !_updLink.Visible) continue;
            c.Left = (Width - c.Width) / 2;
            c.Top = y;
            y += c.Height + Theme.S(c is LogoBox ? 14 : 6);
            // extra gap between the update block and the repo link
            if (c == _updLink || (c == _updStatus && !_updLink.Visible)) y += Theme.S(10);
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RunUpdateCheck();
        else _checkCts?.Cancel();
    }

    async void RunUpdateCheck()
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();

        _updStatus.Text = L.T.AboutChecking;
        _updStatus.ForeColor = Theme.Fg3;
        _updLink.Visible = false;
        PerformLayout();

        try
        {
            var newer = await UpdateChecker.CheckAsync(cts.Token);
            if (cts.Token.IsCancellationRequested) return;
            if (newer != null)
            {
                _updStatus.Text = string.Format(L.T.AboutUpdateFound, newer.ToString(3));
                _updStatus.ForeColor = Theme.Fg;
                _updLink.Visible = true;
                _updateFound(newer);
            }
            else
            {
                _updStatus.Text = L.T.AboutUpToDate;
                _updStatus.ForeColor = Theme.Good;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _updStatus.Text = string.Format(L.T.AboutCheckFailed, ex.Message);
            _updStatus.ForeColor = Theme.Fg3;
        }
        PerformLayout();
    }

    // The signal-bars logo, drawn with the shared glyph painter.
    sealed class LogoBox : Control
    {
        public LogoBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Theme.WinBg);
            NavIcons.Logo(e.Graphics, new RectangleF(0, 0, Width, Height), Theme.Accent);
        }
    }
}
