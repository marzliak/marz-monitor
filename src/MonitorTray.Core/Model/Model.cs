namespace MonitorTray.Core.Model;

public enum SensorKind
{
    Temperature, Load, Power, Clock, Voltage, Current, Fan, Control, Flow,
    SmallData, Data, Throughput, Level, Factor, Timing, Energy, Noise, Unknown
}

public enum SensorRole { Reading, Limit, Metadata, Accumulated, Estimate }

public enum SensorQuality { Ok, Partial, Unavailable }

/// <summary>Raw reading from a provider, before any normalization.</summary>
/// <param name="Family">LHM HardwareType name (Cpu, GpuNvidia, Memory, SuperIO, ...).</param>
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

/// <summary>Logical drive (C:, D:...) with the physical disk that holds it. Native bytes (DriveInfo), never LHM's "Total Space".</summary>
public sealed record VolumeInfo(string Letter, string Label, string Hardware, ulong Total, ulong Free)
{
    /// <summary>Drive read/write (bytes/s, Windows LogicalDisk counter); null when there is no reading.</summary>
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
    /// <summary>Load (%) history of the three Summary cards; null = no reading on that tick (gap).</summary>
    /// <summary>Visible points of the network chart for the chosen window (NetDown/UpHistory carries Points+1 values).</summary>
    public int HistoryPoints { get; init; } = 60;
    /// <summary>RPM of the board fan outputs (Super I/O), by output index. Empty when there is no reading.</summary>
    public IReadOnlyDictionary<int, float> MbFanRpm { get; init; } = new Dictionary<int, float>();
    /// <summary>Board temperatures (sensor name → °C): Motherboard, Chipset, VRM... (zeros/missing left out).</summary>
    public IReadOnlyDictionary<string, float> MbTemps { get; init; } = new Dictionary<string, float>();
    /// <summary>Control % (PWM) of the board fan outputs, same index as MbFanRpm.</summary>
    public IReadOnlyDictionary<int, float> MbFanControl { get; init; } = new Dictionary<int, float>();
    /// <summary>Temperatures per role of the popup cards (TempCards id → °C); roles without a reading are left out.</summary>
    public IReadOnlyDictionary<string, float> Temps { get; init; } = new Dictionary<string, float>();
    /// <summary>LHM sensor id of each Temps role (clicking the card opens Details on it).</summary>
    public IReadOnlyDictionary<string, string> TempSources { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<float?> CpuHistory { get; init; } = [];
    public IReadOnlyList<float?> GpuHistory { get; init; } = [];
    public IReadOnlyList<float?> MemHistory { get; init; } = [];
    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];
}
