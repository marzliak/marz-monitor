namespace MonitorTray.Core.Fans;

/// <summary>Thermal control device: each one has its own mode and its own actuators.</summary>
public enum ThermalDevice { Gpu, Board, Cooler }

/// <summary>Top fan strip item: one physical fan (rpm only if measured), pointing to the actuator (curve) that drives it.</summary>
public sealed record StripItem(string Name, string? Nick, string? ActuatorId, int? Rpm, int? Percent, bool Off, bool Locked);

/// <summary>
/// Thermal control "source → curve → actuator" model: actuator ids, temperature source of each one,
/// chart axis, strip items and when the board curve can be edited.
/// </summary>
public static class ThermalMap
{
    public const string GpuId = "gpu", PumpId = "ry:pump", RadiatorId = "ry:rad", EmbeddedId = "ry:emb";
    public const string CpuSource = "calc:cpu-temp", GpuSource = "calc:gpu-temp";
    /// <summary>Cooler curve points (the curve runs on the PC; ryujin-fans.json stores up to 8).</summary>
    public const int CoolerMaxPoints = 8;

    public static string BoardId(string acName) => "mb:" + acName;
    public static string? BoardFan(string actuatorId) => actuatorId.StartsWith("mb:", StringComparison.Ordinal) ? actuatorId[3..] : null;

    public static ThermalDevice? DeviceOf(string actuatorId) =>
        actuatorId == GpuId ? ThermalDevice.Gpu
        : actuatorId.StartsWith("mb:", StringComparison.Ordinal) ? ThermalDevice.Board
        : actuatorId is PumpId or RadiatorId or EmbeddedId ? ThermalDevice.Cooler : null;

    /// <summary>Header name as printed on the board: "Chassis Fan 1" → "CHA_FAN1".</summary>
    public static string BoardHeader(string acName) => acName switch
    {
        "CPU Fan" => "CPU_FAN", "CPU Optional Fan" => "CPU_OPT", "Water Pump+" => "W_PUMP+", "AIO Pump" => "AIO_PUMP",
        _ when acName.StartsWith("Chassis Fan ", StringComparison.Ordinal) => "CHA_FAN" + acName["Chassis Fan ".Length..],
        _ => acName.ToUpperInvariant().Replace(' ', '_'),
    };

    /// <summary>
    /// Temperature source of each board header as the ASUS service writes it (SetMultiFanSource, see asus-rog-protocol-notes):
    /// Chassis Fan 1 follows the GPU, the others the CPU. Read only: the source is chosen in Armoury Crate.
    /// </summary>
    public static string BoardSource(string acName) => acName == "Chassis Fan 1" ? GpuSource : CpuSource;

    /// <summary>Source (sensor id) of the actuator's curve: GPU = core; board = the header's source; cooler = the one chosen in Marz.</summary>
    public static string SourceOf(string actuatorId, string coolerSourceId) => DeviceOf(actuatorId) switch
    {
        ThermalDevice.Gpu => GpuSource,
        ThermalDevice.Board => BoardSource(BoardFan(actuatorId)!),
        _ => coolerSourceId,
    };

    /// <summary>
    /// Chart X axis: liquid varies little (20–60 °C in steps of 5); the other sources 20–100 °C in steps of 10.
    /// Grows to fit the curve points (a preset with a point at 75 °C is not cut off on the liquid axis).
    /// </summary>
    public static (double Min, double Max, double Step) Axis(bool liquid, IEnumerable<double> temps)
    {
        double step = liquid ? 5 : 10, min = 20, max = liquid ? 60 : 100;
        foreach (var t in temps)
        {
            if (!double.IsFinite(t)) continue;
            if (t < min) min = Math.Max(0, Math.Floor(t / step) * step);
            if (t > max) max = Math.Min(liquid ? 110 : 120, Math.Ceiling(t / step) * step);
        }
        return (min, max, step);
    }

    /// <summary>Editing limits: cooler 2..8 points (pump ≥ <paramref name="pumpMin"/>%); board = the chip's points, CPU/pumps never 0%.</summary>
    public static CurveLimits Limits(string actuatorId, (double Min, double Max, double Step) axis, int pumpMin, int boardPoints = 4)
    {
        var dev = DeviceOf(actuatorId);
        if (dev == ThermalDevice.Board)
        {
            var fan = BoardFan(actuatorId)!;
            return new CurveLimits(axis.Min, axis.Max, BoardFanOff.Locked(fan) ? BoardLockedMinPercent : 0, boardPoints, boardPoints);
        }
        return new CurveLimits(axis.Min, axis.Max, actuatorId == PumpId ? pumpMin : 0, 2, CoolerMaxPoints);
    }

    /// <summary>Board CPU and pumps: the edited curve never goes below this (the OFF lock also applies to the curve).</summary>
    public const int BoardLockedMinPercent = 20;

    /// <summary>
    /// Why this board fan's curve cannot be edited right now (null = it can). The write is the same as FULL/OFF
    /// (User curve via Armoury Crate), so it only works in CURVES, with the fan on and no stored curve pending.
    /// </summary>
    public static string? BoardEditBlock(bool available, string? mode, string? pending, bool off, bool savedPending, bool fits) =>
        !available ? "Armoury Crate unavailable"
        : pending != null ? "applying a mode"
        : off ? "fan off: turn it back on to edit"
        : mode != null ? "editing only in CURVES mode (the curve shown is the active mode's)"
        : savedPending ? "the curve stored before FULL/OFF has not been restored yet"
        : !fits ? "curve is not the standard 4 points (fixed or from Fan Xpert): edit it in Armoury Crate"
        : null;

    /// <summary>Board fans in the strip: all headers, in Armoury Crate order; rpm only for the verified ones.</summary>
    public static IReadOnlyList<StripItem> BoardStrip(IEnumerable<string> acFans, Func<string, string?> nick,
        IReadOnlyDictionary<int, float> rpm, IReadOnlyDictionary<int, float> control, Func<string, bool> isOff) =>
        acFans.Select(f =>
        {
            var r = BoardFanMap.Read(f, rpm, control);
            return new StripItem(BoardHeader(f), nick(f), BoardId(f), r.Rpm, r.Percent, isOff(f), BoardFanOff.Locked(f));
        }).ToList();

    /// <summary>Cooler fans in the strip: pump, one item per radiator fan and the embedded fan. <paramref name="custom"/>: user names per part.</summary>
    public static IReadOnlyList<StripItem> CoolerStrip(int? pumpRpm, int? pumpPct, IReadOnlyList<int> radRpm, int? radPct, int? embRpm, int? embPct,
        Func<string, string?>? custom = null)
    {
        string? Nick(string part) => custom?.Invoke(part);
        var rad = Nick("radiator");
        var l = new List<StripItem> { new("PUMP", Nick("pump"), PumpId, pumpRpm, pumpPct, false, false) };
        if (radRpm.Count == 0) l.Add(new("RADIATOR", rad, RadiatorId, null, radPct, false, false));
        for (int i = 0; i < radRpm.Count; i++) l.Add(new($"RAD {i + 1}", rad != null ? $"{rad} {i + 1}" : null, RadiatorId, radRpm[i], radPct, false, false));
        l.Add(new("EMBEDDED", Nick("embedded"), EmbeddedId, embRpm, embPct, false, false));
        return l;
    }

    /// <summary>GPU fans in the strip (all follow the same driver/Marz mode).</summary>
    public static IReadOnlyList<StripItem> GpuStrip(IEnumerable<(uint? Percent, uint? Rpm)> fans) =>
        fans.Select((f, i) => new StripItem($"FAN {i + 1}", null, GpuId, f.Rpm is uint r ? (int)r : null, f.Percent is uint p ? (int)p : null, false, false)).ToList();
}
