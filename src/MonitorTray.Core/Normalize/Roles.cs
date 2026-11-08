using System.Text.RegularExpressions;
using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

/// <summary>Papel do sensor (docs/05 §6). Overrides por regex ficam no topo.</summary>
public static partial class Roles
{
    private static readonly (Regex Family, Regex Name, SensorRole Role)[] Overrides =
    [
        // (família, nome, papel) — avaliados antes das regras gerais. Vazio por padrão.
    ];

    [GeneratedRegex(@"Distance to TjMax", RegexOptions.IgnoreCase)] private static partial Regex Margin();
    [GeneratedRegex(@"Warning Temperature|Critical Temperature|Limit|Resolution|Threshold", RegexOptions.IgnoreCase)] private static partial Regex LimitName();
    [GeneratedRegex(@"Max Temperature", RegexOptions.IgnoreCase)] private static partial Regex MaxTemp();
    [GeneratedRegex(@"^(Data Read|Data Written|Host Reads|Host Writes|Power On Count|Power On Hours|Unsafe Shutdowns|Media Errors)\b", RegexOptions.IgnoreCase)] private static partial Regex Accumulated();
    [GeneratedRegex(@"^(Life|Available Spare|Percentage Used)$", RegexOptions.IgnoreCase)] private static partial Regex EstimateName();
    [GeneratedRegex(@"^t[A-Z]|Timing|Cycle Time|Thermal Sensor .*Limit|Temperature Sensor Resolution", RegexOptions.None)] private static partial Regex Spd();

    public static bool IsMargin(string name) => Margin().IsMatch(name);

    public static SensorRole Classify(string family, string kindRaw, string name)
    {
        foreach (var o in Overrides) if (o.Family.IsMatch(family) && o.Name.IsMatch(name)) return o.Role;
        bool memory = family == "Memory", storage = family == "Storage";
        if (memory && (kindRaw == "Timing" || Spd().IsMatch(name) || LimitName().IsMatch(name))) return SensorRole.Metadata;
        if (LimitName().IsMatch(name)) return SensorRole.Limit;
        if ((memory || storage) && MaxTemp().IsMatch(name)) return SensorRole.Limit;
        if (Accumulated().IsMatch(name)) return SensorRole.Accumulated;
        if (storage && EstimateName().IsMatch(name)) return SensorRole.Estimate;
        return SensorRole.Reading;
    }

    public static string Tag(SensorRole role, SensorQuality quality, string name) =>
        quality == SensorQuality.Unavailable ? "sem leitura"
        : quality == SensorQuality.Partial ? "parcial"
        : IsMargin(name) ? "margem"
        : role switch
        {
            SensorRole.Accumulated => "acumulado",
            SensorRole.Estimate => "estimativa",
            SensorRole.Limit => "limite",
            SensorRole.Metadata => "SPD",
            _ => "",
        };

    public static bool Graphable(SensorRole role, SensorQuality quality, string unit) =>
        role == SensorRole.Reading && quality != SensorQuality.Unavailable && Units.GraphUnits.Contains(unit);
}
