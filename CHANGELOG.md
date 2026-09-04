# Changelog

User-facing changes per release. The release workflow publishes the matching
section as the GitHub release notes — every tagged version needs a section here.

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
