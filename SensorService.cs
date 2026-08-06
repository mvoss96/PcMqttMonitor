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
    readonly bool _buildSummary;

    IHardware? _cpu;
    IHardware? _gpu;
    IHardware? _memory;
    IHardware? _motherboard;

    // Static RAM module info (WMI), read once in Open.
    string? _ramType;
    int? _ramSpeedMtps;

    // Per-adapter byte counters from the previous cycle, keyed by adapter id.
    readonly Dictionary<string, (long Sent, long Received, DateTime Time)> _nicLast = new();

    public SensorService(SensorConfig? config, Action<string>? log = null, bool buildSummary = true)
    {
        _config = config ?? new SensorConfig();
        _log = log;
        _buildSummary = buildSummary;
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
        // Prefer a discrete GPU: on hybrid systems (Intel iGPU + NVIDIA/AMD dGPU)
        // plain FirstOrDefault picks whichever LHM enumerates first — which can be
        // the iGPU, hiding the card users actually care about. Intel is ranked
        // last as a heuristic (mostly iGPUs; a discrete Arc still works, it just
        // loses the tie against another vendor's card).
        _gpu = _computer.Hardware
            .Where(IsGpuHardware)
            .OrderBy(h => h.HardwareType == HardwareType.GpuIntel ? 1 : 0)
            .FirstOrDefault();
        _memory = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
        _motherboard = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
        ReadRamModuleInfo();
        DetectFanChannels();
    }

    // All fan channels the hardware exposes (spinning or not) — the Sensors
    // page lists them as checkboxes. Detected once at Open, RPM values are
    // refreshed on every snapshot so the page shows live readings. Static so
    // the UI can reach it without holding a SensorService reference (there is
    // only ever one instance; demo mode seeds it directly).
    public sealed record DetectedFan(string Id, string Name, float Rpm, bool IsGpu);
    public static IReadOnlyList<DetectedFan> DetectedFans { get; private set; } = [];
    public static void SeedDetectedFans(IReadOnlyList<DetectedFan> fans) => DetectedFans = fans;

    // True when Open found fan channels the config had never seen (defaults
    // were just assigned) — the caller persists the config once in response.
    public bool NewFanChannelsDetected { get; private set; }

    void DetectFanChannels()
    {
        var found = new List<DetectedFan>();

        void Scan(IHardware? hw, bool gpu)
        {
            if (hw == null) return;
            hw.Update();
            foreach (var s in hw.Sensors.Where(s => s.SensorType == SensorType.Fan))
                found.Add(new DetectedFan(TopicId(s.Name), s.Name, s.Value ?? 0, gpu));
            foreach (var sub in hw.SubHardware)
                Scan(sub, gpu);
        }

        Scan(_motherboard, gpu: false);   // fans live on the SuperIO sub-hardware
        Scan(_gpu, gpu: true);
        DetectedFans = found;

        // Default for channels the config has never seen: enabled when the fan
        // is spinning — and always for GPU fans, whose zero-RPM idle would
        // otherwise hide them at boot. NewFanChannelsDetected makes the caller
        // persist the config right away, so the defaults are decided exactly
        // once (first start) instead of re-rolled from whatever happens to
        // spin at every boot.
        foreach (var fan in found)
            if (_config.FanChannels.TryAdd(fan.Id, fan.Rpm > 0 || fan.IsGpu))
                NewFanChannelsDetected = true;

        Log($"Fan channels: {string.Join(", ", found.Select(f => $"{f.Name}={f.Rpm:0}rpm"))}");
    }

    // DDR generation and configured transfer rate ("DDR5-6000") from WMI —
    // LHM has no module info. Static hardware data, so reading it once at
    // startup is enough; failures just leave the fields null.
    void ReadRamModuleInfo()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT SMBIOSMemoryType, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            foreach (var module in searcher.Get())
            {
                // SMBIOS memory type (SMBIOS spec 7.18.2) — only the DDR
                // generations matter here.
                _ramType ??= Convert.ToInt32(module["SMBIOSMemoryType"] ?? 0) switch
                {
                    20 => "DDR",
                    21 => "DDR2",
                    24 => "DDR3",
                    26 => "DDR4",
                    30 => "LPDDR4",
                    34 => "DDR5",
                    35 => "LPDDR5",
                    _  => null,
                };
                // ConfiguredClockSpeed is the actual running rate (XMP/EXPO),
                // Speed the rated one — both in MT/s despite the WMI naming.
                var speed = Convert.ToInt32(module["ConfiguredClockSpeed"] ?? 0);
                if (speed == 0) speed = Convert.ToInt32(module["Speed"] ?? 0);
                if (speed > 0 && _ramSpeedMtps == null) _ramSpeedMtps = speed;
                if (_ramType != null && _ramSpeedMtps != null) break;
            }
            Log($"RAM modules: {_ramType ?? "?"}-{_ramSpeedMtps?.ToString() ?? "?"}");
        }
        catch (Exception ex)
        {
            Log($"RAM module info unavailable: {ex.Message}");
        }
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
        // No fallback here: FindFirstSensorValue(Load) would return "GPU Core" load,
        // which is not the memory load.
        var rawGpuMemLoad = FindSensorValue(_gpu, SensorType.Load, "GPU Memory");
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
        var gpuMemLoad = _config.GpuMemoryLoad ? RoundPercentToInt(rawGpuMemLoad) : null;
        var gpuMemUsed = _config.GpuMemoryUsed ? rawGpuMemUsed : null;
        var gpuMemTotal = _config.GpuMemoryTotal ? rawGpuMemTotal : null;

        var cpuName = _cpu?.Name ?? "CPU";
        var gpuName = _gpu?.Name ?? "GPU";
        var motherboardName = _config.MotherboardName ? _motherboard?.Name : null;
        var driveMetrics = _config.Drives ? CollectDriveMetrics() : new List<StorageMetrics>();

        var adapters = (_config.NetworkUpload || _config.NetworkDownload)
            ? CollectNetworkAdapters()
            : null;
        if (adapters != null)
            foreach (var a in adapters)
            {
                if (!_config.NetworkUpload)   a.UploadKbps = null;
                if (!_config.NetworkDownload) a.DownloadKbps = null;
            }

        var fans = CollectFanMetrics();

        var uptimeSec = _config.Uptime ? (int?)(Environment.TickCount64 / 1000) : null;

        // The one-line console summary is only ever seen with --console — skip
        // the string building entirely otherwise.
        var summaryParts = new List<string>();
        if (_buildSummary)
        {
            var cpuSummary = BuildCpuSummary(cpuName, cpuLoad, cpuTemp, cpuPackagePower, cpuCoreVoltage);
            if (cpuSummary != null)
                summaryParts.Add(cpuSummary);

            var gpuSummary = BuildGpuSummary(gpuName, gpuLoad, gpuTemp, gpuBoardPower, gpuMemLoad, gpuMemUsed, gpuMemTotal);
            if (gpuSummary != null)
                summaryParts.Add(gpuSummary);

            var ramSummary = BuildRamSummary(ramLoad, ramUsed, ramTotal);
            if (ramSummary != null)
                summaryParts.Add(ramSummary);

            if (!string.IsNullOrWhiteSpace(motherboardName))
                summaryParts.Add($"MB {motherboardName}");

            if (driveMetrics.Count > 0)
                summaryParts.Add(BuildDrivesSummary(driveMetrics));

            var netSummary = BuildNetworkSummary(adapters);
            if (netSummary != null)
                summaryParts.Add(netSummary);
        }

        var metrics = new MetricsSnapshot
        {
            TimestampUtc = DateTime.UtcNow,
            Host = Environment.MachineName,
            Cpu = BuildCpuMetrics(cpuName, cpuLoad, cpuTemp, cpuPackagePower, cpuCoreVoltage),
            Gpu = BuildGpuMetrics(gpuName, gpuLoad, gpuTemp, gpuBoardPower, gpuMemLoad, gpuMemUsed, gpuMemTotal),
            Ram = BuildRamMetrics(ramLoad, ramUsed, ramTotal),
            Motherboard = BuildMotherboardMetrics(motherboardName),
            Drives = driveMetrics.Count > 0 ? driveMetrics : null,
            Network = adapters is { Count: > 0 } ? adapters : null,
            Fans = fans is { Count: > 0 } ? fans : null,
            // Always present: OS version is static and uptime merely optional.
            System = new SystemMetrics { UptimeSec = uptimeSec, OsVersion = OsVersionString }
        };

        var summary = summaryParts.Count > 0 ? string.Join(" || ", summaryParts) : string.Empty;
        return new SensorSnapshot(summary, metrics);
    }

    // One entry per fan channel the user has enabled on the Sensors page —
    // including ones at 0 RPM (a GPU in zero-RPM idle stays visible instead of
    // its HA entities appearing and disappearing). The matching Control sensor
    // (same name) contributes the PWM duty cycle. Also refreshes DetectedFans
    // with the current readings so the Sensors page checkboxes stay live.
    List<FanMetrics> CollectFanMetrics()
    {
        var result = new List<FanMetrics>();
        var detected = new List<DetectedFan>();

        void AddFans(IHardware? hw, bool gpu)
        {
            if (hw == null) return;
            foreach (var sensor in hw.Sensors.Where(s => s.SensorType == SensorType.Fan))
            {
                var id = TopicId(sensor.Name);
                detected.Add(new DetectedFan(id, sensor.Name, sensor.Value ?? 0, gpu));
                if (!_config.FanChannels.GetValueOrDefault(id)) continue;
                var pwm = hw.Sensors.FirstOrDefault(s =>
                    s.SensorType == SensorType.Control && s.Name == sensor.Name)?.Value;
                result.Add(new FanMetrics
                {
                    Id = id,
                    Name = sensor.Name,
                    Rpm = sensor.Value ?? 0,
                    Pwm = pwm,
                });
            }
            foreach (var sub in hw.SubHardware)
                AddFans(sub, gpu);
        }

        AddFans(_motherboard, gpu: false);   // fans live on the SuperIO sub-hardware
        AddFans(_gpu, gpu: true);
        DetectedFans = detected;
        return result;
    }

    // "Fan #2" → "fan_2", "Ethernet 2" → "ethernet_2" — topic-safe and stable.
    static string TopicId(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        return sb.ToString().TrimEnd('_');
    }

    // One entry per active physical adapter. "Physical" heuristic: type is
    // Ethernet/WiFi AND a default gateway is set — virtual host adapters
    // (VMware, WSL, Hyper-V) report Ethernet but route nowhere.
    List<NetworkAdapterMetrics> CollectNetworkAdapters()
    {
        var result = new List<NetworkAdapterMetrics>();
        var now = DateTime.UtcNow;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;

            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            if (!props.GatewayAddresses.Any(gw => gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                continue;

            var adapter = new NetworkAdapterMetrics
            {
                Id = TopicId(ni.Name),
                Name = ni.Name,
                IpAddress = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.Address.ToString(),
            };
            var mac = ni.GetPhysicalAddress().GetAddressBytes();
            if (mac.Length == 6)
                adapter.Mac = string.Join(":", mac.Select(b => b.ToString("x2")));

            try
            {
                var stats = ni.GetIPv4Statistics();
                if (_nicLast.TryGetValue(ni.Id, out var last))
                {
                    var elapsed = (now - last.Time).TotalSeconds;
                    if (elapsed > 0)
                    {
                        adapter.UploadKbps   = (float)Math.Max(0, (stats.BytesSent     - last.Sent)     / elapsed / 1024.0);
                        adapter.DownloadKbps = (float)Math.Max(0, (stats.BytesReceived - last.Received) / elapsed / 1024.0);
                    }
                }
                _nicLast[ni.Id] = (stats.BytesSent, stats.BytesReceived, now);
            }
            catch { /* some adapters throw on statistics */ }

            result.Add(adapter);
        }
        return result;
    }

    // "Windows 11 Pro 24H2" — ProductName still reports "Windows 10" on
    // Windows 11, fixed up via the build number (>= 22000 means 11).
    static readonly string? OsVersionString = GetOsVersion();

    static string? GetOsVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key == null) return null;
            var product = key.GetValue("ProductName") as string ?? "Windows";
            var display = key.GetValue("DisplayVersion") as string;
            if (int.TryParse(key.GetValue("CurrentBuildNumber") as string, out var build) && build >= 22000)
                product = product.Replace("Windows 10", "Windows 11");
            return display != null ? $"{product} {display}" : product;
        }
        catch { return null; }
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
    List<StorageMetrics> CollectDriveMetrics()
    {
        var results = new List<StorageMetrics>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            // USB sticks and optical media come and go — publishing them would
            // churn HA entities on every plug cycle. Fixed disks and virtual
            // drives (Google Drive reports Fixed) stay.
            if (drive.DriveType is DriveType.Removable or DriveType.CDRom) continue;
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
                Id          = driveLetter.TrimEnd(':').ToLowerInvariant(),
                Name        = name,
                Type        = GetDriveType(driveLetter),
                UsedGb      = usedGb,
                FreeGb      = freeGb,
                TotalGb     = totalGb,
                UsedPercent = usedPct,
            });
        }
        return results;
    }

    // "SSD" / "HDD" / "USB" per drive letter, resolved once and cached — the
    // WMI chain (logical disk → partition → physical disk) is not free.
    // Virtual drives (Google Drive) have no partition association → null.
    readonly Dictionary<string, string?> _driveTypeCache = new();

    string? GetDriveType(string driveLetter)
    {
        if (_driveTypeCache.TryGetValue(driveLetter, out var cached)) return cached;
        string? type = null;
        try
        {
            using var partitions = new System.Management.ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{driveLetter}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");
            foreach (var partition in partitions.Get())
            {
                using var disks = new System.Management.ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                foreach (var disk in disks.Get())
                {
                    if (disk["InterfaceType"] as string == "USB") { type = "USB"; break; }
                    // Spinning vs solid state lives in the storage namespace:
                    // MSFT_PhysicalDisk.MediaType 3 = HDD, 4 = SSD.
                    using var physical = new System.Management.ManagementObjectSearcher(
                        @"root\Microsoft\Windows\Storage",
                        $"SELECT MediaType FROM MSFT_PhysicalDisk WHERE DeviceId='{disk["Index"]}'");
                    foreach (var p in physical.Get())
                        type = Convert.ToInt32(p["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => null };
                    break;
                }
                break;
            }
        }
        catch { /* WMI unavailable — type stays unknown */ }
        _driveTypeCache[driveLetter] = type;
        return type;
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
            MemoryLoad = effectiveMemoryLoad,
            MemoryUsedMb = vramUsed,
            MemoryTotalMb = vramTotal
        };
    }

    RamMetrics? BuildRamMetrics(int? load, float? used, float? total)
    {
        if (!load.HasValue && !used.HasValue && !total.HasValue)
        {
            return null;
        }

        return new RamMetrics
        {
            Load = load,
            UsedGb = used,
            TotalGb = total,
            Type = _ramType,
            SpeedMtps = _ramSpeedMtps
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

    static string? BuildNetworkSummary(List<NetworkAdapterMetrics>? adapters)
    {
        if (adapters == null || adapters.Count == 0) return null;
        return "Net " + string.Join(", ", adapters.Select(a =>
            $"{a.Name} Up {(a.UploadKbps.HasValue ? FormatNetworkSpeed(a.UploadKbps.Value) : "n/a")}" +
            $" Down {(a.DownloadKbps.HasValue ? FormatNetworkSpeed(a.DownloadKbps.Value) : "n/a")}"));
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

// SensorSnapshot and the metrics model live in Metrics.cs.
