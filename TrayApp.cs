using System.Drawing;
using System.Windows.Forms;

// Manages the system tray icon, native hover tooltip, and the sensors window.
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly SensorsWindow _sensorsWindow;
    MqttMetrics? _lastMetrics;
    volatile string _statusText = "Starting...";

    public TrayApp(CancellationTokenSource shutdown)
    {
        _sensorsWindow = new SensorsWindow();

        _tray = new NotifyIcon
        {
            Icon = CreateIcon(),
            Visible = true,
            Text = "PC MQTT Monitor"
        };

        // Left-click opens the sensors window.
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowSensorsWindow();
        };

        // Status item at the top — disabled so it acts as a label, not a button.
        var statusItem = new ToolStripMenuItem(_statusText) { Enabled = false };

        var menu = new ContextMenuStrip();
        // Refresh the status text just before the menu is shown (avoids cross-thread invoke).
        menu.Opening += (_, _) => statusItem.Text = _statusText;
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Sensors", null, (_, _) => ShowSensorsWindow());
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
        _lastMetrics = snapshot.Metrics;

        // Update the native hover tooltip with a short summary.
        var tooltip = BuildTooltipText(snapshot.Metrics);
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        // Update the sensors window if it is currently open.
        if (_sensorsWindow.Visible && _sensorsWindow.IsHandleCreated)
            _sensorsWindow.BeginInvoke(() => _sensorsWindow.UpdateMetrics(snapshot.Metrics));
    }

    void ShowSensorsWindow()
    {
        // Show the latest data immediately when opening the window.
        if (_lastMetrics != null)
            _sensorsWindow.UpdateMetrics(_lastMetrics);

        _sensorsWindow.Show();
        _sensorsWindow.BringToFront();
    }

    // Short single-line summary for the native OS tooltip.
    static string BuildTooltipText(MqttMetrics m)
    {
        var parts = new List<string> { "PC MQTT Monitor" };
        if (m.Cpu?.Load != null) parts.Add($"CPU {m.Cpu.Load}%");
        if (m.Cpu?.TempC != null) parts.Add($"{m.Cpu.TempC:0}C");
        if (m.Gpu?.Load != null) parts.Add($"GPU {m.Gpu.Load}%");
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
            _sensorsWindow.Dispose();
        }
        base.Dispose(disposing);
    }
}
