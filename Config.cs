using System.IO;
using System.Text.Json;

// Loads runtime configuration from JSON (config.json next to the exe).
// v2 schema in sections — old flat (v1.x) configs simply deserialize to
// defaults; there is deliberately no migration code.
static class ConfigLoader
{
    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return new AppConfig();

        AppConfig config;
        try
        {
            config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            AppLog.Write($"[error] config.json unreadable, using defaults: {ex.Message}");
            return new AppConfig();
        }

        // Sections may be null when absent from the file (or when reading a v1 config).
        config.General ??= new GeneralConfig();
        config.Mqtt    ??= new MqttConfig();
        config.Udp     ??= new UdpConfig();
        config.Tcp     ??= new TcpConfig();
        config.Serial  ??= new SerialConfig();
        config.Sensors ??= new SensorConfig();
        if (config.General.PublishIntervalSeconds <= 0)
            config.General.PublishIntervalSeconds = 1.0;

        return config;
    }

    // The one place that writes config.json — the UI must not serialize itself.
    public static void Save(string path, AppConfig config)
        => File.WriteAllText(path,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
}

sealed class AppConfig
{
    public GeneralConfig General { get; set; } = new();
    public MqttConfig Mqtt { get; set; } = new();
    public UdpConfig Udp { get; set; } = new();
    public TcpConfig Tcp { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public SensorConfig Sensors { get; set; } = new();
}

sealed class GeneralConfig
{
    public double PublishIntervalSeconds { get; set; } = 1.0;
    public bool DebugEnabled { get; set; } = false;
    // UI color mode: "system" (follow Windows), "light" or "dark".
    // Applied at startup; changing it in Settings restarts the app.
    public string Theme { get; set; } = "system";
    // UI language: "system" (follow Windows), "en" or "de". Restart to apply.
    public string Language { get; set; } = "system";
    // Daily check against GitHub releases; shows a tray notification when a
    // newer version exists. Notify-only — never downloads or installs anything.
    public bool UpdateCheckEnabled { get; set; } = true;
}

sealed class MqttConfig
{
    // The sink is only created when Enabled AND Host is set.
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
    // TLS-encrypted connection to the broker (typically port 8883).
    public bool UseTls { get; set; } = false;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string TopicRoot { get; set; } = "pc";
    // Opt-in: publish a Home Assistant device-discovery config so all sensors
    // appear in HA automatically. Off by default — enabled via the Settings tab.
    public bool HaDiscoveryEnabled { get; set; } = false;
}

// Sends each snapshot as one JSON datagram to Host:Port.
sealed class UdpConfig
{
    public bool Enabled { get; set; } = false;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 5555;
}

// Listens on ListenPort and streams line-delimited JSON to every connected client.
sealed class TcpConfig
{
    public bool Enabled { get; set; } = false;
    public int ListenPort { get; set; } = 5556;
}

// Writes line-delimited JSON to a serial port (e.g. an ESP32 status display).
sealed class SerialConfig
{
    public bool Enabled { get; set; } = false;
    public string Port { get; set; } = string.Empty;   // e.g. "COM3"
    public int Baud { get; set; } = 115200;
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
    public bool GpuMemoryLoad { get; set; } = true;
    public bool GpuMemoryUsed { get; set; } = true;
    public bool GpuMemoryTotal { get; set; } = true;
    public bool MotherboardName { get; set; } = true;
    // Per-channel fan selection, keyed by the stable sensor-derived id
    // ("fan_2", "gpu_fan_1"). Channels detected at startup that are missing
    // here get a default: enabled when the fan was spinning. Enabled channels
    // are always published, even at 0 RPM (GPU zero-RPM mode stays visible).
    public Dictionary<string, bool> FanChannels { get; set; } = new();
    public bool Drives { get; set; } = true;
    public bool Uptime { get; set; } = true;
    public bool NetworkUpload { get; set; } = true;
    public bool NetworkDownload { get; set; } = true;
}
