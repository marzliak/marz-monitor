# Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versioning: **0.1.x** series (each delivered package bumps the last number).

## [0.1.37] - 2026-11-08
### Changed
- Lower CPU use:
  - Network adapter list cached for 10 s (enumerating adapters cost ~6 ms per tick; only the byte counters are read each tick).
  - LibreHardwareMonitor read at most once per second, even at a 0.5 s refresh.
  - With the popup and Details closed the sensor loop runs at most once per second (tray tooltip/icon, keyboard screens and fan safety don't need 2 Hz).

## [0.1.36] - 2026-11-08
### Fixed
- Sensor loop running non-stop (~1.6 cores of CPU, values and charts jumping): every keyboard-screen send and cooler status update re-set `KeepHardware`, which woke the sensor loop at once; the new reading triggered the next send, and so on. The loop is now woken only when `KeepHardware` turns on.

## [0.1.35] - 2026-11-08
### Fixed
- Saved board fans in OFF were not turned off again when the app started hidden in the tray: the CPU temperature only reached the popup logic while the popup was open. It is now fed on every reading, and sensors stay on until the saved OFF fans are restored.

## [0.1.34] - 2026-11-08
### Changed
- **English only.** The whole app (popup, Settings, Details, Thermal control, cooler LCD, keyboard screens, About, tray menu/tooltip, quick card, installer), logs, code, tests and all documentation (README, CHANGELOG, docs, kit) are now in English. Numbers and dates use en-US. The package has `INSTALL.cmd` and `README.md`.
- Saved settings keep working: old Portuguese ids are still read and written back in English (theme escuro→dark, palette eletrico→electric, scale independente→independent, board mode curvas→curves, LCD slot names Agua→Water, etc.).
- Board fans show their original header names by default (CPU_FAN, CHA_FAN1…CHA_FAN4, W_PUMP+, AIO_PUMP), derived from the Armoury Crate connector, never from the board model. The old built-in nicknames (Exaustor/Front top/Front down/Rear) were removed; saved values identical to those defaults are dropped.
### Added
- Rename any board fan or RYUJIN part: Settings › FAN NAMES (empty = original name) or **Rename** in the quick card (Enter saves, Esc cancels). A custom name is the main label; the original header stays in the tooltip. Long names are trimmed with an ellipsis.
### Fixed
- `AcFanNames` was missing from Settings equality, so a rename-only change could fail to save.

## [0.1.33] - 2026-11-07
### Fixed
- Fan choices are now saved and restored when the app opens: motherboard mode (CURVES/SILENT/STD/TURBO/FULL), motherboard fans in OFF or MANUAL, and the GPU preset (A/30/50/75/100). Before, the app always opened with the motherboard in CURVES, the fans on and the GPU on automatic.
- Leaving game mode returns to your choices (before: motherboard in CURVES and GPU on automatic).
### Notes
- On quitting the app everything still goes back to safe (GPU automatic, motherboard on the User curves); only the choice is kept (`state\fan-choices.json`). OFF is only reapplied with the "TURN OFF MOTHERBOARD FAN" option on and after the first CPU reading. Protections and game mode do not change the saved choice.

## [0.1.32] - 2026-11-07
### Fixed
- RYUJIN built-in fan stuck at 100%: game mode put it on the FULL preset and, when game mode turned itself off (GPU/motherboard changed externally), the cooler did not go back to what it was before. Now any exit from game mode returns each part of the cooler that is still as game mode left it.
- On opening, a built-in fan in FULL outside game mode goes back to STD (a user's own curve is kept).
### Changed
- CURVES / SILENT / STD / TURBO / FULL of the WC RYUJIN II now also apply to the built-in fan (before only pump and radiator); CURVES restores the built-in fan's own config, if there is one.

## [0.1.31] - 2026-11-07 (test, branch popup-v2)
### Added
- **Quick card** (right-click on a fan card in the popup): CURVE / MANUAL / OFF, % slider (writes on release) and 30/50/75/100 shortcuts, "Edit curve…" link that opens Thermal control on that fan.
- **MANUAL per motherboard fan**: flat curve at the requested % up to 80 °C and 100% from 85 °C, written to the chip through the same path as OFF/FULL (User curve saved; restored when Marz quits). Minimum 20%. Card with a highlighted outline. FULL and game mode take it out of MANUAL.
- **MANUAL on the RYUJIN** via the quick card: pump (min. 30%), radiator (3 fans together) and built-in fan at a fixed %.

## [0.1.30] - 2026-11-07 (test, branch popup-v2)
### Added
- Tray icon: choose which temperatures to show (ICON TEMPERATURE; none = the hottest). With more than one, they alternate every 2/3/5/10 s (SWITCH EVERY). The icon tooltip says which one is showing. The alert color still considers all cards.

## [0.1.29] - 2026-11-07 (test, branch popup-v2)
### Changed
- Tray icon: new XS size (58% of the height), default.
- Thermal control: "screen n/m on the LCD" only shows on the LCD tab.
### Added
- Thermal control (RYUJIN): RESTORE button resets the actuator curve to the default (STD points); only written on Apply.

## [0.1.28] - 2026-11-07 (test, branch popup-v2)
### Fixed
- Light outline around the popup: the window had 6 transparent px outside the frame, where the acrylic showed as a border. Margin set to zero.
### Changed
- The RYUJIN curve follows the **~5 s average** of the source temperature (before: instantaneous reading every 1 s; CPU Package swings 10–20 °C in spikes). The NOW line and the "% now" in Thermal control use the same average.
- Thermal control: protections collapsed (title with an info icon; click opens the list, tooltip shows the rules); no "owner:"/"driver".
- Radiator Auto Tuning: strip over the chart with step (1/11), progress bar, remaining time and CANCEL; popup shows "auto tuning n/11".

## [0.1.27] - 2026-11-07 (test, branch popup-v2)
### Added
- New **Thermal control** window: strip with all fans, GPU/MOTHERBOARD/RYUJIN II tabs, curve chart with explicit source, NOW line, previous curve, point editing on the RYUJIN (up to 8) and on the motherboard in CURVES (the chip's 4 points), Apply/Discard; cooler LCD in its own tab.
### Fixed
- Light outline around the popup in the real app: the borderless frame still drew a 1 px border in the background color, and at fractional DPI the seam came out with lower alpha (light line over the blur). Now only the background; the window region follows the frame's real rectangle and the DWM border (Windows 11) has no color.
- WC CURVES in the popup opened Thermal control: it now applies the cooler's own config from before the preset (saved in `ryujin-curves.json`, survives restarts) or, without one, the default curves (STD points as its own curve).
- Motherboard fan card with "TURN OFF MOTHERBOARD FAN" off: read-only (no hand cursor or hint). With it on, a click blocked by the protection shows "did not turn off" with the reason; turning the option off turns the stopped fans back on.
### Changed
- No driver / AC / Marz tags in the titles.
- Motherboard: all modes visible by default (the choice remains in Settings).
- GPU: rpm next to each fan's % when > 0.
- Tray icon with temperature: smaller number by default and a FONT SIZE option (S/M/L).

## [0.1.26] - 2026-11-07 (test, branch popup-v2)
### Changed
- **Popup redesigned** at the same size: no light outline; strip of temperature cards (choice and order in Settings; alert color from the protection limits); GPU with a compact FAN SPEED and fan/A/% on the right; motherboard and WC with CURVES/SILENT/STD/TURBO/FULL and single-size fan cards (choice in Settings); rpm only when measured; network with ↓/↑ on its own line.
### Added
- Alert strip at the top (game mode, fan turned back on by protection, GPU/cooler protections).
- Tray icon with the highest temperature and/or alert color (optional).
- Automatic game mode from a list of executables.
- Clicking a temperature card opens Details on that sensor; tooltip with the session min./max.
- Tag showing who controls each section (driver / AC / Marz).
- Turn off a motherboard fan from its card (behind the "TURN OFF MOTHERBOARD FAN" option, off by default; protection curve 0% < 80 °C / 100% ≥ 85 °C; turns back on by temperature, FULL and game mode; not persisted).

## [0.1.25] - 2026-11-02
### Added
- **ROG RYUJIN II water cooler** (USB HID, without Armoury Crate): reading of water, pump, built-in fan and the 3 radiator fans; control of pump/radiator/built-in fan with SILENT/STD/TURBO/FULL presets, curve or fixed %, configurable curve source and radiator Auto Tuning. Protections: pump ≥ 30 %, water ≥ 50 °C puts everything at 100 %, no source reading uses 70 %, on close leaves ≥ 60 %, stops and warns if another program writes to the cooler.
- **Cooler LCD screen**: screens with 1 to 4 readings of any sensor, label and unit, rotating on a timer (off by default).
- **Thermal control** window (thermometer button in the header and tray item): temperature and speed cards (cooler, motherboard, GPU), motherboard modes, % × rpm curves.
- Popup: **WC RYUJIN II** row (presets + water + pump/radiator/built-in fan rpm).
- **Configurable game mode** in Settings (GPU, motherboard with all fans, pump, radiator, built-in fan; GPU and cooler %); default everything at 100 %. Persists across restarts. `MonitorTray.exe --game` toggles it.
### Changed
- Popup: GPU, MOTHERBOARD and WC in the same pattern; motherboard and WC modes on the title row; network without the numbers row (current value highlighted in the chart).

## [0.1.24] - 2026-11-02
### Fixed
- Tray icon did not stay visible even when checked: the icon Guid was the same as the installed app's and the test builds', and Windows lost the choice. Now the Guid is per exe path and, the first time, the icon already shows up visible on the taskbar.

## [0.1.23] - 2026-11-02
### Changed
- STORAGE: per-drive read and write labeled `R` and `W` (still blank when idle).

## [0.1.22] - 2026-11-02
### Changed
- Installer, README and INSTALL updated: motherboard fans via Armoury Crate, game mode, where the curve backup is kept.

## [0.1.21] - 2026-11-02
### Added
- **Game mode** button (controller) in the header, next to the 1s: all fans at 100% (GPU + motherboard via Armoury Crate FULL); click again to go back to automatic/curves.
- MOTHERBOARD: motherboard temperatures (mobo, chipset, VRM) on the modes row; chipset ≥ 85 °C highlighted.

## [0.1.20] - 2026-11-02
### Changed
- STORAGE: R/W on the same line as the % (blank when idle) and a micro read/write chart on each drive.
- Network: chart labels show "current | scale" at the top (download) and bottom (upload).

## [0.1.19] - 2026-11-02
### Changed
- STORAGE: small label on the same line as the drives; each drive shows %, read and write (R/W per drive, LogicalDisk counter). Total R/W line removed (total in the STORAGE tooltip).

## [0.1.18] - 2026-11-02
### Changed
- MOTHERBOARD: RPM in cells with each fan's name (Exhaust, Front top, Front down, Rear), readable; names in `settings.json` (`acFanNames`).

## [0.1.17] - 2026-11-02
### Changed
- BOARD row renamed to MOTHERBOARD; RPM of the mode fans next to the buttons (compact; names in the tooltip).

## [0.1.16] - 2026-11-02
### Fixed
- BOARD row: the clicked button lights up immediately (with "applying…" while Armoury Crate works); if it fails, it goes back to the previous mode. FULL applies all fans together (~1 s instead of ~6 s).

## [0.1.15] - 2026-11-02
### Added
- **Motherboard fan modes via Armoury Crate** (AsusFanControlService COM; Marz does not write to the chip): **BOARD** row in the popup with CURVES (your User profile), SILENT, STD, TURBO and FULL (100%).
- Settings › **MOTHERBOARD MODES** (which buttons appear) and **FANS IN MODES** (which outputs are included; default Chassis 1–4).
- Safety: before FULL the User curves are saved in `state\ac-user-curves.json` and restored (with verification) when going back to CURVES, on quit, and on app start if it was left pending. The Armoury Crate startup profile is not changed.
- `--ac-test <log> "<fan>"`: bench test (admin).

## [0.1.14] - 2026-11-01
### Changed
- Memory: workstation GC without concurrent collection, `ConserveMemory=7`, `RetainVM=false`, no TieredPGO.
- Tray only (popup, Details and keyboard editor closed): after 10 s compacts the heap and returns the pages to Windows; repeats every 5 min.

## [0.1.13] - 2026-11-01
### Changed
- Settings › network chart **HISTORY**: 1m, 5m, 10m, 30m, 1h, 2h (max. 2 h). Stores 1 value per second (peak of the ticks); long windows show up to 120 points with the **peak** of each interval (2 h = 1 point per minute), so short spikes do not disappear. Old values (30/120 s) go back to 1m. The chart tooltip shows the window.

## [0.1.12] - 2026-11-01
### Added
- Settings › **NETWORK UNIT**: MB/s (bytes, base 1024) or **Mbps** (bits, base 1000, like speedtest/ISP). Applies to the popup, chart scale (round ceiling in bits), tray tooltip, Details and the keyboard's computed network values. Saved in `settings.json` (`netUnit`).
### Fixed
- The fans mutex test crashed the test runner when the installed app was open (test count varied).

## [0.1.11] - 2026-11-01
First version that fully replaces GPU by MarzOps.
### Added
- Installer: **Replace GPU by MarzOps** option (only appears when it is installed/running): closes it through its exit signal and disables its logon task (reversible). Silent: on by default, `-KeepGpuApp` to keep it.
- Installer/README: description with keyboard and network; table with `keyboard.json` and `keyboard-status.json`.

## [0.1.10] - 2026-11-01
### Changed
- Details: **Network in a single tab**. Inside it, cards and sections by traffic type (Total, Local network, Internet) and then one section per adapter. Each card chooses Download/Upload (▾, saved).

## [0.1.9] - 2026-11-01
### Fixed
- App exit no longer logs `ObjectDisposedException` (queued snapshots tried to update the already disposed icon).

## [0.1.8] - 2026-11-01
### Added
- Details › Network: **Network traffic** device with **total**, **local network** and **internet** download/upload (classification by address: private, link-local, CGNAT/Tailscale, multicast and IPv6 ULA count as local). Source: kernel ETW (TCP/UDP, read-only, requires admin). On the Rate card, the ▾ selector chooses which one to show; the choice is saved.

## [0.1.7] - 2026-11-01
### Added
- **Keyboard (SteelSeries Apex OLED)** ported from GPU by MarzOps: up to 20 screens of 2 lines × 20 columns sent through SteelSeries GG (GameSense), timed rotation, scroll wheel (1 click = next screen, 2 clicks = pin/unpin; Raw Input only on the Apex Consumer Control), compensation for the mute toggled by the wheel click.
- **Keyboard screens** window: create, duplicate, remove and reorder screens; time on screen; GG icon; per line: sensor (generic computed values or any LHM sensor), prefix, suffix, decimal places; live preview of what is on the keyboard. Saved in `%APPDATA%\MonitorTray\keyboard.json`.
- KEYBOARD row in the popup (current screen, pin, next, edit) and an item in the tray menu.
- On first run, the screens are imported from the GPU by MarzOps profile (`profile-rodinha.json`).
- On quit, the OLED is released (GG goes back to its own screen) and the wheel is released.

## [0.1.6] - 2026-11-01
### Changed
- New name: **Marz Monitor** (popup, About, tooltip, shortcuts, Installed apps, package `MarzMonitor-<version>-win-x64.zip`). Internal identifiers (MonitorTray.exe, folder, tasks, signals, %APPDATA%\MonitorTray) kept so it updates in place without losing anything; old shortcuts are removed.

## [0.1.5] - 2026-11-01
### Added
- Tray tooltip with CPU (% and °C), memory, GPU (% and °C, fan preset when manual), disk (R/W MB/s) and network. Works with the popup closed (GPU via NVML).

## [0.1.4] - 2026-11-01
### Changed
- Tray icon: fixed pulse line (white on a dark taskbar, dark on a light one, switches with the Windows theme). Network speed remains in the tooltip.

## [0.1.3] - 2026-11-01
### Fixed
- Installer: the ACL was applied file by file and left the exe unreadable ("null-valued expression" failure in the version check). Now only the folder gets the ACL and the files inherit it. Errors in the elevated step report the line.

## [0.1.2] - 2026-11-01
### Added
- Silent install: `Installer.ps1 -Silent [-NoDesktop] [-NoStartup] [-NoOpen]` (no windows; same safe flow and UAC).

## [0.1.1] - 2026-11-01
### Added
- Details cards with a selector (▾): Average, Maximum or any sensor of the type — e.g. CPU Clock per P/E core or the average of all. The list comes from the hardware; the choice is saved in `settings.json` (`cardPicks`).

## [0.1.0] - 2026-11-01
### Added
- Tray popup: CPU/GPU/MEM, STORAGE (R/W + per-drive usage with hardware in the tooltip), network with a mirrored chart, GPU row (short name, core/hot/vram temperatures, fan icon with mode) and FAN SPEED (A/30/50/75/100).
- Details window with dynamic sensor discovery (LibreHardwareMonitor), cards, per-core grid, tiles, search and filters.
- Fan control ported from GPU by MarzOps with all protections; presets also in Details.
- Charts with continuous scrolling and a monotonic curve; numbers change directly; nothing drops to zero between readings.
- Settings: theme, 6 palettes, refresh rate (click the label in the header), history, opacity, start with Windows, start hidden, close hides to tray, pin.
- Draggable popup and Details.
- About window (version, build, system, credits, copy info).
- Graphical installer: verifies SHA256, closes the running instance through the exit signal (fans to automatic), UAC, backup/rollback, elevated tasks for the account, shortcuts, Installed apps entry and uninstaller.
