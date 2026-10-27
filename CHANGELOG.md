# Changelog

User-facing changes per release. The release workflow publishes the matching
section as the GitHub release notes — every tagged version needs a section here.

## v2.12.1 — 2026-10-27

- The installer shrinks from about 50 MB to about 3 MB: the app now uses the
  .NET 10 Desktop Runtime installed on the PC instead of bringing its own.
  If it is missing, setup downloads it from Microsoft (about 60 MB) and
  installs it before the app, also on updates.

## v2.12.0 — 2026-10-27

- **Idle time sensor**: seconds since the last keyboard or mouse input on the
  PC, published as `system/idle` and as a Home Assistant entity "Idle Time".
  Useful for automations such as "PC in use" (idle below 60 s) or "nobody at
  the PC for 10 minutes". Shown in the dashboard's System card and can be
  turned off on the Sensors page.
- **Live value in the tray icon**: Settings → Tray icon shows CPU load, CPU
  or GPU temperature, GPU load or RAM load as a number instead of the logo
  (temperatures turn red from 80 °C), or CPU and GPU as two bars: load bars,
  or temperature bars from 30 to 100 °C in green, yellow (from 60 °C) and red
  (from 80 °C). Black or white to match the taskbar; the logo with the pause
  badge while paused.

## v2.11.0 — 2026-10-26

- **New UI built with WPF and the Windows 11 Fluent theme**: native Windows 11
  controls throughout (text fields, dropdowns, check boxes, buttons, scroll
  bars, tooltips, tray menu), the Mica window background and the Windows
  accent color.
- Changing the theme applies right after saving; the app no longer restarts.
  A language change still restarts it.
- Sharp on every monitor: the window follows each monitor's scaling instead of
  being stretched on a second screen with different scaling.
- Outputs: the cards look like the expandable sections of the Windows 11
  Settings app; the serial port is a dropdown of the ports present that also
  accepts a typed port.
- The tray menu is a Windows 11 menu and follows light/dark mode.
- Starting the app while it is already running opens the window of the
  running instance instead of starting a second copy that publishes the same
  data twice.

## v2.10.1 — 2026-10-26

- The serial port field on the Outputs page now matches the other inputs:
  a Windows 11 text field with a dropdown of the COM ports present right
  now (click the arrow, or press F4 / Alt+Down). A port that is not plugged
  in can still be typed.
- Every card on the Outputs page shows what the output is actually doing,
  not just what is configured: Serial "COM9 not found" or "in use by another
  program", TCP "Port 5556 is in use by another program" or the number of
  connected clients, UDP "host not found" or "not reachable", in red when it
  fails. Previously these cards showed green as soon as they were enabled.
- UDP send errors are logged once instead of every second, and the TCP
  output retries listening every cycle when its port is taken.
- For a UDP target on the local network, the card shows "No device at …"
  when no device owns the address (checked every 30 s in the background).
  Sending continues either way, so a device that comes online later
  receives data right away.
- Hosts, ports and the baud rate are checked while typing: an invalid value
  (e.g. port `9871111111` or host `192.168.2.255eeee`) turns the field red
  and blocks saving. Previously it was silently replaced by the old value
  and the save bar disappeared.
- The publish interval is chosen from a dropdown (0.5 to 60 s) instead of
  typed as text; a value set by hand in `config.json` stays selectable.
- The save bar is easier to notice (accent-tinted with a colored top line and
  a pencil icon, red for invalid input) and disappears right after saving
  instead of showing "Saved" for a moment.
- Fixed: after saving once, the Discard and Save buttons of the save bar
  could be hidden behind the "Unsaved changes" text on the next edit.
- The README screenshots are regenerated automatically for every release.

## v2.10.0 — 2026-10-26

- **Windows 11 look:** Segoe UI Variable font and the Windows 11 type sizes,
  Fluent icons in the sidebar, a Windows 11 style info bar for the pause
  notice, a save footer with Discard/Save buttons, themed dropdowns for theme
  and language, and rounded tray menus. Windows 10 falls back to Segoe UI.
- The accent color (bars, switches, check boxes, Save button) follows the
  accent color chosen in the Windows settings.
- Dashboard: fan speeds with four digits are no longer cut off, and free
  drive space above 1 TB is shown in TB.
- Outputs page: connection errors show a red status, the field labels no
  longer get cut off ("Benutzername"), and the inputs have Windows 11 borders.
- Settings: the publish interval is shown with the unit and the decimal
  separator of the app language ("1,0 s").
- An unreadable `config.json` is now moved to `config.json.bad` instead of
  being overwritten with defaults on the next save, so manual edits with a
  syntax error no longer wipe all settings.
- While the MQTT broker is unreachable, the app no longer freezes the
  dashboard and the other outputs for up to 20 seconds per attempt:
  connecting times out after 5 seconds, retries back off up to 60 seconds,
  and only the first failure is logged.
- The MQTT card on the Outputs page now says why a connection failed: no
  response from the broker, connection refused, host not found or login
  failed, instead of only "Not connected".
- MQTT and UDP host fields reject incomplete or invalid IPv4 addresses such as
  `192.168.2.411` while typing: the field turns red and the Save button is
  disabled until it is corrected.
- The save bar disappears again when edits are changed back to the saved
  values, and a new **Discard** button resets all unsaved edits.
- Home Assistant entities that disappear while the app runs (a sensor or fan
  unchecked, a drive or network adapter gone) are now removed from HA right
  away instead of lingering with their last value.
- Logging off or shutting down Windows now marks the PC offline in Home
  Assistant immediately instead of after the MQTT keep-alive timeout.
- Updating needs fewer clicks: when the app is already installed, the
  installer skips the language, options and confirmation pages, keeps the
  previous options and restarts the app automatically.
- The autostart task now also starts on battery power, keeps running when
  the power cable is unplugged and is no longer subject to Task Scheduler's
  72-hour run limit. Re-run the installer (or toggle autostart off and on)
  to update an existing task.

## v2.9.1 — 2026-09-04

- The app now asks Windows to restart it after hardware-driver crashes that
  bypass .NET crash handlers, including NVIDIA driver restarts during updates.

## v2.9.0 — 2026-08-06

- **Network topics are now keyed by adapter name** (`net/ethernet_2/…` instead
  of `net/0/…`), so an adapter going down never shifts another adapter's
  topics or Home Assistant entities. Existing HA entities migrate
  automatically via the stale-discovery cleanup; retained topics under the
  old `net/<index>` paths are no longer written.
- Config saving is now crash-safe (temp file + atomic rename) — a power loss
  mid-write can no longer truncate `config.json` and silently reset settings.
- Changing MQTT settings live no longer leaves a stale `availability = online`
  behind under the old topic root: the outgoing connection says goodbye with
  the settings it was started with.
- The autostart toggle now reverts and logs an error if registering the
  scheduled task fails, instead of pretending it succeeded.
- README: documented that the TCP stream listens on all interfaces without
  authentication.

## v2.8.0 — 2026-08-05

- **Drive type detection**: each drive reports SSD, HDD or USB
  (`drives/c/type`), shown in the dashboard tooltip as well.
- **Drives are now keyed by letter** (`drives/c/…` instead of `drives/0/…`) —
  plugging or removing a drive never shifts another drive's topics or HA
  entities.
- **Stale-discovery cleanup**: sensors that disappear between app runs
  (hardware swapped, drive removed, fan channel unchecked) are now removed
  from Home Assistant automatically and their retained topics cleared.
- Removable drives (USB sticks, card readers) are excluded from drive metrics.
- Fixed: network rate entities were missing in HA after a fresh discovery
  (first publish cycle has no rates yet).

## v2.6.0 — 2026-08-05

- **Per-channel fan monitoring**: every detected fan header (motherboard and
  GPU) appears as its own checkbox in the Sensors page, with live RPM.
  Enabled channels publish RPM and PWM under `fans/<id>/…` and as HA entities.
- Sensible defaults on first start: spinning fans and GPU fans (even at
  0 RPM — zero-RPM idle mode stays visible) are enabled once, then the choice
  is yours.
- The old single `gpu/fan` topic is gone — GPU fans live in the fans subtree.

## v2.5.1 — 2026-08-05

- The dashboard shows the full CPU/GPU model name whenever it fits; when space
  is tight it shortens in stages (marketing suffixes → generation prefix →
  brand word → vendor), covering NVIDIA, AMD and Intel Arc naming schemes.
  The tooltip always carries the full name.

## v2.5.0 — 2026-08-05

- Dashboard cards show hardware model info again: CPU and GPU model names,
  plus RAM type and speed (e.g. "DDR5-6000", read once via WMI).

## v2.4.0 — 2026-08-04

- The app is now DPI-aware: crisp rendering on scaled displays instead of
  blurry bitmap upscaling. All layout, icons and fonts follow one scale
  factor.

## v2.3.0 — 2026-08-04

- **Localization**: English and German — follows Windows by default,
  switchable in Settings. MQTT topics, payloads and HA entity names stay
  English. The installer speaks German too.
- The Outputs page scrolls when all cards are expanded.

## v2.2.1 — 2026-08-04

- The COM port dropdown shows device names next to the port.

## v2.2.0 — 2026-08-04

- **Compact dashboard**: all hardware groups as half-width cards in a
  two-column grid — everything visible without scrolling; tooltips reveal
  truncated values.
- **Per-adapter network metrics**: every active physical adapter reports
  up/down rate, IP and MAC (virtual adapters like VMware/WSL are filtered).
- Richer System card: uptime, host, OS version and motherboard.
- Discrete GPU is preferred when an iGPU is also present.
- The dashboard shows the startup phase instead of a blank page until first
  sensor data arrives.

## v2.1.3 — 2026-08-04

- Update notice in the window title bar and a badge on the About icon.

## v2.1.2 — 2026-08-04

- The update pill gets its own strip instead of floating over the cards.

## v2.1.1 — 2026-08-04

- COM port dropdown for the serial output.

## v2.1.0 — 2026-08-04

- **Serial output**: line-delimited JSON to a COM port (e.g. an ESP32 status
  display), with automatic reopen when the device is re-plugged.

## v2.0.0 — 2026-08-04

- **Complete v2 rework.**
- New UI: icon sidebar, dashboard cards with live bars and 60-second
  sparklines, light/dark/system theme, floating save panel; the window
  anchors above the tray like a flyout.
- Sink architecture: **UDP** (one JSON datagram per snapshot) and **TCP**
  (line-delimited JSON server) outputs alongside MQTT, each independently
  switchable.
- Live apply: changed output settings rebuild the sinks on the fly — no app
  restart.
- Breaking: new sectioned config schema; 1.x configs are not migrated — the
  app starts with defaults, broker settings must be re-entered once.

---

Releases before 2.0 predate this changelog.
