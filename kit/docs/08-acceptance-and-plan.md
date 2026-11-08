# 08 · Task plan and acceptance criteria

## Phase 1: Skeleton
- [ ] Solution with the 4 projects (see 02) building; PerMonitorV2 manifest
- [ ] Single instance
- [ ] Tray icon (static), menu Open/Details/Settings/Quit
- [ ] Empty 344×232 popup with the blueprint frame, positioned as in 07 (test the taskbar at the bottom and at the top, 100% and 150% DPI, 2 monitors)
- [ ] Barlow and Barlow Condensed fonts embedded

## Phase 2: Collection
- [ ] `SimulatedProvider` reading `prototype/data/sensors.json` (arg `--simulate`)
- [ ] `LhmProvider` with recursive scan, stable IDs and hot-plug
- [ ] `PdhProvider` (CPU, disk) and network via `NetworkInterface` with real Δt
- [ ] `MemoryProvider` (GlobalMemoryStatusEx)
- [ ] `SensorService` on its own thread, skipping late ticks; immutable Snapshot
- [ ] `RingBuffer<float?>(120)` per sensor
- [ ] Normalization: category, name, Memory consolidation, role, quality, allZero
- [ ] Unit tests: Nice, Formatter (every row of table 05 §9), Primary (cases from 06 §4), Namer (generic Network, duplicates), Consolidator (slots 1 and 3 → modules 1 and 2)

## Phase 3: Summary
- [ ] Metrics row, disk, network and mirrored chart (06 §1–2) identical to `screenshots/01` and `03`
- [ ] Dynamic tray icon with ↓/↑ bars and tooltip, without leaking HICON (GDI handles stable after 1 h)

## Phase 4: Settings and theme
- [ ] Every row of 04 §B6, identical to `screenshots/02` and `04`
- [ ] Atomic persistence in `%APPDATA%\MonitorTray\settings.json`, validated against the schema; corrupted file → defaults
- [ ] ThemeService: instant theme/palette switch in every open window; "system" theme reacts to Windows
- [ ] Opacity + acrylic (Win10 and Win11), square corners on Win11
- [ ] Autostart (Run or Scheduled Task) and close on focus loss

## Phase 5: Details
- [ ] Its own window, draggable, resizable, maximizable, with remembered position/size
- [ ] Device list with primary value, sparkline and NEW tag
- [ ] Cards, gauges, sections with grid or tiles exactly as in 04 §C, identical to `screenshots/10–26`
- [ ] Search (accent-insensitive), ZEROS, LIMITS, footer with counts
- [ ] Admin banner when not elevated
- [ ] Virtualization: `VirtualizingStackPanel` in the list; the panel only renders the selected device

## Phase 6: Polish
- [ ] Resume from hibernation, DPI/monitor change and USB/NVMe hot-plug tested
- [ ] Reduced collection with the windows closed
- [ ] Single-file publish and .ico icon
- [ ] App README (requirements, admin, driver warning)

## Acceptance checklist (all mandatory)
### Visual
- [ ] Square corners and "+" marks at the 4 corners of the popup and of Details
- [ ] All numbers in tabular Barlow Condensed, with no "jumping" between ticks
- [ ] Popup 344 px wide and network chart 64 px tall
- [ ] The 6 palettes change the tray icon, title square, bars, charts, chips, segmented controls and selection
- [ ] Light and dark correct (table 03)
### Data
- [ ] No hard-coded model names (grep for "14900", "3090", "Kingston", "Corsair" and "NCT" in `src/` returns nothing)
- [ ] On the reference hardware: CPU with a per-core grid (Load, Clock, Temperature, Voltage with ≥ 9 items)
- [ ] Memory shows as 1 device with "2 modules · module 1 in slot 1, module 2 in slot 3"
- [ ] The CPU header value is CPU Package (never a Distance to TjMax)
- [ ] The CPU Load card uses CPU Total
- [ ] SSD Warning/Critical Temperature only appear with LIMITS on and with the `limit` tag
- [ ] Water In/Out/T Sensor with "—" and the `no reading` tag
- [ ] Gaps appear as breaks in the line
- [ ] No "healthy/OK/failure" text anywhere
- [ ] Plugging in a USB flash drive/SSD with SMART makes the device appear with NEW in ≤ 30 s; when removed, it disappears
- [ ] `--simulate` with the kit JSON + a sensor of an invented type ("Current" in a "Psu") renders without error, with the PSU category and the Current type
### Performance
- [ ] App CPU < 1% (popup closed), < 3% (Details open, 1 s), on an i9-14900K
- [ ] RAM < 120 MB; no growth after 1 h
- [ ] Popup opens in < 100 ms after the click
