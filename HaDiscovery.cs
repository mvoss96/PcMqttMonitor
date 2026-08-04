using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    public static string Fingerprint(MetricsSnapshot m) =>
        string.Join(",", MetricTable.All.Where(d => d.Get(m) != null).Select(d => d.Id))
        + "|" + string.Join(",", m.Drives?.Select(d => d.Name) ?? []);

    public static Task PublishConfigAsync(
        IMqttClient client, string topicRoot, string host, MetricsSnapshot metrics,
        string version, string brokerHost, int brokerPort,
        CancellationToken cancellationToken)
    {
        var baseTopic = $"{topicRoot}/{host}";
        var dev = new Dictionary<string, object>
        {
            ["ids"] = new[] { $"pcmqtt_{host}" },
            ["name"] = Environment.MachineName,
            ["mf"] = "PC MQTT Monitor",
            ["mdl"] = metrics.Cpu?.Name ?? "PC",
            ["sw"] = version,
        };
        // "cns" (connections) with the active NIC's MAC lets HA merge this device
        // with entries other integrations register under the same MAC (router
        // presence tracker, Wake-on-LAN) — one device page instead of several.
        if (PrimaryMac(brokerHost, brokerPort) is { } mac)
            dev["cns"] = new[] { new[] { "mac", mac } };

        var payload = new Dictionary<string, object>
        {
            ["dev"] = dev,
            ["o"] = new Dictionary<string, object>
            {
                ["name"] = "PC MQTT Monitor",
                ["sw"] = version,
            },
            // Shared by all components: entities flip to "unavailable" when the
            // availability topic goes "offline" (shutdown, crash via LWT, pause).
            ["availability_topic"] = MqttSink.AvailabilityTopic(topicRoot, host),
            ["cmps"] = BuildComponents(metrics, baseTopic),
        };

        return PublishRetainedAsync(client, ConfigTopic(host),
            JsonSerializer.Serialize(payload), cancellationToken);
    }

    // MAC of the NIC that carries the broker connection, "aa:bb:cc:dd:ee:ff"
    // (HA's registry format). Connecting a UDP socket sends no packets but makes
    // the OS resolve the outgoing route, so the local address identifies the
    // interface actually in use — on a machine with LAN + WLAN this picks the
    // active one. Null if detection fails (payload then simply omits "cns").
    static string? PrimaryMac(string brokerHost, int brokerPort)
    {
        try
        {
            // Explicitly IPv4: the dual-stack default reports the local address as
            // IPv6-mapped ("::ffff:192.168.x.x"), which never Equals the adapters'
            // IPv4 unicast addresses below.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(brokerHost, brokerPort);
            var localAddress = ((IPEndPoint)socket.LocalEndPoint!).Address;
            var mac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                .FirstOrDefault(nic => nic.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.Equals(localAddress)))
                ?.GetPhysicalAddress().GetAddressBytes();
            return mac is { Length: 6 }
                ? string.Join(":", mac.Select(b => b.ToString("x2")))
                : null;
        }
        catch
        {
            return null;
        }
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

    // Mirrors MqttSink's subtopic publishing via the shared MetricTable: a
    // component is advertised exactly when its scalar subtopic is being
    // published (metric present in the snapshot).
    static Dictionary<string, object> BuildComponents(MetricsSnapshot m, string baseTopic)
    {
        var cmps = new Dictionary<string, object>();

        void Sensor(string id, string name, string subTopic, string? unit, string? deviceClass)
        {
            var c = new Dictionary<string, object>
            {
                ["p"] = "sensor",
                ["name"] = name,
                ["state_topic"] = $"{baseTopic}/{subTopic}",
                ["unique_id"] = $"pcmqtt_{HostInfo.Id}_{id}",
                ["state_class"] = "measurement",
            };
            if (unit != null) c["unit_of_measurement"] = unit;
            if (deviceClass != null) c["device_class"] = deviceClass;
            cmps[id] = c;
        }

        foreach (var def in MetricTable.All)
            if (def.Get(m) != null)
                Sensor(def.Id, def.Name, def.SubTopic, def.Unit, def.DeviceClass);

        // Drives: dynamic count — mirrors MqttSink's drives special case.
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

        return cmps;
    }
}
