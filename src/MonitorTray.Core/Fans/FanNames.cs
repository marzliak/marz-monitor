namespace MonitorTray.Core.Fans;

/// <summary>
/// Display names of the board and cooler fans, one mapping for every place that shows them: the original name
/// (board header as printed on the board/BIOS, derived from the Armoury Crate connector; PUMP/RADIATOR/EMB. on the cooler)
/// unless the user renamed it in the app. A custom name is the main label; the original goes to the tooltip.
/// </summary>
public static class FanNames
{
    public const int MaxLength = 16;
    /// <summary>Cooler parts that can be renamed (keys of Settings.CoolerNames).</summary>
    public static readonly string[] CoolerParts = ["pump", "radiator", "embedded"];

    public static string BoardHeader(string acName) => ThermalMap.BoardHeader(acName);

    public static string CoolerHeader(string part) => part switch
    {
        "pump" => "PUMP", "radiator" => "RADIATOR", "embedded" => "EMB.", _ => part.ToUpperInvariant(),
    };

    /// <summary>Cooler part of an actuator id (ry:pump → pump); null if it is not a cooler actuator.</summary>
    public static string? CoolerPart(string actuatorId) => actuatorId switch
    {
        ThermalMap.PumpId => "pump", ThermalMap.RadiatorId => "radiator", ThermalMap.EmbeddedId => "embedded", _ => null,
    };

    /// <summary>The user's name for <paramref name="key"/>, or null (not renamed).</summary>
    public static string? Custom(IReadOnlyDictionary<string, string> names, string key) =>
        names.TryGetValue(key, out var n) && n.Trim() is { Length: > 0 } t ? t : null;

    public static string Name(IReadOnlyDictionary<string, string> names, string key, string original) => Custom(names, key) ?? original;

    /// <summary>"Exhaust · CHA_FAN1" when renamed, otherwise just the original.</summary>
    public static string Tip(IReadOnlyDictionary<string, string> names, string key, string original) =>
        Custom(names, key) is string c ? $"{c} · {original}" : original;

    /// <summary>Trimmed and cut to <see cref="MaxLength"/>.</summary>
    public static string Clean(string? name)
    {
        var v = (name ?? "").Trim();
        return v.Length > MaxLength ? v[..MaxLength].TrimEnd() : v;
    }

    /// <summary>New dictionary with <paramref name="key"/> renamed. Empty (or the original name itself) = back to the original.</summary>
    public static IReadOnlyDictionary<string, string> Rename(IReadOnlyDictionary<string, string> names, string key, string? name, string original)
    {
        var d = names.ToDictionary(kv => kv.Key, kv => kv.Value);
        var v = Clean(name);
        if (v.Length == 0 || string.Equals(v, original, StringComparison.OrdinalIgnoreCase)) d.Remove(key); else d[key] = v;
        return d;
    }
}
