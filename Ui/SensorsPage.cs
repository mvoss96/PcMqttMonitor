using System.Drawing;
using System.Windows.Forms;

// The Sensors page: which metrics get published, grouped by hardware, as a
// two-column checkbox grid inside one card. Unchecked values are not read
// from LHM and not published.
sealed class SensorsPage : Panel
{
    readonly List<(FlatCheck Box, Action<SensorConfig, bool> Setter)> _boxes = new();
    readonly AppConfig _config;
    readonly CardPanel _card;

    const int Col0 = 16, Col1 = 190, RowStep = 24;

    int _y = 10;
    bool _col1;   // next checkbox goes into the second column

    public SensorsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        BackColor = Theme.WinBg;

        var title = new Label
        {
            Text = "Sensors to publish", Font = Theme.Title, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(16, 12)
        };
        Controls.Add(title);

        _card = new CardPanel
        {
            Location = new Point(16, 42),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_card);

        var s = config.Sensors;

        Group("CPU");
        Add(markDirty, "Load",          s.CpuLoad,         (c, v) => c.CpuLoad = v);
        Add(markDirty, "Temperature",   s.CpuTemp,         (c, v) => c.CpuTemp = v);
        Add(markDirty, "Package Power", s.CpuPackagePower, (c, v) => c.CpuPackagePower = v);
        Add(markDirty, "Core Voltage",  s.CpuCoreVoltage,  (c, v) => c.CpuCoreVoltage = v);

        Group("GPU");
        Add(markDirty, "Load",         s.GpuLoad,        (c, v) => c.GpuLoad = v);
        Add(markDirty, "Temperature",  s.GpuTemp,        (c, v) => c.GpuTemp = v);
        Add(markDirty, "Board Power",  s.GpuBoardPower,  (c, v) => c.GpuBoardPower = v);
        Add(markDirty, "Fan Speed",    s.GpuFanSpeed,    (c, v) => c.GpuFanSpeed = v);
        Add(markDirty, "Memory Load",  s.GpuMemoryLoad,  (c, v) => c.GpuMemoryLoad = v);
        Add(markDirty, "Memory Used",  s.GpuMemoryUsed,  (c, v) => c.GpuMemoryUsed = v);
        Add(markDirty, "Memory Total", s.GpuMemoryTotal, (c, v) => c.GpuMemoryTotal = v);

        Group("RAM");
        Add(markDirty, "Load",  s.RamLoad,  (c, v) => c.RamLoad = v);
        Add(markDirty, "Used",  s.RamUsed,  (c, v) => c.RamUsed = v);
        Add(markDirty, "Total", s.RamTotal, (c, v) => c.RamTotal = v);

        Group("Network");
        Add(markDirty, "Upload",   s.NetworkUpload,   (c, v) => c.NetworkUpload = v);
        Add(markDirty, "Download", s.NetworkDownload, (c, v) => c.NetworkDownload = v);

        Group("Other");
        Add(markDirty, "Motherboard", s.MotherboardName, (c, v) => c.MotherboardName = v);
        Add(markDirty, "Drives",      s.Drives,          (c, v) => c.Drives = v);
        Add(markDirty, "Uptime",      s.Uptime,          (c, v) => c.Uptime = v);

        if (_col1) _y += RowStep;
        _card.Height = _y + 10;
    }

    protected override void OnResize(EventArgs eventargs)
    {
        _card.Width = Width - 32;
        base.OnResize(eventargs);
    }

    void Group(string name)
    {
        if (_col1) { _y += RowStep; _col1 = false; }
        var header = new Label
        {
            Text = name, Font = new Font("Segoe UI Semibold", 8f), ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(Col0, _y + 8)
        };
        var rule = new Panel
        {
            Height = 1, BackColor = Theme.CardBorder,
            Bounds = new Rectangle(Col0 + 40, _y + 16, _card.Width - Col0 - 56, 1),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _card.Controls.Add(header);
        _card.Controls.Add(rule);
        // the rule starts after the actual text width once the label measured itself
        rule.Left = header.Right + 8;
        rule.Width = 0;   // set on first card resize below
        _card.Resize += (_, _) => { rule.Left = header.Right + 8; rule.Width = _card.Width - rule.Left - 16; };
        _y += 28;
    }

    void Add(Action markDirty, string label, bool value, Action<SensorConfig, bool> setter)
    {
        var box = new FlatCheck
        {
            Text = label,
            Location = new Point(_col1 ? Col1 : Col0, _y)
        };
        box.Checked = value;
        box.CheckedChanged += (_, _) => markDirty();
        _card.Controls.Add(box);
        _boxes.Add((box, setter));
        if (_col1) _y += RowStep;
        _col1 = !_col1;
    }

    public void Apply()
    {
        foreach (var (box, setter) in _boxes)
            setter(_config.Sensors, box.Checked);
    }
}
