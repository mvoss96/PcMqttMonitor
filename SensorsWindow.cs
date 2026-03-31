using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

// Shows all current sensor readings in a simple window.
sealed class SensorsWindow : Form
{
    readonly TextBox _text;

    public SensorsWindow()
    {
        Text = "PC MQTT Monitor — Sensors";
        Size = new Size(360, 480);
        MinimumSize = new Size(300, 300);
        ShowInTaskbar = true;

        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            Font = new Font("Consolas", 9f),
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window
        };
        Controls.Add(_text);
    }

    public void UpdateMetrics(MqttMetrics m)
    {
        var sb = new StringBuilder();

        if (m.Cpu != null)
        {
            sb.AppendLine($"CPU: {m.Cpu.Name}");
            AddRow(sb, "Load",          Format(m.Cpu.Load, "%"));
            AddRow(sb, "Temperature",   Format(m.Cpu.TempC, "°C", "0.#"));
            AddRow(sb, "Package Power", Format(m.Cpu.PackagePowerW, "W", "0.#"));
            AddRow(sb, "Core Voltage",  Format(m.Cpu.CoreVoltageV, "V", "0.###"));
            sb.AppendLine();
        }

        if (m.Gpu != null)
        {
            sb.AppendLine($"GPU: {m.Gpu.Name}");
            AddRow(sb, "Load",        Format(m.Gpu.Load, "%"));
            AddRow(sb, "Temperature", Format(m.Gpu.TempC, "°C", "0.#"));
            AddRow(sb, "Board Power", Format(m.Gpu.BoardPowerW, "W", "0.#"));
            AddRow(sb, "Fan",         Format(m.Gpu.FanRpm, "RPM", "0"));
            AddRow(sb, "VRAM Load",   Format(m.Gpu.MemoryLoad, "%"));
            AddRow(sb, "VRAM Used",   Format(m.Gpu.MemoryUsedMb, "MB", "0"));
            AddRow(sb, "VRAM Total",  Format(m.Gpu.MemoryTotalMb, "MB", "0"));
            sb.AppendLine();
        }

        if (m.Ram != null)
        {
            sb.AppendLine("RAM");
            AddRow(sb, "Load",  Format(m.Ram.Load, "%"));
            AddRow(sb, "Used",  Format(m.Ram.UsedGb, "GB", "0.#"));
            AddRow(sb, "Total", Format(m.Ram.TotalGb, "GB", "0.#"));
            sb.AppendLine();
        }

        if (m.Motherboard != null)
        {
            sb.AppendLine($"Motherboard: {m.Motherboard.Name}");
            sb.AppendLine();
        }

        if (m.Drives != null)
        {
            sb.AppendLine("Drives");
            foreach (var d in m.Drives)
            {
                sb.AppendLine($"  {d.Name}");
                AddRow(sb, "  Used",  Format(d.UsedGb, "GB", "0.#"), indent: 4);
                AddRow(sb, "  Free",  Format(d.FreeGb, "GB", "0.#"), indent: 4);
                AddRow(sb, "  Total", Format(d.TotalGb, "GB", "0.#"), indent: 4);
            }
        }

        _text.Text = sb.ToString();
    }

    // Adds a "  Label:    Value" line, skipping null values.
    static void AddRow(StringBuilder sb, string label, string? value, int indent = 2)
    {
        if (value == null) return;
        var padding = new string(' ', indent);
        sb.AppendLine($"{padding}{label,-16}{value}");
    }

    // Returns null for null values so AddRow can skip them.
    static string? Format(int? value, string unit)
        => value == null ? null : $"{value} {unit}";

    static string? Format(float? value, string unit, string fmt = "0.##")
        => value == null ? null : $"{value.Value.ToString(fmt, CultureInfo.InvariantCulture)} {unit}";

    // Closing the window just hides it so it can be reopened from the tray.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
