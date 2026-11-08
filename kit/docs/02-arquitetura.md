# 02 · Arquitetura

## Solução
```
MonitorTray.sln
├─ src/MonitorTray.App/            WPF (net8.0-windows), UI + composição
│   ├─ App.xaml(.cs)               single-instance (Mutex "Global\MonitorTray"), DI, tray
│   ├─ Tray/TrayIconRenderer.cs    gera o ícone 16×16 a cada tick
│   ├─ Views/PopupWindow.xaml      Resumo + Configurações
│   ├─ Views/DetailsWindow.xaml    janela Detalhes
│   ├─ Controls/                   AreaChart, MirrorChart, Gauge, CellGrid, Sparkline, BlueprintFrame, Segmented
│   ├─ Theming/ThemeService.cs     tema, paleta e ResourceDictionary dinâmicos
│   ├─ Platform/                   Acrylic, TaskbarInfo, Autostart, SystemTheme (P/Invoke)
│   └─ ViewModels/
├─ src/MonitorTray.Core/           net8.0, sem dependência de WPF (testável)
│   ├─ Model/                      SensorReading, Device, Snapshot, Settings
│   ├─ Normalize/                  Categorizer, Namer, Units, Tags, PrimaryPicker, Consolidator
│   ├─ History/RingBuffer.cs
│   └─ Format/                     Formatter, NiceScale
├─ src/MonitorTray.Sensors/        net8.0-windows
│   ├─ LhmProvider.cs              LibreHardwareMonitorLib
│   ├─ PdhProvider.cs              PerformanceCounter (CPU, disco, rede)
│   ├─ MemoryProvider.cs           GlobalMemoryStatusEx
│   ├─ SimulatedProvider.cs        lê sensores.json do kit
│   └─ SensorService.cs            orquestra, aplica o tick e publica o snapshot
└─ tests/MonitorTray.Core.Tests/   xUnit: formatação, escala, primary, consolidação, nomes
```

## Fluxo de dados
```
[Timer thread, intervalo = refreshMs]
  SensorService.Tick()
    ├─ PdhProvider.Sample()      → cpu%, disk R/W B/s, net ↓/↑ B/s
    ├─ MemoryProvider.Sample()   → used/total bytes
    ├─ LhmProvider.Update()      → IHardware.Update() recursivo, lê ISensor.Value
    └─ monta Snapshot (imutável) + empurra no RingBuffer<float?>(120) por sensor
  → publica via IObservable<Snapshot> / evento
[UI thread]
  Dispatcher.InvokeAsync → ViewModels atualizam propriedades
```
- O LHM é **lento** em algumas famílias (Storage e Motherboard podem levar dezenas de ms). Rode o `Update()` do LHM numa thread dedicada; se um tick atrasar, pule-o (não enfileire).
- **Com o popup e o Detalhes fechados**, colete só o que alimenta o ícone da tray (PDH rede). Ao abrir, retome a coleta completa. O histórico é mantido sempre para rede/CPU/GPU/mem; para os demais sensores, só enquanto o Detalhes estiver aberto (com pré-preenchimento vazio, gerando lacunas).

## Interfaces centrais (C#)
```csharp
public enum SensorKind { Temperature, Load, Power, Clock, Voltage, Current, Fan, Control, Flow,
                         SmallData, Data, Throughput, Level, Factor, Timing, Energy, Noise, Unknown }

public sealed record SensorReading(
    string Id,            // ID estável do provedor, ex. "/intelcpu/0/temperature/0"
    string DeviceId,      // "/intelcpu/0"
    string Name,          // "CPU Package"
    SensorKind Kind,
    string KindRaw,       // nome cru do tipo (para tipos desconhecidos)
    string Unit,          // "degC" | "%" | "W" | "MHz" | "V" | "A" | "RPM" | "B/s" | "MiB" | "GiB" | "L/h" | "ns" | "1" | ...
    float? Value,
    float? Min, float? Max,   // observados nesta sessão
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
    void Open();                       // pode lançar UnauthorizedAccess → app segue sem o provedor
    IEnumerable<SensorReading> Read(); // chamado a cada tick
    event Action? HardwareChanged;     // hot-plug
}

public interface IHistoryStore {
    void Push(string sensorId, float? value);
    ReadOnlySpan<float?> Get(string sensorId); // mais antigo → mais novo, até 120
}
```

## Mapeamento das fontes do Resumo
| Campo | Fonte primária | Fallback |
|---|---|---|
| CPU % | PDH `\Processor Information(_Total)\% Processor Utility` | LHM `CPU Total` (Load) |
| CPU °C | LHM CPU: `CPU Package` → `Core Max` → maior Temperature (excluindo "Distance to TjMax") | "—" |
| CPU W | LHM CPU Power `CPU Package` | "—" |
| GPU % | LHM GPU Load `GPU Core` | PDH `\GPU Engine(*engtype_3D)\Utilization Percentage` (soma) |
| GPU °C | LHM GPU `GPU Core` → `GPU Hot Spot` | "—" |
| GPU W | LHM GPU Power `GPU Package` / `GPU Power` | "—" |
| Mem | `GlobalMemoryStatusEx` (ullTotalPhys − ullAvailPhys) | LHM `Total Memory` |
| Disco L/E | PDH `\PhysicalDisk(_Total)\Disk Read Bytes/sec` e `Disk Write Bytes/sec` | — (**não** use as taxas LHM de Storage; ver 05) |
| Rede ↓/↑ | soma de `NetworkInterface.GetIPv4Statistics()` BytesReceived/Sent dos adaptadores `Up` não-loopback/túnel, com delta ÷ Δt real (Stopwatch) | PDH `\Network Interface(*)\Bytes Received/sec` |

Se houver mais de uma CPU ou GPU, o Resumo usa a primeira por ordem de ID (configurável na v2). Se não houver GPU, a coluna GPU mostra "—".

## Persistência
- `%APPDATA%\MonitorTray\settings.json` (schema em `spec/settings.schema.json`). Gravação atômica (arquivo temporário + `File.Replace`), com debounce de 300 ms.
- `%APPDATA%\MonitorTray\known-devices.json`: IDs de dispositivo já vistos, usados na tag NOVO (ver 05).

## Privilégios
O LHM precisa de **admin** para MSR/SMBus/Super-I/O (temperatura e potência da CPU, placa-mãe, SPD da RAM). Estratégia:
1. Manifesto `asInvoker`. Na inicialização, se não for admin, o LHM abre o que conseguir (GPU NVIDIA via NVAPI, Storage SMART parcial), e o Detalhes mostra o banner "Algumas leituras exigem executar como administrador" com o botão "Reiniciar como admin" (`runas`).
2. O autostart como admin usa uma **Tarefa Agendada** com "Executar com privilégios mais altos" (`schtasks /Create /SC ONLOGON /RL HIGHEST /TN MonitorTray /TR "<exe>"`), e não a chave Run, que não eleva.
