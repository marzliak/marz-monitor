# Architecture

```
src/
  MonitorTray.Core     net10.0, no WPF: model, formatting, normalization (category, name, memory consolidation,
                       roles/tags, primary sensor), history (RingBuffer, SensorTracker), Details view-model, Settings.
  MonitorTray.Sensors  LibreHardwareMonitor (LhmSource), PDH/network/memory (SystemSummarySource), drives (Volumes, WMI),
                       simulated mode, SensorService (own thread, publishes immutable Snapshot).
  MonitorTray.Gpu      NVIDIA fan control ported from GPU by MarzOps (NVML/NVAPI + GpuSafety) and GpuFanService.
  MonitorTray.Keyboard (namespace MonitorTray.Oled) GameSense, single OLED writer, wheel Raw Input, mute compensation, KeyboardService (screen cycling).
                       Pure logic (WheelState, MuteCorrelation, DeviceFilter, KeyboardConfig, OledComposer) lives in Core/Keyboard.
  MonitorTray.App      WPF built in code: theme/palettes, drawn controls, popup, Details, About, tray, platform.
tests/MonitorTray.Core.Tests   xUnit: formatting, scales, kit sensor rules, settings, fans (fakes).
installer/             Installer.ps1 (not elevated), Elevated.ps1 (UAC), Uninstall.ps1.
tools/build-package.py Versioned package.
kit/                   Original specification (docs, prototype, screenshots, hardware survey).
```

## Data flow
- `SensorService` runs every `refreshMs`: the cheap Summary always (PDH, memory, network); LHM only with the popup or Details open, on a pool thread (a late tick is skipped, not queued). Full scan every 30 s only with Details open; the first reading after `Reset` is discarded.
- The UI receives `Snapshot` via `Dispatcher.BeginInvoke` and updates the existing elements. The device panel is only rebuilt when the **structure** changes (sections/sensors/cards), never because of a value.
- `GpuFanService` reads NVML every 1 s on another timer, holds the manual preset (reasserts it, promotes to 100% if safety requires) and writes `fan-status.json`.

## Decisions (deviations from the kit, agreed with the user)
- .NET 10 instead of 8 (end of support). 
- No "+" marks in the corners. The popup does not close on focus loss by default (pin). Numbers without animation; bars and charts animated.
- Fan control included (outside the kit's v1 scope).
- "STORAGE" instead of "DISK"; R/W in one cell; compact logical drives.
- Primary sensor: `composite > core$ > hot spot` (the prototype picked Hot Spot on the GPU, contrary to doc 06 §4).
- Network filter interfaces (WFP/QoS/WAN Miniport) are ignored; 0 W package power without admin becomes "—".
- Popup v2 (mock 01i): no light outline; strip of temperature cards (`TempCards` roles, chosen and ordered in Settings, missing sensor = card omitted); GPU/MOTHERBOARD/WC titles without temperatures; GPU FAN SPEED with the modes button; fan cards (motherboard and cooler) chosen in Settings; network ↓↑ side by side in the bottom-left corner; scrollable Settings (max. 760 px). Turning a motherboard fan OFF from its card is behind a flag (see asus-rog-protocol-notes).
- Popup v2, round 2: network ↓↑ on its own row below the chart (nothing covers the curve); motherboard and cooler fan cards in the same 5-column grid, % in the header and rpm below; rpm only from a verified header (`BoardFanMap`: chassis), CPU/pumps show the % and "—" (rpm never derived from %). Card alerts from the protection limits (`TempLimits`: CPU 85, GPU 80, VRAM 95, liquid 50; others documented in the code), near = limit − 5 °C. Thin strip at the top (`StatusStrip`) with protections that kicked in and game mode. Clicking a temperature card opens Details on that sensor (tooltip with session min./max., `TempSession`). Optional tray icon with the highest temperature and/or alert color (keeps LHM active with the popup closed). Automatic game mode per executable (`AutoGame`, processes every 3 s; only turns off what it turned on).

## Elevation
LHM needs admin for CPU/motherboard/SPD. The installed copy, when opened without elevation (shortcut), relaunches through the `MonitorTray-<SID>` task and exits, so it always runs elevated without UAC.
