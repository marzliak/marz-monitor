<div align="center">

<img src="docs/assets/icon-1024.png" width="96" alt="Marz Monitor icon">

# Marz Monitor

**One tray app for every temperature, every fan and every little screen on your Windows PC.**

CPU · GPU · RAM · storage · network · NVIDIA fans · ASUS motherboard fans · ROG Ryujin II pump, fans and LCD · SteelSeries Apex OLED

[**Download**](https://github.com/marzliak/marz-monitor/releases/latest) · [Screenshots](#screenshots) · [Features](#features) · [Hardware](#supported-hardware) · [Build](#build-from-source)

![Marz Monitor](screenshots/hero.png)

</div>

---

## Why

A gaming PC usually ends up with four or five vendor apps running in the background just to show a temperature or spin a fan: Armoury Crate, Fan Xpert, the NVIDIA tools, SteelSeries GG, an overlay… Each one has its own UI, its own updater and its own idea of who controls the fans.

Marz Monitor puts all of it in **one tray icon**:

- 🌡 **See everything.** Every sensor your hardware exposes, live, in a popup next to the clock or in a full Details window.
- 🌀 **Control every fan from one place.** GPU, motherboard headers and the AIO cooler, each with its own curve or fixed speed.
- 💧 **Drive the ROG Ryujin II directly over USB, without Armoury Crate.** Pump, radiator fans, pump-head fan and **the LCD**.
- ⌨️ **Put your stats on the SteelSeries Apex OLED.** Up to 20 screens, switched with the keyboard's volume wheel.
- 🎮 **Game mode.** One click and everything goes to 100 %, then back to your curves. It can also turn on by itself when a game starts.
- 🛡 **Safe by design.** Every fan feature has hard safety floors and gives control back to the hardware when the app exits.

## Screenshots

<table>
<tr>
<td width="33%"><img src="screenshots/popup.png"><br><sub><b>Tray popup.</b> Load, power, storage, network, temperature cards, GPU / motherboard / cooler fans</sub></td>
<td width="33%"><img src="screenshots/game-mode.png"><br><sub><b>Game mode.</b> One click: everything to 100 %; click again to go back</sub></td>
<td width="33%"><img src="screenshots/popup-light.png"><br><sub><b>Light theme</b></sub></td>
</tr>
</table>

**Thermal control.** Every fan of every device in one strip, a curve editor with a live "now" line, the previous curve dashed, and the protection zone hatched:

![Ryujin II radiator curve](screenshots/thermal-ryujin-curve.png)

<table>
<tr>
<td width="50%"><img src="screenshots/thermal-board-curve.png"><br><sub><b>Motherboard headers.</b> The 4-point curve stored in the board's fan chip, with its temperature source</sub></td>
<td width="50%"><img src="screenshots/thermal-board-fan-off.png"><br><sub><b>Turn a case fan off</b>, with an automatic safety curve (0 % below 80 °C, 100 % from 85 °C)</sub></td>
</tr>
<tr>
<td width="50%"><img src="screenshots/ryujin-lcd-editor.png"><br><sub><b>ROG Ryujin II LCD.</b> Up to 4 sensors per screen, any label and unit, screens rotating on a timer</sub></td>
<td width="50%"><img src="screenshots/steelseries-oled-editor.png"><br><sub><b>SteelSeries Apex OLED.</b> Up to 20 screens of 2 lines, with a live preview</sub></td>
</tr>
</table>

**Details window.** Every device and every sensor, with gauges, history charts and per-core grids:

![Details CPU](screenshots/details-cpu.png)

<table>
<tr>
<td width="25%"><img src="screenshots/details-gpu.png"><br><sub>GPU, with fan presets</sub></td>
<td width="25%"><img src="screenshots/details-motherboard.png"><br><sub>Motherboard</sub></td>
<td width="25%"><img src="screenshots/details-storage.png"><br><sub>NVMe / SSD</sub></td>
<td width="25%"><img src="screenshots/details-network.png"><br><sub>Network</sub></td>
</tr>
<tr>
<td width="25%"><img src="screenshots/details-search.png"><br><sub>Search across all sensors</sub></td>
<td width="25%"><img src="screenshots/details-light.png"><br><sub>Light theme</sub></td>
<td width="25%"><img src="screenshots/quick-card-rename.png"><br><sub>Right-click any fan: curve / manual / off / rename</sub></td>
<td width="25%"><img src="screenshots/settings-fan-names.png"><br><sub>Settings: name every fan header your way</sub></td>
</tr>
</table>

**Six color palettes.** The tray icon can show the hottest temperature and change color near the limit:

![Palettes](screenshots/palettes.png)

<table><tr>
<td width="50%"><img src="screenshots/tray-icon.png"><br><sub>Tray icon: temperature in 4 sizes, alert colors, light/dark taskbar</sub></td>
<td width="50%"><img src="screenshots/about.png"><br><sub>About: version, live system facts, licenses</sub></td>
</tr></table>

<sub>Screenshots are rendered by the app itself (`MonitorTray.exe --preview`) from a real sensor dump of the author's PC.</sub>

## Features

### Monitoring
- **Popup next to the clock.**
  - CPU / GPU / memory load and power.
  - Per-drive storage usage with read/write rates and sparklines.
  - A mirrored download/upload chart, with 1 min to 2 h of history.
  - A strip of temperature cards: you choose which ones and in what order.
- **Details window.** Every sensor LibreHardwareMonitor finds (300+ on a typical desktop):
  - gauges and scrolling charts;
  - per-core temperature/clock/voltage grids;
  - accent-insensitive search;
  - filters for zeroed and limit sensors;
  - min/max per session.
- **Tray icon.** Optionally shows the hottest temperature (or rotates through the ones you pick), colored by how close it is to its limit. The tooltip summarizes CPU, GPU, memory, disk and network.
- **Network.** Bytes or bits, total vs local network vs internet traffic, per adapter.
- **Low footprint.** ~110–130 MB private memory in the tray, under 1 % CPU at idle.

### Fan control
| Device | What you can do |
|---|---|
| **NVIDIA GPU** | Auto or fixed presets (30 / 50 / 75 / 100 %). Driver range, temperature and thermal-throttle guards, per-fan verification. Back to automatic when the app exits; the app refuses to close if it cannot confirm that |
| **ASUS motherboard** | Silent / Standard / Turbo / Full / your own curves, per header. Each header can be put on a fixed % or turned off (experimental, behind a setting), and renamed (original header name stays in the tooltip). Your choices are saved and re-applied at start. Curves are stored in the board's fan chip, so they keep working even if the app is closed |
| **ROG Ryujin II** | Pump, radiator fans and pump-head fan: Silent / Standard / Turbo / Full, custom curves up to 8 points, fixed %, and a choice of curve source (CPU, GPU, liquid…). **Auto Tuning** measures your radiator fans' rpm table |
| **Game mode** | Everything to 100 % (or 70 / 85 %, configurable), per device. Can turn on automatically from a list of `.exe` names, and survives restarts |

Safety rules, always on:
- the pump never goes below 30 %;
- liquid ≥ 50 °C forces the cooler to 100 %;
- a lost temperature source falls back to 70 %;
- the cooler is left at ≥ 60 % on exit;
- writes from another program are detected and reported;
- the CPU fan and pump headers can never be turned off;
- a fan that was turned off comes back on by itself above 85 °C CPU / 80 °C GPU.

### Screens
- **ROG Ryujin II LCD.** The built-in hardware-monitor layout with 1–4 lines of any sensor, your own labels and units. Pages rotate on a timer, with no Armoury Crate or AIDA64 needed.
- **SteelSeries Apex OLED.** Up to 20 screens of 2 × 20 characters with any sensor, through SteelSeries GG. The volume wheel flips or pins screens, and mute is compensated.

## Supported hardware

| Area | Works with | Notes |
|---|---|---|
| Monitoring | Any Windows 10/11 x64 PC | Through [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor): CPUs, GPUs, motherboard Super I/O, RAM, NVMe/SSD, network. Admin rights are needed for CPU/board sensors; the installer sets this up once |
| GPU fans | NVIDIA (NVML / NVAPI) | Tested on an RTX 3090 |
| Motherboard fans | ASUS boards with Armoury Crate / Fan Xpert 4 | Tested on a ROG MAXIMUS Z790 HERO. Modes are applied through the Armoury Crate fan service, which must be installed |
| AIO cooler | **ROG Ryujin II** (`0B05:1988`) | Direct USB HID, no Armoury Crate. Tested on a Ryujin II 360. Protocol: [asus-rog-protocol-notes](https://github.com/marzliak/asus-rog-protocol-notes) |
| Keyboard OLED | SteelSeries Apex keyboards with an OLED screen (128 × 40) | Through SteelSeries GG (GameSense), local only |

Other devices are welcome: open an issue with your hardware, and a sensor dump from `MonitorTray.exe --read-test dump.json` if you can.

## Install

1. Download `MarzMonitor-<version>-win-x64.zip` from [Releases](https://github.com/marzliak/marz-monitor/releases/latest).
2. Extract it and run **`INSTALL.cmd`**. Don't use "Run as administrator"; the installer asks once through UAC.
3. Click the icon next to the clock. Drag the popup by its header; double-click the header to snap it back.

What the installer does:
- copies the app to `C:\Program Files\MonitorTray`;
- creates a logon task that starts it elevated in your session, so there is no UAC prompt on every boot;
- adds Start Menu and desktop shortcuts and an uninstall entry.

It is self-contained: .NET 10 is included and there is nothing else to install. Details: [docs/INSTALL.md](docs/INSTALL.md).

> **Ryujin II users:** Armoury Crate also writes to the cooler every few seconds, and two programs writing to it fight each other. Detach the Ryujin from Armoury Crate first. The [methodology guide](https://github.com/marzliak/asus-rog-protocol-notes/blob/main/docs/methodology.md#2-replay-safely) explains how to do it without uninstalling Armoury Crate.

## Build from source

Requires the .NET SDK 10.0.400 (pinned in `global.json`) and Python 3 for packaging.

```powershell
dotnet build src/MonitorTray.App -c Release -warnaserror
dotnet test tests/MonitorTray.Core.Tests -c Release        # 398 tests
MonitorTray.exe --preview out\       # render every screen to PNG from simulated data
MonitorTray.exe --simulate           # run the UI without hardware or admin
MonitorTray.exe --read-test x.json   # one read-only sensor dump, no UI
python tools/build-package.py        # versioned ZIP
```

The app is WPF built in code (no XAML) on .NET 10. Layout:

| Folder | Contents |
|---|---|
| `src/MonitorTray.Core` | Model, normalization, history, settings, fan logic (no WPF) |
| `src/MonitorTray.Sensors` | LibreHardwareMonitor, PDH, network, simulated source |
| `src/MonitorTray.Gpu` | NVIDIA fan control and safety |
| `src/MonitorTray.Ryujin` | ROG Ryujin II USB HID: sensors, fans, LCD |
| `src/MonitorTray.Keyboard` | SteelSeries GameSense OLED and the volume wheel |
| `src/MonitorTray.App` | UI, tray, windows, preview renderer |

More in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). The original design spec is in [`kit/`](kit/).

## FAQ

**Does it replace Armoury Crate?**
For the Ryujin II, yes: the app talks to the cooler directly. For motherboard fans, modes are still applied through the Armoury Crate fan service; direct chip control is documented in [asus-rog-protocol-notes](https://github.com/marzliak/asus-rog-protocol-notes) and is planned. Aura RGB is out of scope (use [OpenRGB](https://openrgb.org)).

**Is it safe to let an app control my pump?**
The cooler has no watchdog: if any software dies, the cooler keeps the last speed. That is why Marz Monitor never lets the pump go below 30 %, goes to 100 % above 50 °C liquid, and leaves the cooler at ≥ 60 % on exit. Still, it is fan-control software: use it at your own risk.

**Why does it need admin?**
LibreHardwareMonitor needs a kernel driver to read CPU and motherboard sensors. The installer registers a logon task, so the app starts elevated without a UAC prompt.

## License

[MIT](LICENSE) © 2026 Leandro Marzliak.

Uses LibreHardwareMonitorLib (MPL-2.0), H.NotifyIcon (MIT), CommunityToolkit.Mvvm (MIT), Barlow fonts (SIL OFL 1.1) and Lucide icons (ISC).

Not affiliated with ASUS, NVIDIA or SteelSeries. ROG, Ryujin, Armoury Crate, Fan Xpert, GeForce, Apex and GameSense are trademarks of their owners, used only to name the hardware this app works with.
