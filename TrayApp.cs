using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

// The tray icon (WinForms NotifyIcon; WPF has none), its Fluent context menu
// and the main window.
sealed class TrayApp : IDisposable
{
    readonly Forms.NotifyIcon _tray;
    readonly MainWindow _window;
    readonly Icon _normalIcon;
    readonly Icon _pausedIcon;
    readonly AppConfig _config;
    Icon? _valueIcon;          // the current number/bars icon, if any
    string? _valueKey;         // what _valueIcon shows; unchanged key = no redraw
    readonly ContextMenu _menu = new() { Placement = PlacementMode.MousePoint };
    readonly MenuItem _statusItem = new() { IsEnabled = false };
    readonly MenuItem _pauseItem = new() { Header = L.T.TrayPause };
    MenuItem? _updateItem;
    Version? _notifiedUpdate;
    volatile string _statusText = L.T.StatusStarting;
    volatile bool _paused;

    public bool IsPaused => _paused;

    public TrayApp(CancellationTokenSource shutdown, string configPath, AppConfig config)
    {
        _config = config;
        _window = new MainWindow(configPath, config) { PauseChangeRequested = SetPaused };

        _normalIcon = CreateIcon();
        _pausedIcon = CreatePausedIcon(_normalIcon);
        _tray = new Forms.NotifyIcon { Icon = _normalIcon, Visible = true, Text = "PC MQTT Monitor" };

        _pauseItem.Click += (_, _) => SetPaused(!_paused);
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item(L.T.NavDashboard, _window.ShowDashboard));
        _menu.Items.Add(Item(L.T.NavSettings, _window.ShowSettings));
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(Item(L.T.TrayExit, () =>
        {
            shutdown.Cancel();
            _tray.Visible = false;
            _window.CloseForExit();
            Application.Current.Shutdown();
        }));

        // Left click opens the dashboard, right click the menu.
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window.ShowDashboard();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };

        // The only balloon this app shows is the update notification.
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_notifiedUpdate != null) OpenReleasesPage();
        };
    }

    // Called when a second start of the app was redirected here.
    public void ShowWindow() => _window.ShowDashboard();

    static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    // A WPF menu opened from the tray only closes on an outside click when
    // its popup window is the foreground window.
    void ShowMenu()
    {
        _statusItem.Header = _statusText;
        _menu.IsOpen = true;
        if (PresentationSource.FromVisual(_menu) is HwndSource source)
            SetForegroundWindow(source.Handle);
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    // Called from the update-check thread when a newer GitHub release exists.
    // Adds a menu entry and shows a balloon once per discovered version.
    public void NotifyUpdateAvailable(Version version)
    {
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_notifiedUpdate != null && version <= _notifiedUpdate) return;
            _notifiedUpdate = version;

            if (_updateItem == null)
            {
                _updateItem = Item("", OpenReleasesPage);
                _updateItem.FontWeight = FontWeights.SemiBold;
                _menu.Items.Insert(2, _updateItem);   // under the status line
            }
            _updateItem.Header = string.Format(L.T.TrayUpdateItem, version.ToString(3));
            _window.SetUpdateAvailable(version);

            _tray.BalloonTipTitle = "PC MQTT Monitor";
            _tray.BalloonTipText  = string.Format(L.T.BalloonUpdate, version.ToString(3));
            _tray.BalloonTipIcon  = Forms.ToolTipIcon.Info;
            _tray.ShowBalloonTip(10_000);
        });
    }

    // Also used by the About page's download link.
    internal static void OpenReleasesPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = UpdateChecker.ReleasesPageUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { AppLog.Write($"[update] open releases page failed: {ex.Message}"); }
    }

    // Central pause switch: keeps menu, tray icon and the window's pause bar
    // in sync no matter where the change was triggered. UI thread only.
    void SetPaused(bool paused)
    {
        _paused          = paused;
        _pauseItem.Header = paused ? L.T.TrayResume : L.T.TrayPause;
        _tray.Icon       = paused ? _pausedIcon : _valueIcon ?? _normalIcon;
        _window.SetPauseState(paused);
    }

    // Any thread: the status line of the menu, and the dashboard placeholder
    // until the first data arrives.
    public void SetStatus(string status)
    {
        _statusText = status;
        _window.SetLoadingStatus(status);
    }

    // Any thread: a sink's state for the Outputs page.
    public void SetSinkStatus(string sink, StatusKind? kind, string text)
        => _window.SetSinkStatus(sink, kind, text);

    // Publish loop thread: the latest sensor snapshot.
    public void UpdateSnapshot(SensorSnapshot snapshot)
    {
        var tooltip = BuildTooltipText(snapshot.Metrics, _paused);
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _window.SetLatestMetrics(snapshot.Metrics);
        _window.Dispatcher.BeginInvoke(() => UpdateValueIcon(snapshot.Metrics));
    }

    // Shows the value chosen in Settings (GeneralConfig.TrayIcon) as the tray
    // icon; the logo when "logo" is chosen or the value is unavailable, the
    // paused logo while paused. UI thread only.
    void UpdateValueIcon(MetricsSnapshot m)
    {
        string mode = _config.General.TrayIcon;
        string key = TrayIcon.Key(m, mode);
        if (key == _valueKey) return;
        _valueKey = key;

        var old = _valueIcon;
        _valueIcon = null;
        using (var bmp = TrayIcon.Render(m, mode, Forms.SystemInformation.SmallIconSize.Width))
            if (bmp != null) _valueIcon = Icon.FromHandle(bmp.GetHicon());
        if (!_paused) _tray.Icon = _valueIcon ?? _normalIcon;
        if (old != null)
        {
            TrayIcon.DestroyIcon(old.Handle);
            old.Dispose();
        }
    }

    // Short single-line summary for the native tray tooltip.
    static string BuildTooltipText(MetricsSnapshot m, bool paused)
    {
        var cpuString = (m.Cpu != null && (m.Cpu.Load != null || m.Cpu.TempC != null))
            ? $"CPU{(m.Cpu.Load != null ? $" {m.Cpu.Load}%" : "")}{(m.Cpu.TempC != null ? $" {m.Cpu.TempC:0}C" : "")}"
            : null;

        var gpuString = (m.Gpu != null && (m.Gpu.Load != null || m.Gpu.TempC != null))
            ? $"GPU{(m.Gpu.Load != null ? $" {m.Gpu.Load}%" : "")}{(m.Gpu.TempC != null ? $" {m.Gpu.TempC:0}C" : "")}"
            : null;

        var ramString = m.Ram?.Load != null ? $"RAM {m.Ram.Load}%" : null;

        var header = paused ? $"PC MQTT Monitor {L.T.TrayPausedSuffix}" : "PC MQTT Monitor";
        var parts  = new[] { header, cpuString, gpuString, ramString }
            .Where(s => !string.IsNullOrEmpty(s));

        return string.Join(" | ", parts);
    }

    // The exe's embedded app.ico; also the window icon.
    internal static Icon CreateIcon()
        => Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;

    // Takes the embedded app.ico and adds a small "||" badge at the bottom-right.
    static Icon CreatePausedIcon(Icon baseIcon)
    {
        using var bmp16 = AddPauseOverlay(baseIcon, 16);
        using var bmp32 = AddPauseOverlay(baseIcon, 32);

        var pngs = new[] { bmp16, bmp32 }.Select(b =>
        {
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();

        using var stream = new MemoryStream();
        using var bw = new BinaryWriter(stream);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)2);
        int offset = 6 + 2 * 16;
        int[] sizes = { 16, 32 };
        for (int i = 0; i < 2; i++)
        {
            bw.Write((byte)sizes[i]); bw.Write((byte)sizes[i]);
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(pngs[i].Length); bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) bw.Write(png);
        stream.Position = 0;
        return new Icon(stream);
    }

    static Bitmap AddPauseOverlay(Icon baseIcon, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        using var sized = new Icon(baseIcon, size, size);
        g.DrawIcon(sized, 0, 0);

        // Small "||" badge in the bottom-right corner.
        int barW   = Math.Max(1, size / 8);
        int barH   = Math.Max(2, size / 4);
        int gap    = Math.Max(1, size / 14);
        int totalW = barW * 2 + gap;
        int margin = Math.Max(1, size / 16);
        int x      = size - totalW - margin;
        int y      = size - barH  - margin;

        using var bg = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        g.FillRectangle(bg, x - 1, y - 1, totalW + 2, barH + 2);

        using var wb = new SolidBrush(Color.White);
        g.FillRectangle(wb, x,              y, barW, barH);
        g.FillRectangle(wb, x + barW + gap, y, barW, barH);

        return bmp;
    }

    public void Dispose()
    {
        _tray.Dispose();
        if (_valueIcon != null) TrayIcon.DestroyIcon(_valueIcon.Handle);
        _normalIcon.Dispose();
        _pausedIcon.Dispose();
    }
}
