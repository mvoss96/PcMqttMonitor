using System.Text.Json;
using System.Text.Json.Serialization;

// Shared host identity — used in topics, client ids and HA unique_ids.
static class HostInfo
{
    public static readonly string Id = Environment.MachineName.ToLowerInvariant();
}

// The one JSON shape used by every stream output: the MQTT status topic and
// the UDP/TCP sinks all serialize MetricsSnapshot with these options.
static class MetricsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] SerializeToUtf8Bytes(MetricsSnapshot m)
        => JsonSerializer.SerializeToUtf8Bytes(m, Options);
}

// One row per scalar metric — drives the MQTT subtopics and the HA discovery
// components from a single place, so adding a metric means adding one line here
// (plus reading it in SensorService). Drives are handled separately: their count
// is dynamic and they carry a string payload, forcing them into this table would
// complicate it for everything else.
sealed record MetricDef(
    string Id,           // unique key, also the HA component id suffix
    string Name,         // human-readable name (HA entity name)
    string SubTopic,     // published under <topicRoot>/<host>/<SubTopic>
    string? Unit,        // HA unit_of_measurement
    string? DeviceClass, // HA device_class
    string Format,       // numeric format of the plain-text payload
    Func<MetricsSnapshot, float?> Get);

static class MetricTable
{
    public static readonly MetricDef[] All =
    [
        new("cpu_load",       "CPU Load",          "cpu/load",       "%",     null,          "0",     m => m.Cpu?.Load),
        new("cpu_temp",       "CPU Temperature",   "cpu/temp",       "°C",    "temperature", "0.#",   m => m.Cpu?.TempC),
        new("cpu_power",      "CPU Package Power", "cpu/power",      "W",     "power",       "0.#",   m => m.Cpu?.PackagePowerW),
        // Voltage needs 3 decimals — "0.#" would turn 1.225 V into "1.2".
        new("cpu_voltage",    "CPU Core Voltage",  "cpu/voltage",    "V",     "voltage",     "0.###", m => m.Cpu?.CoreVoltageV),
        new("gpu_load",       "GPU Load",          "gpu/load",       "%",     null,          "0",     m => m.Gpu?.Load),
        new("gpu_temp",       "GPU Temperature",   "gpu/temp",       "°C",    "temperature", "0.#",   m => m.Gpu?.TempC),
        new("gpu_power",      "GPU Board Power",   "gpu/power",      "W",     "power",       "0.#",   m => m.Gpu?.BoardPowerW),
        new("gpu_fan",        "GPU Fan",           "gpu/fan",        "RPM",   null,          "0.#",   m => m.Gpu?.FanRpm),
        new("gpu_vram_used",  "GPU VRAM Used",     "gpu/vram_used",  "MB",    "data_size",   "0.#",   m => m.Gpu?.MemoryUsedMb),
        new("gpu_vram_total", "GPU VRAM Total",    "gpu/vram_total", "MB",    "data_size",   "0.#",   m => m.Gpu?.MemoryTotalMb),
        new("ram_load",       "RAM Load",          "ram/load",       "%",     null,          "0",     m => m.Ram?.Load),
        new("ram_used",       "RAM Used",          "ram/used",       "GB",    "data_size",   "0.#",   m => m.Ram?.UsedGb),
        new("ram_total",      "RAM Total",         "ram/total",      "GB",    "data_size",   "0.#",   m => m.Ram?.TotalGb),
        // Network is per-adapter (dynamic count) and special-cased like drives.
        new("uptime",         "Uptime",            "system/uptime",  "s",     "duration",    "0",     m => m.System?.UptimeSec),
    ];
}

sealed record SensorSnapshot(string Summary, MetricsSnapshot Metrics);

// Transport-neutral metrics model — serialized 1:1 as the JSON payload of the
// MQTT status topic and the UDP/TCP streams; also feeds the tray/window UI.
sealed class MetricsSnapshot
{
    public DateTime TimestampUtc { get; set; }
    public string Host { get; set; } = string.Empty;
    public CpuMetrics? Cpu { get; set; }
    public GpuMetrics? Gpu { get; set; }
    public RamMetrics? Ram { get; set; }
    public MotherboardMetrics? Motherboard { get; set; }
    public List<StorageMetrics>? Drives { get; set; }
    public List<NetworkAdapterMetrics>? Network { get; set; }
    public SystemMetrics? System { get; set; }
}

sealed class SystemMetrics
{
    public int? UptimeSec { get; set; }
    // e.g. "Windows 11 Pro 24H2" — static, read once at startup.
    public string? OsVersion { get; set; }
}

sealed class CpuMetrics
{
    public string Name { get; set; } = string.Empty;
    public int? Load { get; set; }
    public float? TempC { get; set; }
    public float? PackagePowerW { get; set; }
    public float? CoreVoltageV { get; set; }
}

sealed class GpuMetrics
{
    public string Name { get; set; } = string.Empty;
    public int? Load { get; set; }
    public float? TempC { get; set; }
    public float? BoardPowerW { get; set; }
    public float? FanRpm { get; set; }
    public int? MemoryLoad { get; set; }
    public float? MemoryUsedMb { get; set; }
    public float? MemoryTotalMb { get; set; }
}

sealed class RamMetrics
{
    public int? Load { get; set; }
    public float? UsedGb { get; set; }
    public float? TotalGb { get; set; }
    // Static module info from WMI, read once at startup: "DDR5" and the
    // configured transfer rate (the "6000" in DDR5-6000). JSON-only, like the
    // CPU/GPU names — no scalar subtopic.
    public string? Type { get; set; }
    public int? SpeedMtps { get; set; }
}

sealed class MotherboardMetrics
{
    public string Name { get; set; } = string.Empty;
}

sealed class StorageMetrics
{
    public string Name { get; set; } = string.Empty;
    public float? UsedGb { get; set; }
    public float? FreeGb { get; set; }
    public float? TotalGb { get; set; }
    public int? UsedPercent { get; set; }
}

// One entry per active physical adapter (Ethernet/WiFi with a default
// gateway — virtual adapters like VMware/WSL/Bluetooth are filtered out).
sealed class NetworkAdapterMetrics
{
    public string Name { get; set; } = string.Empty;   // e.g. "Ethernet", "WLAN"
    public float? UploadKbps { get; set; }
    public float? DownloadKbps { get; set; }
    public string? IpAddress { get; set; }
    public string? Mac { get; set; }
}
