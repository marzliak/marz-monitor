namespace MonitorTray.Core.Model;

public enum SensorKind
{
    Temperature, Load, Power, Clock, Voltage, Current, Fan, Control, Flow,
    SmallData, Data, Throughput, Level, Factor, Timing, Energy, Noise, Unknown
}

public enum SensorRole { Reading, Limit, Metadata, Accumulated, Estimate }

public enum SensorQuality { Ok, Partial, Unavailable }

/// <summary>Leitura crua de um provedor, antes de qualquer normalização.</summary>
/// <param name="Family">Nome do HardwareType do LHM (Cpu, GpuNvidia, Memory, SuperIO, ...).</param>
public sealed record RawSensor(
    string Id, string DeviceId, string DeviceName, string Family,
    string Name, string KindRaw, string Unit, float? Value);

public sealed record SensorReading(
    string Id, string DeviceId, string Name, SensorKind Kind, string KindRaw, string Unit,
    float? Value, float? Min, float? Max, SensorRole Role, SensorQuality Quality,
    bool AllZero, string Tag);

public sealed record MemoryModule(int Index, int Slot);

public sealed record Device(
    string Id, string Family, string RawName, string DisplayName, string Category,
    bool IsNew, IReadOnlyList<SensorReading> Sensors, IReadOnlySet<string> SourceIds,
    IReadOnlyList<MemoryModule> Modules);

public sealed record SummaryData(
    float? CpuLoad, float? CpuTemp, float? CpuPower,
    float? GpuLoad, float? GpuTemp, float? GpuPower,
    ulong MemUsed, ulong MemTotal,
    double DiskReadBps, double DiskWriteBps,
    double NetDownBps, double NetUpBps)
{
    public static SummaryData Empty { get; } = new(null, null, null, null, null, null, 0, 0, 0, 0, 0, 0);
}

public sealed record SourceStatus(bool IsAdmin, bool HardwareReady, bool Simulated, string? Notice)
{
    public static SourceStatus Starting { get; } = new(false, false, false, null);
}

/// <summary>Unidade lógica (C:, D:...) com o disco físico que a contém. Bytes nativos (DriveInfo), nunca o "Total Space" do LHM.</summary>
public sealed record VolumeInfo(string Letter, string Label, string Hardware, ulong Total, ulong Free)
{
    /// <summary>Leitura/escrita da unidade (bytes/s, contador LogicalDisk do Windows); null sem leitura.</summary>
    public double? ReadBps { get; init; }
    public double? WriteBps { get; init; }
    public ulong Used => Total > Free ? Total - Free : 0;
    public double Fraction => Total == 0 ? 0 : (double)Used / Total;
}

public sealed record Snapshot(
    DateTimeOffset At, SummaryData Summary,
    IReadOnlyList<double> NetDownHistory, IReadOnlyList<double> NetUpHistory,
    IReadOnlyList<Device> Devices, int EntryCount, SourceStatus Status)
{
    /// <summary>Histórico de uso (%) dos três cards do Resumo; null = sem leitura naquele tick (lacuna).</summary>
    /// <summary>Pontos visíveis do gráfico de rede para a janela escolhida (NetDown/UpHistory traz Points+1 valores).</summary>
    public int HistoryPoints { get; init; } = 60;
    /// <summary>RPM das saídas de fan da placa (Super I/O), por índice da saída. Vazio sem leitura.</summary>
    public IReadOnlyDictionary<int, float> MbFanRpm { get; init; } = new Dictionary<int, float>();
    /// <summary>Temperaturas da placa (nome do sensor → °C): Motherboard, Chipset, VRM... (zeros/ausentes fora).</summary>
    public IReadOnlyDictionary<string, float> MbTemps { get; init; } = new Dictionary<string, float>();
    /// <summary>% de controle (PWM) das saídas de fan da placa, mesmo índice de MbFanRpm.</summary>
    public IReadOnlyDictionary<int, float> MbFanControl { get; init; } = new Dictionary<int, float>();
    /// <summary>Temperaturas por papel dos cards do popup (id de TempCards → °C); papel sem leitura fica de fora.</summary>
    public IReadOnlyDictionary<string, float> Temps { get; init; } = new Dictionary<string, float>();
    /// <summary>Id do sensor do LHM de cada papel de Temps (clique no card abre o Detalhes nele).</summary>
    public IReadOnlyDictionary<string, string> TempSources { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<float?> CpuHistory { get; init; } = [];
    public IReadOnlyList<float?> GpuHistory { get; init; } = [];
    public IReadOnlyList<float?> MemHistory { get; init; } = [];
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];
}
