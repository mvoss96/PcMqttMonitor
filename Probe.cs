using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

// Hidden diagnostic mode:
//   PcMqttMonitor.exe --probe <file>
// Writes everything LibreHardwareMonitor detects (hardware, sensors, values)
// and the app's own snapshot to <file>, then exits. No UI, no sinks, no
// config changes. Used to see what a machine provides, e.g. Windows on ARM
// in CI. Exceptions are written to the file instead of ending the run.
static class Probe
{
    public static bool TryRun(string[] args)
    {
        int idx = Array.IndexOf(args, "--probe");
        if (idx < 0 || idx + 1 >= args.Length) return false;
        var report = new StringBuilder();
        report.AppendLine($"PC MQTT Monitor {UpdateChecker.CurrentVersion.ToString(3)}");
        report.AppendLine($"OS: {Environment.OSVersion}, process {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
                          $"OS arch {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, admin {IsAdmin()}");

        Section(report, "LibreHardwareMonitor", () =>
        {
            var computer = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true,
                IsMotherboardEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true,
                // No controllers: LHM's Heatmaster detection opens CP210x
                // COM ports (also used by ESP32 boards) and writes to them.
                IsBatteryEnabled = true,
            };
            computer.Open();
            try
            {
                foreach (var hw in computer.Hardware) Dump(report, hw, 0);
            }
            finally { computer.Close(); }
        });

        Section(report, "App snapshot (second read, as published)", () =>
        {
            using var sensors = new SensorService(new SensorConfig(), buildSummary: false);
            sensors.Open();
            report.AppendLine($"Degraded: {sensors.Degraded ?? "no"}");
            sensors.ReadSnapshot();
            Thread.Sleep(1000);
            var snapshot = sensors.ReadSnapshot();
            report.AppendLine(JsonSerializer.Serialize(snapshot.Metrics,
                new JsonSerializerOptions(MetricsJson.Options) { WriteIndented = true }));
        });

        File.WriteAllText(args[idx + 1], report.ToString());
        return true;
    }

    static void Section(StringBuilder report, string title, Action body)
    {
        report.AppendLine().AppendLine($"== {title}");
        try { body(); }
        catch (Exception ex) { report.AppendLine($"FAILED: {ex}"); }
    }

    static void Dump(StringBuilder report, IHardware hw, int depth)
    {
        hw.Update();
        string pad = new(' ', depth * 2);
        report.AppendLine($"{pad}[{hw.HardwareType}] {hw.Name}");
        foreach (var s in hw.Sensors)
            report.AppendLine($"{pad}  {s.SensorType,-12} {s.Name} = {s.Value?.ToString() ?? "null"}");
        foreach (var sub in hw.SubHardware) Dump(report, sub, depth + 1);
    }

    static bool IsAdmin() =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
