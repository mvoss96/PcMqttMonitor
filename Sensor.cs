using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

// Encapsulates all sensor discovery, reading, formatting, and metric construction.
sealed class SensorService : IDisposable
{
    readonly Computer _computer;
    readonly SensorConfig _config;
    readonly Action<string>? _log;

    IHardware? _cpu;
    IHardware? _gpu;
    IHardware? _memory;
    IHardware? _motherboard;

    long _netLastBytesSent = -1;
    long _netLastBytesReceived = -1;
    DateTime _netLastTime;

    public SensorService(SensorConfig? config, Action<string>? log = null)
    {
        _config = config ?? new SensorConfig();
        _log = log;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
        };
    }

    public void Open()
    {
        _computer.Open();
        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        _gpu = _computer.Hardware.FirstOrDefault(IsGpuHardware);
        _memory = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        _motherboard = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
    }

    public SensorSnapshot ReadSnapshot()
    {
        Log("Refreshing sensors.");
        foreach (var hardware in _computer.Hardware)
        {
            UpdateHardwareTree(hardware);
        }

        var rawCpuLoad = FindSensorValue(_cpu, SensorType.Load, "CPU Total");
        var rawCpuTemp = FindSensorValue(_cpu, SensorType.Temperature, "Core (Tctl/Tdie)")
            ?? FindFirstSensorValue(_cpu, SensorType.Temperature);

        var rawGpuLoad = FindSensorValue(_gpu, SensorType.Load, "GPU Core")
            ?? FindFirstSensorValue(_gpu, SensorType.Load);
        var rawGpuTemp = FindSensorValue(_gpu, SensorType.Temperature, "GPU Core")
            ?? FindFirstSensorValue(_gpu, SensorType.Temperature);

        // Use Windows API for all RAM values — matches Task Manager exactly.
        // LHM's values are unreliable (used includes page file, available counts standby differently).
        var (rawRamLoad, rawRamUsed, rawRamTotal) = GetRamFromWindows();

        var rawCpuPackagePower = FindSensorValue(_cpu, SensorType.Power, "Package")
            ?? FindFirstSensorValue(_cpu, SensorType.Power);
        var rawCpuCoreVoltage = FindSensorValue(_cpu, SensorType.Voltage, "Core (SVI2 TFN)")
            ?? FindFirstSensorValue(_cpu, SensorType.Voltage);

        var rawGpuBoardPower = FindSensorValue(_gpu, SensorType.Power, "GPU Board Power")
            ?? FindSensorValue(_gpu, SensorType.Power, "GPU Package")
            ?? FindFirstSensorValue(_gpu, SensorType.Power);
        var rawGpuFan = FindSensorValue(_gpu, SensorType.Fan, "GPU Fan 1")
            ?? FindFirstSensorValue(_gpu, SensorType.Fan);
        var rawGpuMemLoad = FindSensorValue(_gpu, SensorType.Load, "GPU Memory")
            ?? FindFirstSensorValue(_gpu, SensorType.Load);
        var rawGpuMemUsed = FindSensorValue(_gpu, SensorType.SmallData, "GPU Memory Used");
        var rawGpuMemTotal = FindSensorValue(_gpu, SensorType.SmallData, "GPU Memory Total");

        var cpuLoad = _config.CpuLoad ? RoundPercentToInt(rawCpuLoad) : null;
        var cpuTemp = _config.CpuTemp ? rawCpuTemp : null;
        var gpuLoad = _config.GpuLoad ? RoundPercentToInt(rawGpuLoad) : null;
        var gpuTemp = _config.GpuTemp ? rawGpuTemp : null;
        var ramLoad = _config.RamLoad ? RoundPercentToInt(rawRamLoad) : null;
        var ramUsed = _config.RamUsed ? rawRamUsed : null;
        var ramTotal = _config.RamTotal ? rawRamTotal : null;
        var cpuPackagePower = _config.CpuPackagePower ? rawCpuPackagePower : null;
        var cpuCoreVoltage = _config.CpuCoreVoltage ? rawCpuCoreVoltage : null;
        var gpuBoardPower = _config.GpuBoardPower ? rawGpuBoardPower : null;
        var gpuFan = _config.GpuFanSpeed ? rawGpuFan : null;
        var gpuMemLoad = _config.GpuMemoryLoad ? RoundPercentToInt(rawGpuMemLoad) : null;
        var gpuMemUsed = _config.GpuMemoryUsed ? rawGpuMemUsed : null;
        var gpuMemTotal = _config.GpuMemoryTotal ? rawGpuMemTotal : null;

        var cpuName = _cpu?.Name ?? "CPU";
        var gpuName = _gpu?.Name ?? "GPU";
        var motherboardName = _config.MotherboardName ? _motherboard?.Name : null;
        var driveMetrics = _config.Drives ? CollectDriveMetrics() : new List<StorageMetrics>();

        var (rawNetUp, rawNetDown) = GetNetworkSpeed();
        var netUp   = _config.NetworkUpload   ? rawNetUp   : null;
        var netDown = _config.NetworkDownload ? rawNetDown : null;

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

        var netSummary = BuildNetworkSummary(netUp, netDown);
        if (netSummary != null)
            summaryParts.Add(netSummary);

        var metrics = new MqttMetrics
        {
            TimestampUtc = DateTime.UtcNow,
            Host = Environment.MachineName,
            Cpu = BuildCpuMetrics(cpuName, cpuLoad, cpuTemp, cpuPackagePower, cpuCoreVoltage),
            Gpu = BuildGpuMetrics(gpuName, gpuLoad, gpuTemp, gpuBoardPower, gpuFan, gpuMemLoad, gpuMemUsed, gpuMemTotal),
            Ram = BuildRamMetrics(ramLoad, ramUsed, ramTotal),
            Motherboard = BuildMotherboardMetrics(motherboardName),
            Drives = driveMetrics.Count > 0 ? driveMetrics : null,
            Network = BuildNetworkMetrics(netUp, netDown)
        };

        var summary = summaryParts.Count > 0 ? string.Join(" || ", summaryParts) : string.Empty;
        return new SensorSnapshot(summary, metrics);
    }

    (float? uploadKbps, float? downloadKbps) GetNetworkSpeed()
    {
        long sent = 0, received = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = ni.GetIPv4Statistics();
                sent     += stats.BytesSent;
                received += stats.BytesReceived;
            }
            catch { /* some virtual adapters throw */ }
        }

        var now = DateTime.UtcNow;

        if (_netLastBytesSent < 0)
        {
            // First call — store baseline, return null (no delta yet).
            _netLastBytesSent     = sent;
            _netLastBytesReceived = received;
            _netLastTime          = now;
            return (null, null);
        }

        var elapsed = (now - _netLastTime).TotalSeconds;
        if (elapsed <= 0) return (null, null);

        var uploadKbps   = (float)Math.Max(0, (sent     - _netLastBytesSent)     / elapsed / 1024.0);
        var downloadKbps = (float)Math.Max(0, (received - _netLastBytesReceived) / elapsed / 1024.0);

        _netLastBytesSent     = sent;
        _netLastBytesReceived = received;
        _netLastTime          = now;

        return (uploadKbps, downloadKbps);
    }

    public void Dispose()
    {
        _computer.Close();
    }

    void Log(string message)
    {
        _log?.Invoke(message);
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

    // Returns null for NaN/infinity so downstream code never has to deal with them.
    static float? SanitiseSensorValue(float? value)
    {
        if (value == null || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) return null;
        return value;
    }

    static float? FindSensorValue(IHardware? hardware, SensorType type, string name)
    {
        if (hardware == null) return null;

        foreach (var hw in EnumerateHardware(hardware))
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType == type && sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return SanitiseSensorValue(sensor.Value);
                }
            }
        }

        return null;
    }

    static float? FindFirstSensorValue(IHardware? hardware, SensorType type)
    {
        if (hardware == null) return null;

        foreach (var hw in EnumerateHardware(hardware))
        {
            foreach (var sensor in hw.Sensors)
            {
                var value = SanitiseSensorValue(sensor.Value);
                if (sensor.SensorType == type && value.HasValue)
                {
                    return value;
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

    // Reads drive space directly from the OS — no LHM storage backend needed.
    // This avoids the DiskInfoToolkit NullReferenceException that fires on device-change events.
    static List<StorageMetrics> CollectDriveMetrics()
    {
        var results = new List<StorageMetrics>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            var totalGb    = (float)(drive.TotalSize          / 1024d / 1024d / 1024d);
            var freeGb     = (float)(drive.AvailableFreeSpace / 1024d / 1024d / 1024d);
            var usedGb     = totalGb - freeGb;
            var usedPct    = totalGb > 0 ? RoundPercentToInt(usedGb / totalGb * 100f) : null;
            var driveLetter = drive.Name.TrimEnd(Path.DirectorySeparatorChar);
            var name       = string.IsNullOrEmpty(drive.VolumeLabel)
                ? driveLetter
                : $"{drive.VolumeLabel} ({driveLetter})";
            results.Add(new StorageMetrics
            {
                Name        = name,
                UsedGb      = usedGb,
                FreeGb      = freeGb,
                TotalGb     = totalGb,
                UsedPercent = usedPct,
            });
        }
        return results;
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

    static NetworkMetrics? BuildNetworkMetrics(float? uploadKbps, float? downloadKbps)
    {
        if (!uploadKbps.HasValue && !downloadKbps.HasValue) return null;
        return new NetworkMetrics { UploadKbps = uploadKbps, DownloadKbps = downloadKbps };
    }

    static string? BuildNetworkSummary(float? uploadKbps, float? downloadKbps)
    {
        if (!uploadKbps.HasValue && !downloadKbps.HasValue) return null;
        var up   = uploadKbps.HasValue   ? FormatNetworkSpeed(uploadKbps.Value)   : "n/a";
        var down = downloadKbps.HasValue ? FormatNetworkSpeed(downloadKbps.Value) : "n/a";
        return $"Net Up {up} Down {down}";
    }

    static string FormatNetworkSpeed(float kbps)
    {
        if (kbps >= 1024)
            return (kbps / 1024f).ToString("0.##", CultureInfo.InvariantCulture) + " MB/s";
        return kbps.ToString("0.#", CultureInfo.InvariantCulture) + " KB/s";
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

    // Ask Windows for RAM load, used and total — matches Task Manager exactly.
    static (float? load, float? usedGb, float? totalGb) GetRamFromWindows()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (null, null, null);
        var total = (float)(status.ullTotalPhys / 1024d / 1024d / 1024d);
        var avail = (float)(status.ullAvailPhys  / 1024d / 1024d / 1024d);
        var used  = total - avail;
        var load  = (float)status.dwMemoryLoad;
        return (load, used, total);
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

sealed record SensorSnapshot(string Summary, MqttMetrics Metrics);

// MQTT payload root containing grouped metrics.
sealed class MqttMetrics
{
    public DateTime TimestampUtc { get; set; }
    public string Host { get; set; } = string.Empty;
    public CpuMetrics? Cpu { get; set; }
    public GpuMetrics? Gpu { get; set; }
    public RamMetrics? Ram { get; set; }
    public MotherboardMetrics? Motherboard { get; set; }
    public List<StorageMetrics>? Drives { get; set; }
    public NetworkMetrics? Network { get; set; }
}

// CPU metrics payload.
sealed class CpuMetrics
{
    public string Name { get; set; } = string.Empty;
    public int? Load { get; set; }
    public float? TempC { get; set; }
    public float? PackagePowerW { get; set; }
    public float? CoreVoltageV { get; set; }
}

// GPU metrics payload.
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

// RAM metrics payload.
sealed class RamMetrics
{
    public int? Load { get; set; }
    public float? UsedGb { get; set; }
    public float? TotalGb { get; set; }
}

// Motherboard metrics payload.
sealed class MotherboardMetrics
{
    public string Name { get; set; } = string.Empty;
}

// Storage metrics payload for each drive.
sealed class StorageMetrics
{
    public string Name { get; set; } = string.Empty;
    public float? UsedGb { get; set; }
    public float? FreeGb { get; set; }
    public float? TotalGb { get; set; }
    public int? UsedPercent { get; set; }
}

// Network throughput metrics payload.
sealed class NetworkMetrics
{
    public float? UploadKbps { get; set; }
    public float? DownloadKbps { get; set; }
}
