using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

// The Sensors page: which metrics get published, grouped by hardware, as a
// two-column check box grid inside one card. Unchecked values are not read
// from LHM and not published. The fan group lists every detected fan channel;
// detection finishes after the window is built, so the card is rebuilt the
// first time the page is shown with channels available.
sealed class SensorsPage : UserControl
{
    readonly AppConfig _config;
    readonly Action _markDirty;
    readonly StackPanel _card = new() { Margin = new Thickness(16, 8, 16, 12) };
    readonly List<(CheckBox Box, Action<SensorConfig, bool> Setter)> _boxes = new();
    readonly Dictionary<CheckBox, bool> _saved = new();
    readonly Dictionary<string, CheckBox> _fanBoxes = new();
    UniformGrid? _group;
    bool _fansListed;

    public SensorsPage(AppConfig config, Action markDirty)
    {
        _config = config;
        _markDirty = markDirty;

        var stack = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
        stack.Children.Add(new TextBlock { Text = L.T.SensorsTitle, Style = (Style)Application.Current.FindResource("PageTitle") });
        stack.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Card"), Child = _card });
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Build();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && !_fansListed && SensorService.DetectedFans.Count > 0)
                Build();
        };
    }

    void Build()
    {
        _card.Children.Clear();
        _boxes.Clear();
        _saved.Clear();
        _fanBoxes.Clear();
        var s = _config.Sensors;

        Group(L.T.CardCpu);
        Add(L.T.SensorLoad,     s.CpuLoad,         (c, v) => c.CpuLoad = v);
        Add(L.T.SensorTemp,     s.CpuTemp,         (c, v) => c.CpuTemp = v);
        Add(L.T.SensorPkgPower, s.CpuPackagePower, (c, v) => c.CpuPackagePower = v);
        Add(L.T.SensorCoreVolt, s.CpuCoreVoltage,  (c, v) => c.CpuCoreVoltage = v);

        Group(L.T.CardGpu);
        Add(L.T.SensorLoad,       s.GpuLoad,        (c, v) => c.GpuLoad = v);
        Add(L.T.SensorTemp,       s.GpuTemp,        (c, v) => c.GpuTemp = v);
        Add(L.T.SensorBoardPower, s.GpuBoardPower,  (c, v) => c.GpuBoardPower = v);
        Add(L.T.SensorMemLoad,    s.GpuMemoryLoad,  (c, v) => c.GpuMemoryLoad = v);
        Add(L.T.SensorMemUsed,    s.GpuMemoryUsed,  (c, v) => c.GpuMemoryUsed = v);
        Add(L.T.SensorMemTotal,   s.GpuMemoryTotal, (c, v) => c.GpuMemoryTotal = v);

        Group(L.T.CardRam);
        Add(L.T.SensorLoad,  s.RamLoad,  (c, v) => c.RamLoad = v);
        Add(L.T.SensorUsed,  s.RamUsed,  (c, v) => c.RamUsed = v);
        Add(L.T.SensorTotal, s.RamTotal, (c, v) => c.RamTotal = v);

        Group(L.T.CardNetwork);
        Add(L.T.SensorUpload,   s.NetworkUpload,   (c, v) => c.NetworkUpload = v);
        Add(L.T.SensorDownload, s.NetworkDownload, (c, v) => c.NetworkDownload = v);

        // One box per detected channel: "Fan #2 · 861 RPM", kept live by
        // RefreshFanRpm. Enabled fans are published even at 0 RPM.
        var fans = SensorService.DetectedFans;
        _fansListed = fans.Count > 0;
        if (_fansListed)
        {
            Group(L.T.CardFans);
            foreach (var fan in fans)
            {
                var id = fan.Id;
                _fanBoxes[id] = Add(FanLabel(fan), _config.Sensors.FanChannels.GetValueOrDefault(id),
                    (c, v) => c.FanChannels[id] = v);
            }
        }

        Group(L.T.GroupOther);
        Add(L.T.SensorBoard,  s.MotherboardName, (c, v) => c.MotherboardName = v);
        Add(L.T.SensorDrives, s.Drives,          (c, v) => c.Drives = v);
        Add(L.T.SensorUptime, s.Uptime,          (c, v) => c.Uptime = v);
    }

    // Group heading with a rule to its right, then a two-column grid.
    void Group(string name)
    {
        var head = new DockPanel { Margin = new Thickness(0, _card.Children.Count == 0 ? 0 : 10, 0, 2) };
        var text = new TextBlock { Text = name, Style = (Style)Application.Current.FindResource("CardTitle"), Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(text, Dock.Left);
        head.Children.Add(text);
        head.Children.Add(new Border { Style = (Style)Application.Current.FindResource("RowSep"), VerticalAlignment = VerticalAlignment.Center });
        _card.Children.Add(head);
        _group = new UniformGrid { Columns = 2 };
        _card.Children.Add(_group);
    }

    CheckBox Add(string label, bool value, Action<SensorConfig, bool> setter)
    {
        var box = new CheckBox { Content = label, IsChecked = value, MinHeight = 28, Padding = new Thickness(6, 0, 0, 0) };
        box.Checked += (_, _) => _markDirty();
        box.Unchecked += (_, _) => _markDirty();
        _group!.Children.Add(box);
        _boxes.Add((box, setter));
        _saved[box] = value;
        return box;
    }

    static string FanLabel(SensorService.DetectedFan fan) => $"{fan.Name} · {fan.Rpm:0} RPM";

    // Called on every snapshot while this page is visible.
    public void RefreshFanRpm()
    {
        foreach (var fan in SensorService.DetectedFans)
            if (_fanBoxes.TryGetValue(fan.Id, out var box))
                box.Content = FanLabel(fan);
    }

    // The page is built from the saved config, so rebuilding discards edits.
    public void Revert() => Build();

    public bool HasChanges => _boxes.Any(b => b.Box.IsChecked != _saved[b.Box]);

    public void Apply()
    {
        foreach (var (box, setter) in _boxes)
        {
            setter(_config.Sensors, box.IsChecked == true);
            _saved[box] = box.IsChecked == true;
        }
    }
}
