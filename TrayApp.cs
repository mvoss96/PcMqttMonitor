using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

// Manages the system tray icon, native hover tooltip, and the main window.
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly MainWindow _window;
    readonly Icon _normalIcon;
    readonly Icon _pausedIcon;
    readonly ToolStripMenuItem _pauseItem;
    readonly ContextMenuStrip _menu;
    ToolStripMenuItem? _updateItem;
    Version? _notifiedUpdate;
    volatile string _statusText = "Starting...";
    volatile bool _paused;

    public bool IsPaused => _paused;

    public TrayApp(CancellationTokenSource shutdown, string configPath, AppConfig config)
    {
        _window = new MainWindow(configPath, config);

        _normalIcon = CreateIcon();
        _pausedIcon = CreatePausedIcon(_normalIcon);

        _tray = new NotifyIcon
        {
            Icon    = _normalIcon,
            Visible = true,
            Text    = "PC MQTT Monitor"
        };

        // Left-click opens the sensors tab.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _window.ShowSensorsTab();
        };

        var statusItem = new ToolStripMenuItem(_statusText) { Enabled = false };
        var pauseItem  = new ToolStripMenuItem("Pause Publishing");
        _pauseItem = pauseItem;

        pauseItem.Click += (_, _) => SetPaused(!_paused);

        // The main window's pause banner can request a state change too.
        _window.PauseChangeRequested = SetPaused;

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => statusItem.Text = _statusText;
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Sensors", null, (_, _) => _window.ShowSensorsTab());
        menu.Items.Add("Settings",     null, (_, _) => _window.ShowSettingsTab());
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            shutdown.Cancel();
            _tray.Visible = false;
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
        _menu = menu;

        // The only balloon this app shows is the update notification.
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_notifiedUpdate != null) OpenReleasesPage();
        };
    }

    // Called from the update-check thread when a newer GitHub release exists.
    // Adds a menu entry and shows a balloon once per discovered version.
    public void NotifyUpdateAvailable(Version version)
    {
        _window.BeginInvoke(() =>
        {
            if (_notifiedUpdate != null && version <= _notifiedUpdate) return;
            _notifiedUpdate = version;

            if (_updateItem == null)
            {
                _updateItem = new ToolStripMenuItem
                {
                    Font = new Font(_menu.Font, FontStyle.Bold)
                };
                _updateItem.Click += (_, _) => OpenReleasesPage();
                // Directly under the status line, above Show Sensors.
                _menu.Items.Insert(2, _updateItem);
            }
            _updateItem.Text = $"Update available: v{version.ToString(3)}";

            _tray.BalloonTipTitle = "PC MQTT Monitor";
            _tray.BalloonTipText  = $"Version {version.ToString(3)} is available — click to open the download page.";
            _tray.BalloonTipIcon  = ToolTipIcon.Info;
            _tray.ShowBalloonTip(10_000);
        });
    }

    // Also used by the Settings tab's "Check for updates" button.
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

    // Central pause switch — keeps tray menu, tray icon and the main window's
    // banner in sync no matter where the change was triggered. UI thread only.
    void SetPaused(bool paused)
    {
        _paused         = paused;
        _pauseItem.Text = paused ? "Resume Publishing" : "Pause Publishing";
        _tray.Icon      = paused ? _pausedIcon : _normalIcon;
        _window.SetPauseState(paused);
    }

    // Called from any thread to update the status shown in the context menu.
    public void SetStatus(string status) => _statusText = status;

    // Called from the MQTT loop thread to update the connection indicator in the Settings tab.
    public void SetConnectionStatus(bool connected, string broker)
        => _window.SetConnectionStatus(connected, broker);

    // Called from the MQTT loop thread with the latest sensor snapshot.
    public void UpdateSnapshot(SensorSnapshot snapshot)
    {
        var tooltip = BuildTooltipText(snapshot.Metrics, _paused);
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _window.SetLatestMetrics(snapshot.Metrics);
    }

    // Short single-line summary for the native OS tooltip.
    static string BuildTooltipText(MqttMetrics m, bool paused)
    {
        var cpuString = (m.Cpu != null && (m.Cpu.Load != null || m.Cpu.TempC != null))
            ? $"CPU{(m.Cpu.Load != null ? $" {m.Cpu.Load}%" : "")}{(m.Cpu.TempC != null ? $" {m.Cpu.TempC:0}C" : "")}"
            : null;

        var gpuString = (m.Gpu != null && (m.Gpu.Load != null || m.Gpu.TempC != null))
            ? $"GPU{(m.Gpu.Load != null ? $" {m.Gpu.Load}%" : "")}{(m.Gpu.TempC != null ? $" {m.Gpu.TempC:0}C" : "")}"
            : null;

        var ramString = m.Ram?.Load != null ? $"RAM {m.Ram.Load}%" : null;

        var header = paused ? "PC MQTT Monitor (Paused)" : "PC MQTT Monitor";
        var parts  = new[] { header, cpuString, gpuString, ramString }
            .Where(s => !string.IsNullOrEmpty(s));

        return string.Join(" | ", parts);
    }

    // Used by MainWindow for the window title-bar icon.
    internal static Icon CreateIcon()
        => Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _window.Dispose();
            _normalIcon.Dispose();
            _pausedIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
