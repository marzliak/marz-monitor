using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

public static class Units
{
    public static SensorKind Kind(string raw) => Enum.TryParse<SensorKind>(raw, ignoreCase: false, out var k) && k != SensorKind.Unknown ? k : SensorKind.Unknown;

    /// <summary>Native LHM unit per SensorType.</summary>
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

    /// <summary>Order and English label of the types (docs/05 §5). Unknown ones go to the end with the raw name.</summary>
    public static (int Order, string Label) TypeInfo(string kindRaw) => kindRaw switch
    {
        "Temperature" => (0, "Temperature"),
        "Load" => (1, "Load"),
        "Power" => (2, "Power"),
        "Clock" => (3, "Clock"),
        "Voltage" => (4, "Voltage"),
        "Current" => (5, "Current"),
        "Fan" => (6, "Fans"),
        "Control" => (7, "Fan control"),
        "Flow" => (8, "Flow"),
        "SmallData" => (9, "Memory"),
        "Data" => (10, "Data"),
        "Throughput" => (11, "Throughput"),
        "Level" => (12, "Level"),
        "Factor" => (13, "Counters"),
        "Timing" => (14, "SPD timings"),
        _ => (99, kindRaw),
    };
}
