using System.IO;
using System.Text.Json;

// Loads and validates runtime configuration from JSON.
static class ConfigLoader
{
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig { Sensors = new SensorConfig() };

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AppConfig();

        config.Sensors ??= new SensorConfig();
        if (config.PublishIntervalSeconds <= 0)
            config.PublishIntervalSeconds = 1.0;

        return config;
    }
}

// Configuration model for broker settings and sensor toggles.
sealed class AppConfig
{
    public string BrokerHost { get; set; } = string.Empty;
    public int BrokerPort { get; set; } = 1883;
    // TLS-encrypted connection to the broker (typically port 8883).
    public bool UseTls { get; set; } = false;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string TopicRoot { get; set; } = "pc";
    public double PublishIntervalSeconds { get; set; } = 1.0;
    public bool DebugEnabled { get; set; } = false;
    // Opt-in: publish a Home Assistant device-discovery config so all sensors
    // appear in HA automatically. Off by default — enabled via the Settings tab.
    public bool HaDiscoveryEnabled { get; set; } = false;
    // Daily check against GitHub releases; shows a tray notification when a
    // newer version exists. Notify-only — never downloads or installs anything.
    public bool UpdateCheckEnabled { get; set; } = true;
    public SensorConfig? Sensors { get; set; }
}

// Feature flags that enable/disable specific metrics.
sealed class SensorConfig
{
    public bool CpuLoad { get; set; } = true;
    public bool CpuTemp { get; set; } = true;
    public bool GpuLoad { get; set; } = true;
    public bool GpuTemp { get; set; } = true;
    public bool RamLoad { get; set; } = true;
    public bool RamUsed { get; set; } = true;
    public bool RamTotal { get; set; } = true;
    public bool CpuPackagePower { get; set; } = true;
    public bool CpuCoreVoltage { get; set; } = true;
    public bool GpuBoardPower { get; set; } = true;
    public bool GpuFanSpeed { get; set; } = true;
    public bool GpuMemoryLoad { get; set; } = true;
    public bool GpuMemoryUsed { get; set; } = true;
    public bool GpuMemoryTotal { get; set; } = true;
    public bool MotherboardName { get; set; } = true;
    public bool Drives { get; set; } = true;
    public bool Uptime { get; set; } = true;
    public bool NetworkUpload { get; set; } = true;
    public bool NetworkDownload { get; set; } = true;
}
