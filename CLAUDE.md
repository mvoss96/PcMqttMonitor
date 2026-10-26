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

## Testing

Check changes yourself before handing them over:

```powershell
# Unit tests (input rules, sink status reports, name shortening) — also run by CI
dotnet test tests/PcMqttMonitor.Tests -c Release

# UI: render every page plus the scenario states (invalid input, save footer,
# sink status, pause notice) and look at the PNGs. Runs unelevated via dotnet;
# demo mode uses a throwaway config in %TEMP%, never the real config.json.
dotnet build PcMqttMonitor.csproj -c Release
dotnet bin\Release\net10.0-windows\PcMqttMonitor.dll --screenshots <dir> --lang de --theme dark --scale 1 --scenarios

# Interactive demo window (demo data, no sensors, no sinks) to click through the UI
dotnet bin\Release\net10.0-windows\PcMqttMonitor.dll --demo --lang de
```

Add a scenario in `DemoMode.RunScenarios` for every new UI state. Never open real COM ports in tests (a port open can reset an attached ESP32).

## Releasing on GitHub

Repo: `mvoss96/PcMqttMonitor`. CI (`.github/workflows/build.yml`) builds exe + installer on every push/PR. To release: bump the version in both files (see above), **add a `## vx.y.z` section to `CHANGELOG.md`** (user-facing wording — it becomes the GitHub release notes; the release workflow fails without it), commit, then

```powershell
git tag v1.x.y && git push origin main --tags
```

`release.yml` verifies the tag matches both version fields, builds the installer, and creates a GitHub Release with the setup exe attached. The in-app update checker (`UpdateChecker.cs`) compares the latest release tag against the assembly version — tags must always be `v` + the csproj `<Version>`.

## Project Structure

- `Program.cs` — entry point, crash handling, publish loop (snapshot → UI → all sinks)
- `TrayApp.cs` — tray icon (WinForms `NotifyIcon`; WPF has none), WPF context menu, pause/resume
- `Ui/` — WPF UI with the .NET Fluent theme (`Application.ThemeMode`): `MainWindow.xaml`
  (icon sidebar, save footer, tray-anchored placement), one class per page
  (`DashboardPage`, `OutputsPage`, `SensorsPage`, `SettingsPage.xaml`, `AboutPage`),
  `Styles.xaml` (card, toggle switch, text styles), `Format` (UI number formatting, CPU/GPU
  name shortening), `InputRules` (host/port/baud rules, COM port list), `Lang` (en/de strings)
- `SensorService.cs` — LibreHardwareMonitor wrapper, snapshot building
- `Metrics.cs` — transport-neutral metrics model, `HostInfo`, `MetricTable` (one row per
  scalar metric — drives MQTT subtopics AND HA discovery; drives are special-cased)
- `Sinks/` — `IMetricsSink` (the project's only interface) + `MqttSink` (connection,
  availability/LWT, subtopics, HA discovery sync), `UdpSink` (JSON datagram per snapshot,
  ARP check of local targets for the card), `TcpSink` (line-delimited JSON server),
  `SerialSink` (line-delimited JSON on a COM port, lazy open/reopen); `SinkStatus` reports
  each sink's state to its Outputs card and logs failures once
- `Ui/DemoMode.cs` — `--demo` (interactive demo window), `--screenshots` (README images, CI)
  and `--scenarios` (extra UI states); renders via `RenderTargetBitmap`, so `--scale` works
  on any screen size
- `tests/PcMqttMonitor.Tests/` — xUnit tests; the app grants `InternalsVisibleTo`
- `Config.cs` — JSON config in sections: `general`, `mqtt`, `udp`, `tcp`, `sensors`
  (v2 schema, breaking vs. 1.x — no migration by design)
- `UpdateChecker.cs` — daily GitHub-releases check, notify-only (tray balloon + menu item)
- `app.ico` — application icon (16/32/48/256px signal bars design)
- `app.manifest` — requires `requireAdministrator` (needed for LHM hardware access);
  per-monitor DPI awareness
- `installer.iss` — Inno Setup installer script
- `tools/genicon/` — tool to regenerate `app.ico` (run with `dotnet run --project tools/genicon`)

## Key Decisions

- **Config location**: next to the exe (`AppContext.BaseDirectory`) — also under Program Files, which works because the app always runs elevated. Survives `dotnet build` and installer upgrades; only lost on `dotnet clean`.
- **Install location**: `C:\Program Files\PcMqttMonitor` (`{autopf}`, `UsePreviousAppDir=no`). Not LocalAppData — an exe that runs elevated must not be writable by non-admin processes.
- **Elevation is genuinely required**: without admin, LHM cannot load its Ring0 driver — CPU temp and package power silently read 0 and voltages show VID defaults instead of measurements (verified empirically). GPU (NVAPI), RAM, drives and network work unelevated, but CPU metrics are core functionality.
- **Autostart**: Windows Task Scheduler (`schtasks /RL HIGHEST /SC ONLOGON`) — not the registry Run key, because `requireAdministrator` apps are silently skipped there.
- **Settings apply**: one Save for all pages. Output changes rebuild the sinks (`Program.RequestSinksReload`), the theme switches live; only a language change restarts the app (`Program.Restart`).
- **UI framework**: WPF with the built-in Fluent theme (Windows 11 controls, Mica, live light/dark), not WinUI 3: WPF ships with .NET (single-file exe, unchanged installer), runs elevated without restrictions and renders off-screen for CI screenshots. `IncludeNativeLibrariesForSelfExtract` puts WPF's native DLLs into the single-file exe; without it the installed exe crashes at startup. The WinForms namespaces are not global usings, so WPF and WinForms type names never clash.
- **Hardware crashes**: LibreHardwareMonitor background threads can crash on driver updates or at boot. The `AppDomain.UnhandledException` handler detects these and auto-restarts. All crashes are logged to `crash.log` next to the exe.
- **Startup delay**: if system uptime < 30s, wait for storage drivers to settle before opening sensors.
