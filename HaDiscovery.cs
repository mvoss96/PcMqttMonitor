using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

// Home Assistant MQTT discovery — DEVICE-BASED format (HA 2024.11+):
// one retained JSON on homeassistant/device/<id>/config describes the device
// ("dev"), the publishing app ("o", mandatory) and every sensor as a component
// under "cmps" (each with platform "p": "sensor"). An empty retained payload on
// the same topic removes the whole device from HA.
// Spec: https://www.home-assistant.io/integrations/mqtt/#device-discovery-payload
static class HaDiscovery
{
    static string ConfigTopic(string host) => $"homeassistant/device/pcmqtt_{host}/config";

    // Which components the current snapshot would advertise — when this changes
    // (sensor toggled, drive plugged/removed), the config must be republished.
    // Drive NAMES are included: swapping a drive keeps the component ids
    // (index-based) but changes the entity names.
    public static string Fingerprint(MqttMetrics m) =>
        string.Join(",", BuildComponents(m, "").Keys)
        + "|" + string.Join(",", m.Drives?.Select(d => d.Name) ?? []);

    public static Task PublishConfigAsync(
        IMqttClient client, string topicRoot, string host, MqttMetrics metrics,
        string version, CancellationToken cancellationToken)
    {
        var baseTopic = $"{topicRoot}/{host}";
        var payload = new Dictionary<string, object>
        {
            ["dev"] = new Dictionary<string, object>
            {
                ["ids"] = new[] { $"pcmqtt_{host}" },
                ["name"] = Environment.MachineName,
                ["mf"] = "PC MQTT Monitor",
                ["mdl"] = metrics.Cpu?.Name ?? "PC",
                ["sw"] = version,
            },
            ["o"] = new Dictionary<string, object>
            {
                ["name"] = "PC MQTT Monitor",
                ["sw"] = version,
            },
            // Shared by all components: entities flip to "unavailable" when the
            // availability topic goes "offline" (shutdown, crash via LWT, pause).
            ["availability_topic"] = MqttPublisher.AvailabilityTopic(topicRoot, host),
            ["cmps"] = BuildComponents(metrics, baseTopic),
        };

        return PublishRetainedAsync(client, ConfigTopic(host),
            JsonSerializer.Serialize(payload), cancellationToken);
    }

    // Empty retained payload removes the device (and clears the retained config).
    public static Task RemoveAsync(IMqttClient client, string host, CancellationToken cancellationToken)
        => PublishRetainedAsync(client, ConfigTopic(host), "", cancellationToken);

    static Task PublishRetainedAsync(IMqttClient client, string topic, string payload, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        return client.PublishAsync(message, ct);
    }

    // Mirrors MqttPublisher's subtopic logic: a component is advertised exactly
    // when its scalar subtopic is being published (metric present in snapshot).
    static Dictionary<string, object> BuildComponents(MqttMetrics m, string baseTopic)
    {
        var cmps = new Dictionary<string, object>();

        void Sensor(string id, string name, string subTopic, string? unit, string? deviceClass)
        {
            var c = new Dictionary<string, object>
            {
                ["p"] = "sensor",
                ["name"] = name,
                ["state_topic"] = $"{baseTopic}/{subTopic}",
                ["unique_id"] = $"pcmqtt_{Environment.MachineName.ToLowerInvariant()}_{id}",
                ["state_class"] = "measurement",
            };
            if (unit != null) c["unit_of_measurement"] = unit;
            if (deviceClass != null) c["device_class"] = deviceClass;
            cmps[id] = c;
        }

        if (m.Cpu is { } c2)
        {
            if (c2.Load          != null) Sensor("cpu_load",    "CPU Load",          "cpu/load",    "%",  null);
            if (c2.TempC         != null) Sensor("cpu_temp",    "CPU Temperature",   "cpu/temp",    "°C", "temperature");
            if (c2.PackagePowerW != null) Sensor("cpu_power",   "CPU Package Power", "cpu/power",   "W",  "power");
            if (c2.CoreVoltageV  != null) Sensor("cpu_voltage", "CPU Core Voltage",  "cpu/voltage", "V",  "voltage");
        }

        if (m.Gpu is { } g)
        {
            if (g.Load          != null) Sensor("gpu_load",       "GPU Load",        "gpu/load",       "%",   null);
            if (g.TempC         != null) Sensor("gpu_temp",       "GPU Temperature", "gpu/temp",       "°C",  "temperature");
            if (g.BoardPowerW   != null) Sensor("gpu_power",      "GPU Board Power", "gpu/power",      "W",   "power");
            if (g.FanRpm        != null) Sensor("gpu_fan",        "GPU Fan",         "gpu/fan",        "RPM", null);
            if (g.MemoryUsedMb  != null) Sensor("gpu_vram_used",  "GPU VRAM Used",   "gpu/vram_used",  "MB",  "data_size");
            if (g.MemoryTotalMb != null) Sensor("gpu_vram_total", "GPU VRAM Total",  "gpu/vram_total", "MB",  "data_size");
        }

        if (m.Ram is { } r)
        {
            if (r.Load    != null) Sensor("ram_load",  "RAM Load",  "ram/load",  "%",  null);
            if (r.UsedGb  != null) Sensor("ram_used",  "RAM Used",  "ram/used",  "GB", "data_size");
            if (r.TotalGb != null) Sensor("ram_total", "RAM Total", "ram/total", "GB", "data_size");
        }

        if (m.Drives != null)
        {
            for (int i = 0; i < m.Drives.Count; i++)
            {
                var d = m.Drives[i];
                var p = $"drives/{i}";
                if (d.UsedGb      != null) Sensor($"drive_{i}_used",    $"{d.Name} Used",    $"{p}/used",    "GB", "data_size");
                if (d.FreeGb      != null) Sensor($"drive_{i}_free",    $"{d.Name} Free",    $"{p}/free",    "GB", "data_size");
                if (d.TotalGb     != null) Sensor($"drive_{i}_total",   $"{d.Name} Total",   $"{p}/total",   "GB", "data_size");
                if (d.UsedPercent != null) Sensor($"drive_{i}_percent", $"{d.Name} Used %",  $"{p}/percent", "%",  null);
            }
        }

        if (m.Network is { } n)
        {
            // Values are bytes/1024 per second — that is KiB/s in HA's data_rate units.
            if (n.UploadKbps   != null) Sensor("net_up",   "Network Upload",   "net/up",   "KiB/s", "data_rate");
            if (n.DownloadKbps != null) Sensor("net_down", "Network Download", "net/down", "KiB/s", "data_rate");
        }

        if (m.System is { } s)
        {
            if (s.UptimeSec != null) Sensor("uptime", "Uptime", "system/uptime", "s", "duration");
        }

        return cmps;
    }
}
