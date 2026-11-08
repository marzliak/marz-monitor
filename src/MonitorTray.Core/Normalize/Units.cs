using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

public static class Units
{
    public static SensorKind Kind(string raw) => Enum.TryParse<SensorKind>(raw, ignoreCase: false, out var k) && k != SensorKind.Unknown ? k : SensorKind.Unknown;

    /// <summary>Unidade nativa do LHM por SensorType.</summary>
    public static string ForLhmType(string raw) => raw switch
    {
        "Temperature" => "degC",
        "Load" or "Control" or "Level" or "Humidity" => "%",
        "Power" => "W",
        "Clock" => "MHz",
        "Voltage" => "V",
        "Current" => "A",
        "Fan" => "RPM",
        "Flow" => "L/h",
        "SmallData" => "MiB",
        "Data" => "GiB",
        "Throughput" => "B/s",
        "Timing" => "ns",
        "Energy" => "mWh",
        "Noise" => "dBA",
        "Frequency" => "Hz",
        _ => "1",
    };

    public static readonly IReadOnlySet<string> GraphUnits = new HashSet<string> { "%", "degC", "RPM", "W", "MHz", "V", "A", "B/s", "L/h" };
    public static readonly IReadOnlySet<string> BarUnits = new HashSet<string> { "%", "degC", "RPM" };
    public static bool IsFixedRange(string unit) => unit is "%" or "degC";

    /// <summary>Ordem e rótulo pt-BR dos tipos (docs/05 §5). Desconhecidos vão para o fim com o nome cru.</summary>
    public static (int Order, string Label) TypeInfo(string kindRaw) => kindRaw switch
    {
        "Temperature" => (0, "Temperatura"),
        "Load" => (1, "Carga"),
        "Power" => (2, "Potência"),
        "Clock" => (3, "Clock"),
        "Voltage" => (4, "Tensão"),
        "Current" => (5, "Corrente"),
        "Fan" => (6, "Ventoinhas"),
        "Control" => (7, "Controle de ventoinha"),
        "Flow" => (8, "Fluxo"),
        "SmallData" => (9, "Memória"),
        "Data" => (10, "Dados"),
        "Throughput" => (11, "Taxa"),
        "Level" => (12, "Nível"),
        "Factor" => (13, "Contadores"),
        "Timing" => (14, "Tempos SPD"),
        _ => (99, kindRaw),
    };
}
