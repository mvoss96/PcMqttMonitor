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

    static readonly int Col0 = Theme.S(16), Col1 = Theme.S(190), RowStep = Theme.S(24);

    int _y = Theme.S(10);
    bool _col1;   // next checkbox goes into the second column

    public SensorsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        BackColor = Theme.WinBg;

        var title = new Label
        {
            Text = L.T.SensorsTitle, Font = Theme.Title, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(Theme.S(16), Theme.S(12))
        };
        Controls.Add(title);

        _card = new CardPanel
        {
            Location = new Point(Theme.S(16), Theme.S(42)),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_card);

        var s = config.Sensors;

        Group(L.T.CardCpu);
        Add(markDirty, L.T.SensorLoad,     s.CpuLoad,         (c, v) => c.CpuLoad = v);
        Add(markDirty, L.T.SensorTemp,     s.CpuTemp,         (c, v) => c.CpuTemp = v);
        Add(markDirty, L.T.SensorPkgPower, s.CpuPackagePower, (c, v) => c.CpuPackagePower = v);
        Add(markDirty, L.T.SensorCoreVolt, s.CpuCoreVoltage,  (c, v) => c.CpuCoreVoltage = v);

        Group(L.T.CardGpu);
        Add(markDirty, L.T.SensorLoad,       s.GpuLoad,        (c, v) => c.GpuLoad = v);
        Add(markDirty, L.T.SensorTemp,       s.GpuTemp,        (c, v) => c.GpuTemp = v);
        Add(markDirty, L.T.SensorBoardPower, s.GpuBoardPower,  (c, v) => c.GpuBoardPower = v);
        Add(markDirty, L.T.SensorFanSpeed,   s.GpuFanSpeed,    (c, v) => c.GpuFanSpeed = v);
        Add(markDirty, L.T.SensorMemLoad,    s.GpuMemoryLoad,  (c, v) => c.GpuMemoryLoad = v);
        Add(markDirty, L.T.SensorMemUsed,    s.GpuMemoryUsed,  (c, v) => c.GpuMemoryUsed = v);
        Add(markDirty, L.T.SensorMemTotal,   s.GpuMemoryTotal, (c, v) => c.GpuMemoryTotal = v);

        Group(L.T.CardRam);
        Add(markDirty, L.T.SensorLoad,  s.RamLoad,  (c, v) => c.RamLoad = v);
        Add(markDirty, L.T.SensorUsed,  s.RamUsed,  (c, v) => c.RamUsed = v);
        Add(markDirty, L.T.SensorTotal, s.RamTotal, (c, v) => c.RamTotal = v);

        Group(L.T.CardNetwork);
        Add(markDirty, L.T.SensorUpload,   s.NetworkUpload,   (c, v) => c.NetworkUpload = v);
        Add(markDirty, L.T.SensorDownload, s.NetworkDownload, (c, v) => c.NetworkDownload = v);

        Group(L.T.GroupOther);
        Add(markDirty, L.T.SensorBoard,  s.MotherboardName, (c, v) => c.MotherboardName = v);
        Add(markDirty, L.T.SensorDrives, s.Drives,          (c, v) => c.Drives = v);
        Add(markDirty, L.T.SensorUptime, s.Uptime,          (c, v) => c.Uptime = v);

        if (_col1) _y += RowStep;
        _card.Height = _y + Theme.S(10);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        _card.Width = Width - Theme.S(32);
        base.OnResize(eventargs);
    }

    void Group(string name)
    {
        if (_col1) { _y += RowStep; _col1 = false; }
        var header = new Label
        {
            Text = name, Font = Theme.GroupHead, ForeColor = Theme.Fg,
            AutoSize = true, Location = new Point(Col0, _y + Theme.S(8))
        };
        var rule = new Panel
        {
            Height = 1, BackColor = Theme.CardBorder,
            Bounds = new Rectangle(Col0 + Theme.S(40), _y + Theme.S(16), _card.Width - Col0 - Theme.S(56), 1),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        _card.Controls.Add(header);
        _card.Controls.Add(rule);
        // the rule starts after the actual text width once the label measured itself
        rule.Left = header.Right + Theme.S(8);
        rule.Width = 0;   // set on first card resize below
        _card.Resize += (_, _) => { rule.Left = header.Right + Theme.S(8); rule.Width = _card.Width - rule.Left - Theme.S(16); };
        _y += Theme.S(28);
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
