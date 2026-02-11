using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibreHardwareMonitor.Hardware;
using MQTTnet;
using MQTTnet.Protocol;

class Program
{
    static async Task Main()
    {
        var config = LoadConfig("config.json");
        LogDebug(config.DebugEnabled, "Config loaded.");

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true
        };

        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();

        var mqttOptions = mqttFactory.CreateClientOptionsBuilder()
            .WithTcpServer(config.BrokerHost, config.BrokerPort)
            .WithCredentials(config.Username, config.Password)
            .WithClientId($"pcmqtt-{Environment.MachineName.ToLowerInvariant()}")
            .Build();
        LogDebug(config.DebugEnabled, "MQTT options built.");

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
            var motherboard = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
            var storages = computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();

            await EnsureMqttConnectedAsync(mqttClient, mqttOptions, shutdown.Token);
            LogDebug(config.DebugEnabled, "MQTT connected.");

            while (!shutdown.IsCancellationRequested)
            {
                LogDebug(config.DebugEnabled, "Refreshing sensors.");
                foreach (var hardware in computer.Hardware)
                {
                    UpdateHardwareTree(hardware);
                }

                var rawCpuLoad = FindSensorValue(cpu, SensorType.Load, "CPU Total");
                var rawCpuTemp = FindSensorValue(cpu, SensorType.Temperature, "Core (Tctl/Tdie)")
                    ?? FindFirstSensorValue(cpu, SensorType.Temperature);

                var rawGpuLoad = FindSensorValue(gpu, SensorType.Load, "GPU Core")
                    ?? FindFirstSensorValue(gpu, SensorType.Load);
                var rawGpuTemp = FindSensorValue(gpu, SensorType.Temperature, "GPU Core")
                    ?? FindFirstSensorValue(gpu, SensorType.Temperature);

                var rawRamLoad = FindSensorValue(memory, SensorType.Load, "Memory");
                var rawRamUsed = FindSensorValue(memory, SensorType.Data, "Memory Used");
                var rawRamAvailable = FindSensorValue(memory, SensorType.Data, "Memory Available");
                var rawRamTotal = FindSensorValue(memory, SensorType.Data, "Memory Total")
                    ?? (rawRamUsed.HasValue && rawRamAvailable.HasValue ? rawRamUsed + rawRamAvailable : null);

                var rawCpuPackagePower = FindSensorValue(cpu, SensorType.Power, "Package")
                    ?? FindFirstSensorValue(cpu, SensorType.Power);
                var rawCpuCoreVoltage = FindSensorValue(cpu, SensorType.Voltage, "Core (SVI2 TFN)")
                    ?? FindFirstSensorValue(cpu, SensorType.Voltage);

                var rawGpuBoardPower = FindSensorValue(gpu, SensorType.Power, "GPU Board Power")
                    ?? FindSensorValue(gpu, SensorType.Power, "GPU Package")
                    ?? FindFirstSensorValue(gpu, SensorType.Power);
                var rawGpuFan = FindSensorValue(gpu, SensorType.Fan, "GPU Fan 1")
                    ?? FindFirstSensorValue(gpu, SensorType.Fan);
                var rawGpuMemLoad = FindSensorValue(gpu, SensorType.Load, "GPU Memory")
                    ?? FindFirstSensorValue(gpu, SensorType.Load);
                var rawGpuMemUsed = FindSensorValue(gpu, SensorType.SmallData, "GPU Memory Used");
                var rawGpuMemTotal = FindSensorValue(gpu, SensorType.SmallData, "GPU Memory Total");

                var sensors = config.Sensors ?? new SensorConfig();

                var cpuLoad = sensors.CpuLoad ? RoundPercentToInt(rawCpuLoad) : null;
                var cpuTemp = sensors.CpuTemp ? rawCpuTemp : null;
                var gpuLoad = sensors.GpuLoad ? RoundPercentToInt(rawGpuLoad) : null;
                var gpuTemp = sensors.GpuTemp ? rawGpuTemp : null;
                var ramLoad = sensors.RamLoad ? RoundPercentToInt(rawRamLoad) : null;
                var ramUsed = sensors.RamUsed ? rawRamUsed : null;
                var ramTotal = sensors.RamTotal ? rawRamTotal : null;
                var cpuPackagePower = sensors.CpuPackagePower ? rawCpuPackagePower : null;
                var cpuCoreVoltage = sensors.CpuCoreVoltage ? rawCpuCoreVoltage : null;
                var gpuBoardPower = sensors.GpuBoardPower ? rawGpuBoardPower : null;
                var gpuFan = sensors.GpuFanSpeed ? rawGpuFan : null;
                var gpuMemLoad = sensors.GpuMemoryLoad ? RoundPercentToInt(rawGpuMemLoad) : null;
                var gpuMemUsed = sensors.GpuMemoryUsed ? rawGpuMemUsed : null;
                var gpuMemTotal = sensors.GpuMemoryTotal ? rawGpuMemTotal : null;

                var cpuName = cpu?.Name ?? "CPU";
                var gpuName = gpu?.Name ?? "GPU";
                var motherboardName = sensors.MotherboardName ? motherboard?.Name : null;
                var driveMetrics = sensors.Drives ? CollectDriveMetrics(storages) : new List<StorageMetrics>();

                var summaryParts = new List<string>();

                var cpuSummary = BuildCpuSummary(cpuName, cpuLoad, cpuTemp, cpuPackagePower, cpuCoreVoltage);
                if (cpuSummary != null)
                {
                    summaryParts.Add(cpuSummary);
                }

                var gpuSummary = BuildGpuSummary(gpuName, gpuLoad, gpuTemp, gpuBoardPower, gpuFan, gpuMemLoad, gpuMemUsed, gpuMemTotal);
                if (gpuSummary != null)
                {
                    summaryParts.Add(gpuSummary);
                }

                var ramSummary = BuildRamSummary(ramLoad, ramUsed, ramTotal);
                if (ramSummary != null)
                {
                    summaryParts.Add(ramSummary);
                }

                if (!string.IsNullOrWhiteSpace(motherboardName))
                {
                    summaryParts.Add($"MB {motherboardName}");
                }

                if (driveMetrics.Count > 0)
                {
                    summaryParts.Add(BuildDrivesSummary(driveMetrics));
                }

                if (summaryParts.Count > 0)
                {
                    Console.WriteLine(string.Join(" || ", summaryParts));
                }

                await PublishToMqttAsync(mqttFactory, mqttClient, mqttOptions, config.Topic, new MqttMetrics
                {
                    TimestampUtc = DateTime.UtcNow,
                    Host = Environment.MachineName,
                    Cpu = BuildCpuMetrics(cpuName, cpuLoad, cpuTemp, cpuPackagePower, cpuCoreVoltage),
                    Gpu = BuildGpuMetrics(gpuName, gpuLoad, gpuTemp, gpuBoardPower, gpuFan, gpuMemLoad, gpuMemUsed, gpuMemTotal),
                    Ram = BuildRamMetrics(ramLoad, ramUsed, ramTotal),
                    Motherboard = BuildMotherboardMetrics(motherboardName),
                    Drives = driveMetrics.Count > 0 ? driveMetrics : null
                }, shutdown.Token);

                LogDebug(config.DebugEnabled, "MQTT publish complete.");

                await Task.Delay(TimeSpan.FromSeconds(config.PublishIntervalSeconds), shutdown.Token);
            }
        }
        catch (OperationCanceledException)
        {
            LogDebug(config.DebugEnabled, "Shutdown requested.");
        }
        finally
        {
            if (mqttClient.IsConnected)
            {
                var disconnectOptions = mqttFactory.CreateClientDisconnectOptionsBuilder().Build();
                await mqttClient.DisconnectAsync(disconnectOptions, CancellationToken.None);
                LogDebug(config.DebugEnabled, "MQTT disconnected.");
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
            ? value.Value.ToString("0", CultureInfo.InvariantCulture) + "%"
            : "n/a";
    }

    static string FormatPercent(int? value)
    {
        return value.HasValue
            ? value.Value.ToString("0", CultureInfo.InvariantCulture) + "%"
            : "n/a";
    }

    static int? RoundPercentToInt(float? value)
    {
        return value.HasValue ? (int)MathF.Round(value.Value) : null;
    }

    static string FormatCelsius(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "C"
            : "n/a";
    }

    static string FormatRamUsage(int? load, float? used, float? total)
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

    static string? BuildRamSummary(int? load, float? used, float? total)
    {
        if (!load.HasValue && !used.HasValue && !total.HasValue)
        {
            return null;
        }

        return $"RAM: {FormatRamUsage(load, used, total)}";
    }

    static string? BuildCpuSummary(string name, int? load, float? temp, float? packagePower, float? coreVoltage)
    {
        var parts = new List<string>();

        if (load.HasValue)
        {
            parts.Add(FormatPercent(load));
        }

        if (temp.HasValue)
        {
            parts.Add(FormatCelsius(temp));
        }

        if (packagePower.HasValue)
        {
            parts.Add("Pkg " + FormatWatts(packagePower));
        }

        if (coreVoltage.HasValue)
        {
            parts.Add("Vcore " + FormatVolts(coreVoltage));
        }

        return parts.Count == 0 ? $"CPU {name}" : $"CPU {name}: {string.Join(", ", parts)}";
    }

    static string? BuildGpuSummary(
        string name,
        int? load,
        float? temp,
        float? boardPower,
        float? fan,
        int? vramLoad,
        float? vramUsed,
        float? vramTotal)
    {
        var parts = new List<string>();

        if (load.HasValue)
        {
            parts.Add(FormatPercent(load));
        }

        if (temp.HasValue)
        {
            parts.Add(FormatCelsius(temp));
        }

        if (boardPower.HasValue)
        {
            parts.Add("Pwr " + FormatWatts(boardPower));
        }

        if (fan.HasValue)
        {
            parts.Add("Fan " + FormatRpm(fan));
        }

        if (vramUsed.HasValue || vramTotal.HasValue)
        {
            parts.Add("VRAM " + FormatGpuMemory(vramUsed, vramTotal));
        }
        else if (vramLoad.HasValue)
        {
            parts.Add("VRAM Load " + FormatPercent(vramLoad));
        }

        return parts.Count == 0 ? $"GPU {name}" : $"GPU {name}: {string.Join(", ", parts)}";
    }

    static CpuMetrics BuildCpuMetrics(string name, int? load, float? temp, float? packagePower, float? coreVoltage)
    {
        return new CpuMetrics
        {
            Name = name,
            Load = load,
            TempC = temp,
            PackagePowerW = packagePower,
            CoreVoltageV = coreVoltage
        };
    }

    static GpuMetrics BuildGpuMetrics(
        string name,
        int? load,
        float? temp,
        float? boardPower,
        float? fan,
        int? vramLoad,
        float? vramUsed,
        float? vramTotal)
    {
        var effectiveMemoryLoad = (vramUsed.HasValue || vramTotal.HasValue) ? null : vramLoad;

        return new GpuMetrics
        {
            Name = name,
            Load = load,
            TempC = temp,
            BoardPowerW = boardPower,
            FanRpm = fan,
            MemoryLoad = effectiveMemoryLoad,
            MemoryUsedMb = vramUsed,
            MemoryTotalMb = vramTotal
        };
    }

    static RamMetrics? BuildRamMetrics(int? load, float? used, float? total)
    {
        if (!load.HasValue && !used.HasValue && !total.HasValue)
        {
            return null;
        }

        return new RamMetrics
        {
            Load = load,
            UsedGb = used,
            TotalGb = total
        };
    }

    static string FormatGigabytes(float value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    static string FormatWatts(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "W"
            : "n/a";
    }

    static string FormatVolts(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) + "V"
            : "n/a";
    }

    static string FormatRpm(float? value)
    {
        return value.HasValue
            ? value.Value.ToString("0", CultureInfo.InvariantCulture) + " RPM"
            : "n/a";
    }

    static string FormatGpuMemory(float? used, float? total)
    {
        if (used.HasValue && total.HasValue && total.Value > 0)
        {
            var usedGb = used.Value / 1024f;
            var totalGb = total.Value / 1024f;
            var percent = (used.Value / total.Value) * 100f;
            return percent.ToString("0", CultureInfo.InvariantCulture) + "% (" +
                usedGb.ToString("0.##", CultureInfo.InvariantCulture) + "/" +
                totalGb.ToString("0.##", CultureInfo.InvariantCulture) + "GB)";
        }

        if (used.HasValue)
        {
            var usedGb = used.Value / 1024f;
            return usedGb.ToString("0.##", CultureInfo.InvariantCulture) + "GB";
        }

        if (total.HasValue)
        {
            var totalGb = total.Value / 1024f;
            return totalGb.ToString("0.##", CultureInfo.InvariantCulture) + "GB";
        }

        return "n/a";
    }

    static string FormatStorageUsage(float? used, float? free, float? total)
    {
        if (total.HasValue && total.Value > 0 && used.HasValue)
        {
            var percent = RoundPercentToInt((used.Value / total.Value) * 100f);
            var usedText = FormatGigabytes(used.Value);
            var freeText = free.HasValue ? FormatGigabytes(free.Value) : "n/a";
            var totalText = FormatGigabytes(total.Value);
            return $"{FormatPercent(percent)} ({usedText}/{freeText}/{totalText}GB)";
        }

        if (used.HasValue || free.HasValue || total.HasValue)
        {
            var usedText = used.HasValue ? FormatGigabytes(used.Value) : "n/a";
            var freeText = free.HasValue ? FormatGigabytes(free.Value) : "n/a";
            var totalText = total.HasValue ? FormatGigabytes(total.Value) : "n/a";
            return $"{usedText}/{freeText}/{totalText}GB";
        }

        return "n/a";
    }

    static MotherboardMetrics? BuildMotherboardMetrics(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new MotherboardMetrics
        {
            Name = name
        };
    }

    static string BuildDrivesSummary(IEnumerable<StorageMetrics> drives)
    {
        var parts = new List<string>();
        foreach (var drive in drives)
        {
            var usage = FormatStorageUsage(drive.UsedGb, drive.FreeGb, drive.TotalGb);
            parts.Add($"{drive.Name}: {usage}");
        }

        return "Drives " + string.Join(", ", parts);
    }

    static List<StorageMetrics> CollectDriveMetrics(IEnumerable<IHardware> storages)
    {
        var results = new List<StorageMetrics>();
        var osDrives = GetOsDrives();

        foreach (var storage in storages)
        {
            var used = FindStorageValue(storage, "Used Space", "Used", "Usage");
            var free = FindStorageValue(storage, "Available Space", "Free Space", "Available", "Free");
            var total = FindStorageValue(storage, "Total Capacity", "Total");

            if (!total.HasValue && used.HasValue && free.HasValue)
            {
                total = used + free;
            }

            if (!used.HasValue && total.HasValue && free.HasValue)
            {
                used = total - free;
            }

            if (!free.HasValue && total.HasValue && used.HasValue)
            {
                free = total - used;
            }

            if ((!used.HasValue || !free.HasValue || !total.HasValue) && osDrives.Count > 0)
            {
                var match = MatchDriveBySize(storage.Name, total, osDrives);
                if (match != null)
                {
                    total ??= match.TotalGb;
                    free ??= match.FreeGb;
                    used ??= match.UsedGb;
                }
            }

            var usedPercent = (used.HasValue && total.HasValue && total.Value > 0)
                ? RoundPercentToInt((used.Value / total.Value) * 100f)
                : null;

            if (!used.HasValue && !free.HasValue && !total.HasValue)
            {
                continue;
            }

            results.Add(new StorageMetrics
            {
                Name = storage.Name,
                UsedGb = used,
                FreeGb = free,
                TotalGb = total,
                UsedPercent = usedPercent
            });
        }

        return results;
    }

    static List<OsDriveSnapshot> GetOsDrives()
    {
        var results = new List<OsDriveSnapshot>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
            {
                continue;
            }

            var totalGb = (float)(drive.TotalSize / 1024d / 1024d / 1024d);
            var freeGb = (float)(drive.AvailableFreeSpace / 1024d / 1024d / 1024d);
            var usedGb = totalGb - freeGb;

            results.Add(new OsDriveSnapshot
            {
                Name = drive.Name,
                Label = drive.VolumeLabel,
                TotalGb = totalGb,
                FreeGb = freeGb,
                UsedGb = usedGb
            });
        }

        return results;
    }

    static OsDriveSnapshot? MatchDriveBySize(string hardwareName, float? totalGb, List<OsDriveSnapshot> osDrives)
    {
        if (!totalGb.HasValue || totalGb.Value <= 0)
        {
            return null;
        }

        var bestDiff = float.MaxValue;
        OsDriveSnapshot? best = null;
        var threshold = Math.Max(1f, totalGb.Value * 0.02f);

        foreach (var drive in osDrives)
        {
            var diff = Math.Abs(totalGb.Value - drive.TotalGb);
            if (diff <= threshold && diff < bestDiff)
            {
                bestDiff = diff;
                best = drive;
            }
        }

        return best;
    }

    static float? FindSensorValueContains(IHardware? hardware, SensorType type, string namePart)
    {
        if (hardware == null)
        {
            return null;
        }

        foreach (var hw in EnumerateHardware(hardware))
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType == type
                    && sensor.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase))
                {
                    return sensor.Value;
                }
            }
        }

        return null;
    }

    static float? FindStorageValue(IHardware hardware, params string[] names)
    {
        foreach (var name in names)
        {
            var exact = FindSensorValue(hardware, SensorType.Data, name)
                ?? FindSensorValue(hardware, SensorType.SmallData, name);
            if (exact.HasValue)
            {
                return exact;
            }
        }

        foreach (var name in names)
        {
            var partial = FindSensorValueContains(hardware, SensorType.Data, name)
                ?? FindSensorValueContains(hardware, SensorType.SmallData, name);
            if (partial.HasValue)
            {
                return partial;
            }
        }

        return null;
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
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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

        config.Sensors ??= new SensorConfig();

        if (config.PublishIntervalSeconds <= 0)
        {
            throw new InvalidOperationException("Config PublishIntervalSeconds must be > 0.");
        }

        return config;
    }

    static void LogDebug(bool enabled, string message)
    {
        if (!enabled)
        {
            return;
        }

        Console.WriteLine($"[debug] {DateTime.Now:HH:mm:ss} {message}");
    }

    sealed class AppConfig
    {
        public string BrokerHost { get; set; } = string.Empty;
        public int BrokerPort { get; set; } = 1883;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Topic { get; set; } = string.Empty;
        public double PublishIntervalSeconds { get; set; } = 1.0;
        public bool DebugEnabled { get; set; } = true;
        public SensorConfig? Sensors { get; set; }
    }

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
    }

    sealed class MqttMetrics
    {
        public DateTime TimestampUtc { get; set; }
        public string Host { get; set; } = string.Empty;
        public CpuMetrics? Cpu { get; set; }
        public GpuMetrics? Gpu { get; set; }
        public RamMetrics? Ram { get; set; }
        public MotherboardMetrics? Motherboard { get; set; }
        public List<StorageMetrics>? Drives { get; set; }
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

    sealed class OsDriveSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public float TotalGb { get; set; }
        public float FreeGb { get; set; }
        public float UsedGb { get; set; }
    }
}
