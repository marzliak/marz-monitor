# 02 · Architecture

## Solution
```
MonitorTray.sln
├─ src/MonitorTray.App/            WPF (net8.0-windows), UI + composition
│   ├─ App.xaml(.cs)               single-instance (Mutex "Global\MonitorTray"), DI, tray
│   ├─ Tray/TrayIconRenderer.cs    generates the 16×16 icon on every tick
│   ├─ Views/PopupWindow.xaml      Summary + Settings
│   ├─ Views/DetailsWindow.xaml    Details window
│   ├─ Controls/                   AreaChart, MirrorChart, Gauge, CellGrid, Sparkline, BlueprintFrame, Segmented
│   ├─ Theming/ThemeService.cs     dynamic theme, palette and ResourceDictionary
│   ├─ Platform/                   Acrylic, TaskbarInfo, Autostart, SystemTheme (P/Invoke)
│   └─ ViewModels/
├─ src/MonitorTray.Core/           net8.0, no WPF dependency (testable)
│   ├─ Model/                      SensorReading, Device, Snapshot, Settings
│   ├─ Normalize/                  Categorizer, Namer, Units, Tags, PrimaryPicker, Consolidator
│   ├─ History/RingBuffer.cs
│   └─ Format/                     Formatter, NiceScale
├─ src/MonitorTray.Sensors/        net8.0-windows
│   ├─ LhmProvider.cs              LibreHardwareMonitorLib
│   ├─ PdhProvider.cs              PerformanceCounter (CPU, disk, network)
│   ├─ MemoryProvider.cs           GlobalMemoryStatusEx
│   ├─ SimulatedProvider.cs        reads the kit's sensors.json
│   └─ SensorService.cs            orchestrates, applies the tick and publishes the snapshot
└─ tests/MonitorTray.Core.Tests/   xUnit: formatting, scale, primary, consolidation, names
```

## Data flow
```
[Timer thread, interval = refreshMs]
  SensorService.Tick()
    ├─ PdhProvider.Sample()      → cpu%, disk R/W B/s, net ↓/↑ B/s
    ├─ MemoryProvider.Sample()   → used/total bytes
    ├─ LhmProvider.Update()      → recursive IHardware.Update(), reads ISensor.Value
    └─ builds the (immutable) Snapshot + pushes into the RingBuffer<float?>(120) per sensor
  → publishes via IObservable<Snapshot> / event
[UI thread]
  Dispatcher.InvokeAsync → ViewModels update their properties
```
- LHM is **slow** on some families (Storage and Motherboard can take tens of ms). Run LHM's `Update()` on a dedicated thread; if a tick runs late, skip it (do not queue it).
- **With the popup and Details closed**, collect only what feeds the tray icon (PDH network). When they open, resume full collection. History is always kept for network/CPU/GPU/mem; for the other sensors, only while Details is open (pre-filled with empties, producing gaps).

## Core interfaces (C#)
```csharp
public enum SensorKind { Temperature, Load, Power, Clock, Voltage, Current, Fan, Control, Flow,
                         SmallData, Data, Throughput, Level, Factor, Timing, Energy, Noise, Unknown }

public sealed record SensorReading(
    string Id,            // stable provider ID, e.g. "/intelcpu/0/temperature/0"
    string DeviceId,      // "/intelcpu/0"
    string Name,          // "CPU Package"
    SensorKind Kind,
    string KindRaw,       // raw type name (for unknown types)
    string Unit,          // "degC" | "%" | "W" | "MHz" | "V" | "A" | "RPM" | "B/s" | "MiB" | "GiB" | "L/h" | "ns" | "1" | ...
    float? Value,
    float? Min, float? Max,   // observed in this session
    SensorRole Role,      // Reading | Limit | Metadata | Accumulated | Estimate
    SensorQuality Quality // Ok | Partial | Unavailable
);

public sealed record Device(string Id, string Family, string RawName, string DisplayName,
                            string Category, bool IsNew, IReadOnlyList<SensorReading> Sensors,
                            IReadOnlySet<string> SourceIds);

public sealed record Snapshot(DateTimeOffset At, SummaryData Summary, IReadOnlyList<Device> Devices);

public sealed record SummaryData(
    float? CpuLoad, float? CpuTemp, float? CpuPower,
    float? GpuLoad, float? GpuTemp, float? GpuPower,
    ulong MemUsed, ulong MemTotal,
    double DiskReadBps, double DiskWriteBps,
    double NetDownBps, double NetUpBps);

public interface ISensorProvider : IDisposable {
    void Open();                       // may throw UnauthorizedAccess → the app carries on without the provider
    IEnumerable<SensorReading> Read(); // called on every tick
    event Action? HardwareChanged;     // hot-plug
}

public interface IHistoryStore {
    void Push(string sensorId, float? value);
    ReadOnlySpan<float?> Get(string sensorId); // oldest → newest, up to 120
}
```

## Summary source mapping
| Field | Primary source | Fallback |
|---|---|---|
| CPU % | PDH `\Processor Information(_Total)\% Processor Utility` | LHM `CPU Total` (Load) |
| CPU °C | LHM CPU: `CPU Package` → `Core Max` → highest Temperature (excluding "Distance to TjMax") | "—" |
| CPU W | LHM CPU Power `CPU Package` | "—" |
| GPU % | LHM GPU Load `GPU Core` | PDH `\GPU Engine(*engtype_3D)\Utilization Percentage` (sum) |
| GPU °C | LHM GPU `GPU Core` → `GPU Hot Spot` | "—" |
| GPU W | LHM GPU Power `GPU Package` / `GPU Power` | "—" |
| Mem | `GlobalMemoryStatusEx` (ullTotalPhys − ullAvailPhys) | LHM `Total Memory` |
| Disk R/W | PDH `\PhysicalDisk(_Total)\Disk Read Bytes/sec` and `Disk Write Bytes/sec` | — (do **not** use the LHM Storage rates; see 05) |
| Network ↓/↑ | sum of `NetworkInterface.GetIPv4Statistics()` BytesReceived/Sent of the `Up` non-loopback/non-tunnel adapters, with delta ÷ real Δt (Stopwatch) | PDH `\Network Interface(*)\Bytes Received/sec` |

If there is more than one CPU or GPU, the Summary uses the first by ID order (configurable in v2). If there is no GPU, the GPU column shows "—".

## Persistence
- `%APPDATA%\MonitorTray\settings.json` (schema in `spec/settings.schema.json`). Atomic write (temporary file + `File.Replace`), with a 300 ms debounce.
- `%APPDATA%\MonitorTray\known-devices.json`: device IDs already seen, used for the NEW tag (see 05).

## Privileges
LHM needs **admin** for MSR/SMBus/Super-I/O (CPU temperature and power, motherboard, RAM SPD). Strategy:
1. `asInvoker` manifest. At startup, if not admin, LHM opens what it can (NVIDIA GPU via NVAPI, partial Storage SMART), and Details shows the banner "Some readings require running as administrator" with the "Restart as admin" button (`runas`).
2. Autostart as admin uses a **Scheduled Task** with "Run with highest privileges" (`schtasks /Create /SC ONLOGON /RL HIGHEST /TN MonitorTray /TR "<exe>"`), not the Run key, which does not elevate.
