# 01 · Product

## Vision
A modern "DU Meter": discreet, always at hand in the system tray, showing at a glance what matters (CPU, GPU, memory, disk, network), and with a details window that exposes **every** sensor the hardware offers, with live charts.

## User
An enthusiast/power user on Windows, with high-performance hardware (e.g. i9-14900K + RTX 3090 + 5 NVMe), who wants to keep an eye on temperatures, power and traffic without opening a giant HWiNFO.

## Surfaces
| Surface | When | Size |
|---|---|---|
| Tray icon | always | 16×16 (rendered dynamically on every tick) |
| Popup | left click on the icon | 344 px wide, height fits the content (~232 px) |
| Popup › Settings | gear in the popup | same window, content swapped |
| Details window | "Details ↗" button in the popup or menu item | 960×640 default, resizable, draggable, maximizable |

## Scope (v1)
- Summary: CPU % + °C + W; GPU % + °C + W; memory GB used/total + %; disk read/write MB/s; network ↓/↑ with an auto-scaling mirrored chart
- Details: all discovered devices and sensors, grouped by type, with cards, gauges, sparklines, bars and a per-core grid; search; "Zeros" and "Limits" filters
- Settings: theme (light/dark/system), palette (6), opacity, refresh (0.5/1/2 s), network chart history (30/60/120 s), network scale (separate/shared), start with Windows, close on focus loss
- Persistence of the settings and of the Details window position/size

## Out of scope (v1)
- Alerts, notifications, thresholds, "system health" (the data does not support that kind of conclusion; see `05-sensors.md`)
- Fan/RGB control
- Logging to disk/CSV export (v2 candidate)
- In-game overlay
- Other OSes

## Principles
1. **Glanceable**: the popup answers "is everything OK?" in < 1 s of reading.
2. **Adaptive**: zero assumptions about the hardware.
3. **Honest**: never fabricates, interpolates or labels data beyond what the provider reports.
4. **Lightweight**: invisible in Task Manager.
