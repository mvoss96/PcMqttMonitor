using System.Globalization;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;
using MQTTnet;
using MQTTnet.Protocol;

class Program
{
    static async Task Main()
    {
        var config = LoadConfig("config.json");

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true
        };

        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();

        var mqttOptions = mqttFactory.CreateClientOptionsBuilder()
            .WithTcpServer(config.BrokerHost, config.BrokerPort)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{Environment.MachineName.ToLowerInvariant()}")
            .Build();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        computer.Open();
        try
        {
            var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            var gpu = computer.Hardware.FirstOrDefault(IsGpuHardware);
            var memory = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);

            await EnsureMqttConnectedAsync(mqttClient, mqttOptions, shutdown.Token);

            while (!shutdown.IsCancellationRequested)
            {
                foreach (var hardware in computer.Hardware)
                {
                    UpdateHardwareTree(hardware);
                }

                var cpuLoad = FindSensorValue(cpu, SensorType.Load, "CPU Total");
                var cpuTemp = FindSensorValue(cpu, SensorType.Temperature, "Core (Tctl/Tdie)")
                    ?? FindFirstSensorValue(cpu, SensorType.Temperature);

                var gpuLoad = FindSensorValue(gpu, SensorType.Load, "GPU Core")
                    ?? FindFirstSensorValue(gpu, SensorType.Load);
                var gpuTemp = FindSensorValue(gpu, SensorType.Temperature, "GPU Core")
                    ?? FindFirstSensorValue(gpu, SensorType.Temperature);

                var ramLoad = FindSensorValue(memory, SensorType.Load, "Memory");
                var ramUsed = FindSensorValue(memory, SensorType.Data, "Memory Used");
                var ramAvailable = FindSensorValue(memory, SensorType.Data, "Memory Available");
                var ramTotal = FindSensorValue(memory, SensorType.Data, "Memory Total")
                    ?? (ramUsed.HasValue && ramAvailable.HasValue ? ramUsed + ramAvailable : null);

                var cpuName = cpu?.Name ?? "CPU";
                var gpuName = gpu?.Name ?? "GPU";

                Console.WriteLine(
                    $"GPU {gpuName}: {FormatPercent(gpuLoad)} {FormatCelsius(gpuTemp)} || " +
                    $"CPU {cpuName}: {FormatPercent(cpuLoad)} {FormatCelsius(cpuTemp)} || " +
                    $"RAM: {FormatRamUsage(ramLoad, ramUsed, ramTotal)}");

                await PublishToMqttAsync(mqttFactory, mqttClient, mqttOptions, config.Topic, new MqttMetrics
                {
                    TimestampUtc = DateTime.UtcNow,
                    Host = Environment.MachineName,
                    CpuName = cpuName,
                    CpuLoad = cpuLoad,
                    CpuTempC = cpuTemp,
                    GpuName = gpuName,
                    GpuLoad = gpuLoad,
                    GpuTempC = gpuTemp,
                    RamLoad = ramLoad,
                    RamUsedGb = ramUsed,
                    RamTotalGb = ramTotal
                }, shutdown.Token);

                await Task.Delay(TimeSpan.FromSeconds(config.PublishIntervalSeconds), shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (mqttClient.IsConnected)
            {
                var disconnectOptions = mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                await mqttClient.DisconnectAsync(disconnectOptions, CancellationToken.None);
            }
            computer.Close();
        }
    }

    static void UpdateHardwareTree(IHardware hardware)
    {
        hardware.Update();

        foreach (var sub in hardware.SubHardware)
        {
            UpdateHardwareTree(sub);
        }
    }

    static bool IsGpuHardware(IHardware hardware)
    {
        return hardware.HardwareType == HardwareType.GpuNvidia
            || hardware.HardwareType == HardwareType.GpuAmd
            || hardware.HardwareType == HardwareType.GpuIntel;
    }

    static float? FindSensorValue(IHardware? hardware, SensorType type, string name)
    {
        if (hardware == null)
        {
            return null;
        }

        foreach (var hw in EnumerateHardware(hardware))
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType == type && sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return sensor.Value;
                }
            }
        }

        return null;
    }

    static float? FindFirstSensorValue(IHardware? hardware, SensorType type)
    {
        if (hardware == null)
        {
            return null;
        }

        foreach (var hw in EnumerateHardware(hardware))
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType == type && sensor.Value.HasValue)
                {
                    return sensor.Value;
                }
            }
        }

        return null;
    }

    static IEnumerable<IHardware> EnumerateHardware(IHardware hardware)
    {
        yield return hardware;

        foreach (var sub in hardware.SubHardware)
        {
            foreach (var child in EnumerateHardware(sub))
            {
                yield return child;
            }
        }
    }

    static string FormatPercent(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%"
            : "n/a";
    }

    static string FormatCelsius(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "C"
            : "n/a";
    }

    static string FormatRamUsage(float? load, float? used, float? total)
    {
        if (used.HasValue && total.HasValue && total.Value > 0)
        {
            var usedText = FormatGigabytes(used.Value);
            var totalText = FormatGigabytes(total.Value);
            var loadText = FormatPercent(load);
            return load.HasValue
                ? $"{loadText} ({usedText}/{totalText}GB)"
                : $"{usedText}/{totalText}GB";
        }

        return FormatPercent(load);
    }

    static string FormatGigabytes(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    static async Task PublishToMqttAsync(
        MqttClientFactory mqttFactory,
        IMqttClient mqttClient,
        MqttClientOptions mqttOptions,
        string topic,
        MqttMetrics metrics,
        CancellationToken cancellationToken)
    {
        await EnsureMqttConnectedAsync(mqttClient, mqttOptions, cancellationToken);

        var payload = JsonSerializer.Serialize(metrics, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        var message = mqttFactory.CreateApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await mqttClient.PublishAsync(message, cancellationToken);
    }

    static async Task EnsureMqttConnectedAsync(
        IMqttClient mqttClient,
        MqttClientOptions mqttOptions,
        CancellationToken cancellationToken)
    {
        if (mqttClient.IsConnected)
        {
            return;
        }

        await mqttClient.ConnectAsync(mqttOptions, cancellationToken);
    }

    static AppConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}");
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config == null)
        {
            throw new InvalidOperationException("Config file is empty or invalid JSON.");
        }

        if (string.IsNullOrWhiteSpace(config.BrokerHost))
        {
            throw new InvalidOperationException("Config missing BrokerHost.");
        }

        if (config.BrokerPort <= 0)
        {
            throw new InvalidOperationException("Config BrokerPort must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(config.Topic))
        {
            throw new InvalidOperationException("Config missing Topic.");
        }

        if (config.PublishIntervalSeconds <= 0)
        {
            throw new InvalidOperationException("Config PublishIntervalSeconds must be > 0.");
        }

        return config;
    }

    sealed class AppConfig
    {
        public string BrokerHost { get; set; } = string.Empty;
        public int BrokerPort { get; set; } = 1883;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public double PublishIntervalSeconds { get; set; } = 1.0;
    }

    sealed class MqttMetrics
    {
        public DateTime TimestampUtc { get; set; }
        public string Host { get; set; } = string.Empty;
        public string CpuName { get; set; } = string.Empty;
        public float? CpuLoad { get; set; }
        public float? CpuTempC { get; set; }
        public string GpuName { get; set; } = string.Empty;
        public float? GpuLoad { get; set; }
        public float? GpuTempC { get; set; }
        public float? RamLoad { get; set; }
        public float? RamUsedGb { get; set; }
        public float? RamTotalGb { get; set; }
    }
}
