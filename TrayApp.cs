using System.Drawing;
using System.Windows.Forms;

// Manages the system tray icon, native hover tooltip, and the main window.
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly MainWindow _window;
    volatile string _statusText = "Starting...";

    public TrayApp(CancellationTokenSource shutdown, string configPath, AppConfig config)
    {
        _window = new MainWindow(configPath, config);

        _tray = new NotifyIcon
        {
            Icon = CreateIcon(),
            Visible = true,
            Text = "PC MQTT Monitor"
        };

        // Left-click opens the sensors tab.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _window.ShowSensorsTab();
        };

        var statusItem = new ToolStripMenuItem(_statusText) { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => statusItem.Text = _statusText;
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Sensors", null, (_, _) => _window.ShowSensorsTab());
        menu.Items.Add("Settings",     null, (_, _) => _window.ShowSettingsTab());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            shutdown.Cancel();
            _tray.Visible = false;
            Application.Exit();
        });
        _tray.ContextMenuStrip = menu;
    }

    // Called from any thread to update the status shown in the context menu.
    public void SetStatus(string status) => _statusText = status;

    // Called from the MQTT loop thread with the latest sensor snapshot.
    public void UpdateSnapshot(SensorSnapshot snapshot)
    {
        // Update the native hover tooltip with a short summary.
        var tooltip = BuildTooltipText(snapshot.Metrics);
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        _window.SetLatestMetrics(snapshot.Metrics);
    }

    // Short single-line summary for the native OS tooltip.
    static string BuildTooltipText(MqttMetrics m)
    {
        var parts = new List<string> { "PC MQTT Monitor" };
        var cpu = string.Join(" ", new[] { m.Cpu?.Load != null ? $"CPU {m.Cpu.Load}%" : null, m.Cpu?.TempC != null ? $"{m.Cpu.TempC:0}C" : null }.Where(s => s != null));
        if (cpu.Length > 0) parts.Add(cpu);
        var gpu = string.Join(" ", new[] { m.Gpu?.Load != null ? $"GPU {m.Gpu.Load}%" : null, m.Gpu?.TempC != null ? $"{m.Gpu.TempC:0}C" : null }.Where(s => s != null));
        if (gpu.Length > 0) parts.Add(gpu);
        if (m.Ram?.Load != null) parts.Add($"RAM {m.Ram.Load}%");
        return string.Join(" | ", parts);
    }

    static Icon CreateIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.FillEllipse(Brushes.LimeGreen, 1, 1, 14, 14);
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _window.Dispose();
        }
        base.Dispose(disposing);
    }
}
