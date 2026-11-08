namespace MonitorTray.Core.Fans;

/// <summary>Reading of a board fan card: rpm only when measured; control % of the chip output.</summary>
public sealed record BoardFanReading(int? Rpm, int? Percent, bool RpmMeasured, int? ChipIndex);

/// <summary>
/// Armoury Crate header → Super I/O output (rpm/% read only). Chassis 1..4 verified live (outputs 0, 2, 3, 4);
/// CPU and pumps were estimated from the control % in the capture (outputs 1, 5, 6) and their rpm was never confirmed:
/// the card shows only the % and "—" for rpm. The rpm is never computed from the %.
/// </summary>
public static class BoardFanMap
{
    public static int? ChipIndex(string acName) => acName switch
    {
        "Chassis Fan 1" => 0, "CPU Fan" => 1, "Chassis Fan 2" => 2, "Chassis Fan 3" => 3, "Chassis Fan 4" => 4, "Water Pump+" => 5, "AIO Pump" => 6, _ => null,
    };

    /// <summary>rpm measured and verified for this header (chassis fans only).</summary>
    public static bool RpmMeasured(string acName) => acName.StartsWith("Chassis Fan ", StringComparison.Ordinal) && ChipIndex(acName) != null;

    public static BoardFanReading Read(string acName, IReadOnlyDictionary<int, float> rpm, IReadOnlyDictionary<int, float> control)
    {
        var i = ChipIndex(acName);
        bool measured = RpmMeasured(acName);
        int? r = measured && i is int a && rpm.TryGetValue(a, out var v) && v >= 0 ? (int)Math.Round(v) : null;
        int? p = i is int b && control.TryGetValue(b, out var c) && c is >= 0 and <= 100 ? (int)Math.Round(c) : null;
        return new BoardFanReading(r, p, measured, i);
    }

    /// <summary>
    /// Output index at the end of the sensor id: "/lpc/nct6798d/0/fan/2" → 2. Tolerates the "#name" suffix of exported
    /// captures (simulated mode: "/Motherboard/hw-1/fan/2#Fan #3"). null = no index.
    /// </summary>
    public static int? TrailingIndex(string sensorId)
    {
        var id = sensorId;
        int hash = id.IndexOf('#');
        if (hash >= 0) id = id[..hash];
        return int.TryParse(id[(id.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null;
    }
}
