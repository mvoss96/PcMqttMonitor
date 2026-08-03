# PC MQTT Monitor

A lightweight Windows system-tray app that reads hardware sensors (CPU, GPU, RAM, drives, network) via [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and publishes them to an MQTT broker — with native **Home Assistant MQTT Discovery** support, so your PC shows up in Home Assistant as a single device with all its sensors.

## Features

- **Hardware metrics**: CPU load/temperature/package power/core voltage, GPU load/temperature/power/fan/VRAM, RAM usage, drive usage & temperatures, network up/download, uptime
- **MQTT publishing** with configurable topic root and interval, optional TLS, username/password auth
- **Availability topic + Last Will**: subscribers always know whether the PC is online
- **Home Assistant MQTT Discovery** (opt-in): all sensors appear automatically as one HA device
- **System tray UI**: live sensor view, settings dialog, pause/resume publishing, hover tooltip with key metrics
- **Sensor toggles**: publish only the metrics you want
- **Update notification**: checks GitHub releases daily (opt-out) and shows a tray notification when a new version is available — never downloads or installs anything by itself
- **Robust**: auto-reconnect, crash logging, automatic restart on hardware-driver crashes, autostart via Task Scheduler

## Installation

Download the latest `PcMqttMonitorSetup-x.y.z.exe` from the [releases page](https://github.com/mvoss96/PcMqttMonitor/releases/latest) and run it. The installer:

- installs to `C:\Program Files\PcMqttMonitor`
- optionally registers autostart at logon (via Task Scheduler, elevated)
- preserves your `config.json` across upgrades

### Why does it require administrator rights?

LibreHardwareMonitor needs its kernel driver to read CPU temperature, package power, and voltages — without elevation those values silently read zero. The app therefore runs elevated (`requireAdministrator`), and autostart uses a scheduled task with highest privileges (the registry `Run` key silently skips elevated apps).

## Configuration

Everything is configured from the tray icon → **Settings**:

- **MQTT Broker**: host, port, TLS, credentials, topic root — applied via *Test & Apply* (verifies the connection, then restarts the app)
- **General**: publish interval, autostart, debug logging, Home Assistant discovery, update notifications
- **Sensors**: per-metric checkboxes

Settings are stored in `config.json` next to the exe.

## MQTT topics

With topic root `pc` and hostname `myhost`:

```
pc/myhost/availability        online | offline   (retained, set via Last Will on unclean disconnect)
pc/myhost/status              full JSON snapshot of all metrics
pc/myhost/cpu/load            12.3               (plain scalar subtopics, retained)
pc/myhost/cpu/temp            45
pc/myhost/cpu/power           28.5
pc/myhost/cpu/voltage         1.225
pc/myhost/gpu/load            5
pc/myhost/gpu/temp            38
pc/myhost/gpu/vram_used       1234
pc/myhost/ram/load            42.1
pc/myhost/drives/0/name       C:
pc/myhost/drives/0/percent    61.2
pc/myhost/net/up              12.3               (kbit/s)
pc/myhost/net/down            345.6
pc/myhost/system/uptime       86400              (seconds)
...
```

When Home Assistant discovery is enabled, a retained device-discovery config is published under `homeassistant/device/...` and HA picks up all sensors automatically.

## Building from source

Requires the .NET 10 SDK and (for the installer) [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
# Self-contained single-file exe
dotnet publish PcMqttMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish

# Installer (optional)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss
```

Run with `--console` to see log output in the launching terminal.

## License

[MIT](LICENSE). Uses [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0) and [MQTTnet](https://github.com/dotnet/MQTTnet) (MIT).
