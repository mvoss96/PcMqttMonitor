# PC MQTT Monitor — Development Guidelines

## Versioning

After **every** change — whether feature, bug fix, or improvement — increment the patch version (`1.0.x`) in **both** files:

- `PcMqttMonitor.csproj` → `<Version>1.0.x</Version>`
- `installer.iss` → `#define MyAppVersion "1.0.x"`

Use minor version bumps (`1.x.0`) for larger feature additions.

## Build & Deploy

Deployment goes through the Inno Setup installer (install location: `C:\Program Files\PcMqttMonitor`):

```powershell
# 1. Publish self-contained single-file exe into publish\
dotnet publish PcMqttMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish

# 2. Compile the installer
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss

# 3. Run installer-output\PcMqttMonitorSetup-<version>.exe (UAC prompt; kills the
#    running app automatically, replaces the exe, leaves config.json untouched)
```

Do not publish directly into the install directory — writing to Program Files needs an elevated shell; the installer handles that.

## Project Structure

- `Program.cs` — entry point, MQTT loop, crash handling
- `TrayApp.cs` — system tray icon, context menu, pause/resume
- `MainWindow.cs` — sensors tab + settings tab (WinForms)
- `Sensor.cs` — LibreHardwareMonitor wrapper, metrics model, MQTT payload types
- `Config.cs` — JSON config model (`config.json` lives next to the exe)
- `MqttPublisher.cs` — MQTT publish logic
- `app.ico` — application icon (16/32/48/256px signal bars design)
- `app.manifest` — requires `requireAdministrator` (needed for LHM hardware access)
- `installer.iss` — Inno Setup installer script
- `tools/genicon/` — tool to regenerate `app.ico` (run with `dotnet run --project tools/genicon`)

## Key Decisions

- **Config location**: next to the exe (`AppContext.BaseDirectory`) — also under Program Files, which works because the app always runs elevated. Survives `dotnet build` and installer upgrades; only lost on `dotnet clean`.
- **Install location**: `C:\Program Files\PcMqttMonitor` (`{autopf}`, `UsePreviousAppDir=no`). Not LocalAppData — an exe that runs elevated must not be writable by non-admin processes.
- **Elevation is genuinely required**: without admin, LHM cannot load its Ring0 driver — CPU temp and package power silently read 0 and voltages show VID defaults instead of measurements (verified empirically). GPU (NVAPI), RAM, drives and network work unelevated, but CPU metrics are core functionality.
- **Autostart**: Windows Task Scheduler (`schtasks /RL HIGHEST /SC ONLOGON`) — not the registry Run key, because `requireAdministrator` apps are silently skipped there.
- **Settings apply**: MQTT settings require "Test & Apply" which restarts the app (`Application.Restart()`). General/sensor settings use a regular Save.
- **Hardware crashes**: LibreHardwareMonitor background threads can crash on driver updates or at boot. The `AppDomain.UnhandledException` handler detects these and auto-restarts. All crashes are logged to `crash.log` next to the exe.
- **Startup delay**: if system uptime < 30s, wait for storage drivers to settle before opening sensors.
