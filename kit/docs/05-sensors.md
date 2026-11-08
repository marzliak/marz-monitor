# 05 · Sensors: sources, model and rules

The real capture from the reference PC is in `hardware-survey/` (LHM 0.9.6 + WMI/PDH, 2026-10-18, 125.9 s, 355 LHM entries). The same content, normalized, is in `prototype/data/sensors.json` (the `state` and `role` values are stored in Portuguese as captured: "values present" = values present, "partial" = partial, "unavailable" = unavailable; "reading" = reading, "reported limit, not a current value" = reported limit, not a current value, "metadado/limite SPD" = SPD metadata/limit, "accumulated counter; not a rate" = accumulated counter, not a rate, "estimativa reportada" = reported estimate):
```json
{ "family":"CpuGpu", "hardware":"Intel Core i9-14900K", "hwId":"/intelcpu/0",
  "id":"/intelcpu/0/temperature/0#CPU Package", "name":"CPU Package", "type":"Temperature",
  "unit":"degC", "state":"values present"|"partial"|"unavailable",
  "min":45, "mean":53.1, "max":61, "allZero":false,
  "role":"reading"|"reported limit, not a current value"|"metadado/limite SPD; ..."|"accumulated counter; not a rate"|"estimativa reportada; ..." }
```

## 1. Dynamic discovery (core requirement)
- At startup: `new Computer { IsCpuEnabled, IsGpuEnabled, IsMemoryEnabled, IsMotherboardEnabled, IsControllerEnabled, IsNetworkEnabled, IsStorageEnabled, IsPsuEnabled, IsBatteryEnabled = true }.Open()`.
- Walk `Hardware` and `SubHardware` **recursively**; every `ISensor` becomes a `SensorReading`. The ID is `sensor.Identifier.ToString()`.
- Subscribe to `Computer.HardwareAdded` and `HardwareRemoved`, and also redo the full scan every 30 s (covers USB devices that LHM does not notify about). Sensors may appear inside an existing piece of hardware.
- **Never** filter by model name. Any new `SensorType` from the library comes in as `Unknown` with `KindRaw` preserved and appears at the end, labeled with its raw name.
- **NEW tag**: a device whose ID is not in `known-devices.json` gets the tag for this session. When Details is closed, the ID is written to the file. On the first run, all devices are written without a tag.

## 2. Category (family → label)
| LHM family (`HardwareType`) | Category |
|---|---|
| Cpu | CPU |
| GpuNvidia, GpuAmd, GpuIntel | GPU |
| Memory | Memory |
| Motherboard, SuperIO, EmbeddedController | Motherboard |
| Storage | Storage |
| Network | Network |
| Psu | PSU |
| Cooler, third-party EmbeddedController | Controller |
| Battery | Battery |
| PowerMonitor | Power |
| (other) | raw enum name |

List order: CPU, GPU, Memory, Motherboard, Storage, Network, PSU, then alphabetical by category; within a category, by name.

## 3. Device display name
1. Use `hardware.Name`, stripping the ID if it is embedded (e.g. "Network /Network/hw-0").
2. If the result is empty or equal to the family → "Network adapter N" (Network), "Disk N" (Storage), "Memory N" (Memory) or "{Category} N", numbered in ID order.
3. Two or more devices with the same name → suffix " · {last ID segment}" (e.g. "KINGSTON SKC3000D2048G · hw-1").

## 4. Consolidation (Memory family)
The **Memory** family becomes **a single "Memory" device** (ID `family:Memory`), grouping Total Memory, Virtual Memory and every DIMM module:
- Modules = hardware whose name contains `(#N)`; N is the **physical slot**. Sort by N and number sequentially: "Module 1", "Module 2", … (e.g. slots 1 and 3 become Module 1 and Module 2).
- Prefix on the sensor name: "Module 1 · Temperature", "Physical · Memory Used" (Total Memory → **Physical**), "Virtual · Memory Used".
- Header meta: "N modules · module 1 in slot 1, module 2 in slot 3".
- The list of consolidated families is configurable in code (`Consolidate = ["Memory"]`), designed to be extended to others.

## 5. Types (display order and label)
| # | SensorType | English label | Chartable? |
|---|---|---|---|
| 0 | Temperature | Temperature | yes (bar/gauge 0–100) |
| 1 | Load | Load | yes (bar/gauge 0–100) |
| 2 | Power | Power | yes (sparkline) |
| 3 | Clock | Clock | yes (sparkline) |
| 4 | Voltage | Voltage | yes (sparkline) |
| 4b | Current | Current | yes (sparkline) |
| 5 | Fan | Fans | yes (bar) |
| 6 | Control | Fan control | yes (bar) |
| 7 | Flow | Flow | yes (sparkline) |
| 8 | SmallData | Memory | no (text) |
| 9 | Data | Data | no (text) |
| 10 | Throughput | Throughput | yes (sparkline, zero baseline) |
| 11 | Level | Level | yes (bar) |
| 12 | Factor | Counters | no |
| 13 | Timing | SPD timings | no |
| 99 | (unknown) | raw name | by unit |

"Chartable" = role Reading, quality ≠ Unavailable and unit in {%, degC, RPM, W, MHz, V, A, B/s, L/h}.

## 6. Role and tag
| Condition | Role | Displayed tag | Hidden by default? |
|---|---|---|---|
| Name contains "Warning Temperature", "Critical Temperature", "Limit", "Max Temperature" (in Storage/Memory), "Resolution" | Limit | `limit` | yes (LIMITS) |
| Memory SPD sensors (Timing, tRCD, tRP, tRAS, tCL, "Thermal Sensor Low/High/Critical Limit") | Metadata | `SPD` | yes (LIMITS) |
| Data Read/Written, Host Reads/Writes, Power On Count, Power On Hours, Unsafe Shutdowns, Media Errors | Accumulated | `accumulated` | no |
| Storage "Life", "Available Spare", "Percentage Used" | Estimate | `estimate` | no |
| Name contains "Distance to TjMax" | Reading | `margin` (not a temperature; exclude from "primary") | no |
| Quality Partial (missing samples) | — | `partial` | no |
| Quality Unavailable (Value always null) | — | `no reading`, value "—" | no |

In the prototype, the role comes pre-classified in `sensors.json`. In the app, apply the rules above and keep a regex override table in `Normalize/Roles.cs`.

## 7. Quality (derived at runtime)
- 10-sample window per sensor: all null → Unavailable; some null → Partial; otherwise Ok.
- `allZero` = the value was 0 in every sample of the session (until the first sample ≠ 0). This is **not** an error: it is only hidden by default.

## 8. Quality rules from the real capture (mandatory)
Derived from `hardware-survey/README.md`:
1. **355 entries ≠ 355 useful sensors**: they include limits, resolution, SPD, accumulated counters and zeros. Hence the filters.
2. **Disk in the Summary comes from PDH**, not from the LHM Storage rates (which had 9/10 values and initial spikes; LHM 0.9.6 uses Stopwatch/TimeSpan with a suspicious conversion). In Details, the LHM rates are shown with the raw value, with no special handling.
3. **Disk space**: use native bytes (`DriveInfo`), not LHM's `Total Space` (labeled GiB, but in practice it is decimal GB).
4. SSD **Warning/Critical Temperature** values are limits, never current temperatures.
5. The Kingston "Temperature #2" (77.85/65.85 °C) has no identified component: show it with its raw name. Do not use it as the primary temperature ("Composite" comes first).
6. Chipset 88–89 °C (Super-I/O label): show as is; do **not** raise an alert.
7. Water In/Out, T Sensor with no value → `no reading`. Water Flow and CPU Optional Fan at zero → hidden by "Zeros", with no conclusion drawn.
8. Fan #3/#4/#5 do not indicate a physical position: do not rename them.
9. `Win32_VoltageProbe.CurrentReading` is null: do not use it as a source.
10. Native StorageReliability returns zeros: do not use it.
11. **Never** display "healthy", "OK" or "failure" based on this data.

## 9. Unit formatting
| Unit | Display |
|---|---|
| degC | 1 decimal + "°C" (in the Summary: integer + "°C") |
| % | integer; 1 decimal if < 10 |
| B/s | < 1 KiB → "B/s" integer; < 1 MiB → "KB/s" integer; otherwise "MB/s" with 1 decimal (base 1024) |
| MiB | < 1024 → "MB" integer; otherwise "GB" with 1 decimal |
| GiB | < 1024 → "GB" with 1 decimal; otherwise "TB" with 2 decimals |
| MHz | < 1000 → "MHz" integer; otherwise "GHz" with 2 decimals |
| V | 3 decimals · A: 2 decimals · W: 1 decimal · RPM: integer · L/h: 1 decimal |
| ns, 1, unknown | ≥ 100 → integer; otherwise up to 2 decimals without trailing zeros; raw unit |
| null/NaN | "—" and no unit |
Decimal separator: **point** (consistent with the prototype and with technical reading). Network in the Summary: integer KB/s or MB/s with 1 decimal, base 1024.
