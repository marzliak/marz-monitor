using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorTray.Ryujin;

/// <summary>Cooler fan mode: follows the preset curve (Smart) or a fixed % (Fixed).</summary>
public enum FanMode { Smart, Fixed }

public sealed record CoolerFanSetting
{
    public FanMode Mode { get; init; } = FanMode.Smart;
    public RyujinCurves.Preset Preset { get; init; } = RyujinCurves.Preset.Standard;
    /// <summary>Fixed-mode % (0..100). For the pump never below <see cref="CoolerSafety.PumpMinPercent"/>.</summary>
    public int FixedPercent { get; init; } = 60;
    /// <summary>Custom curve (°C → %); empty = preset curve.</summary>
    public IReadOnlyList<CurveNode> Custom { get; init; } = [];
}

/// <summary>Cooler control settings (%APPDATA%\MonitorTray\ryujin-fans.json). Off by default.</summary>
public sealed record CoolerConfig
{
    public int Version { get; init; } = 1;
    /// <summary>Marz owns the speeds. Off = read-only (let Armoury Crate control).</summary>
    public bool Enabled { get; init; }
    /// <summary>Temperature sensor for the curve (LHM id or "calc:cpu-temp"). Default: CPU, like Armoury Crate.</summary>
    public string SourceId { get; init; } = "calc:cpu-temp";
    public CoolerFanSetting Pump { get; init; } = new() { Preset = RyujinCurves.Preset.Standard };
    public CoolerFanSetting Radiator { get; init; } = new() { Preset = RyujinCurves.Preset.Standard };
    public CoolerFanSetting Embedded { get; init; } = new() { Mode = FanMode.Fixed, FixedPercent = 60, Preset = RyujinCurves.Preset.Standard };
    /// <summary>Radiator % → rpm table (Auto Tuning). Empty = Armoury Crate default table.</summary>
    public IReadOnlyList<RpmPoint> RadiatorTable { get; init; } = [];
    public DateTimeOffset? RadiatorTunedAt { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CoolerConfig Parse(string json)
    {
        CoolerConfig? c;
        try { c = JsonSerializer.Deserialize<CoolerConfig>(json, Json); } catch (Exception) { return new(); }
        if (c is null || c.Version != 1) return new();
        return c.Normalized();
    }

    public CoolerConfig Normalized()
    {
        static CoolerFanSetting N(CoolerFanSetting? s, int min) => (s ?? new()) with
        {
            FixedPercent = Math.Clamp((s ?? new()).FixedPercent, min, 100),
            Custom = ((s ?? new()).Custom ?? []).Take(8).Select(n => new CurveNode(Math.Clamp(n.TempC, 0, 110), Math.Clamp(n.Percent, 0, 100))).OrderBy(n => n.TempC).ToList(),
        };
        return this with
        {
            SourceId = string.IsNullOrWhiteSpace(SourceId) ? "calc:cpu-temp" : SourceId,
            Pump = N(Pump, CoolerSafety.PumpMinPercent), Radiator = N(Radiator, 0), Embedded = N(Embedded, 0),
            RadiatorTable = (RadiatorTable ?? []).Where(p => p.Percent is >= 0 and <= 100 && p.Rpm is >= 0 and < 10000).OrderBy(p => p.Percent).ToList(),
        };
    }
}

/// <summary>
/// Time-based exponential moving average of the curve source temperature. CPU Package swings 10–20 °C in short spikes;
/// the curve follows the average of the last few seconds, like Fan Xpert's spin-up/spin-down time. No reading = reset.
/// </summary>
public sealed class TempSmoother
{
    /// <summary>Time constant (s): a step reaches 63% in ~5 s.</summary>
    public const double DefaultTauSeconds = 5;
    private readonly double tau;
    private double? value;
    public TempSmoother(double tauSeconds = DefaultTauSeconds) { tau = tauSeconds; }
    public double? Value => value;
    public void Reset() => value = null;

    /// <param name="dtSeconds">Time since the previous sample; 0 leaves the value unchanged (repeated calls at the same instant).</param>
    public double? Next(double? sample, double dtSeconds)
    {
        if (sample is not double x || double.IsNaN(x)) { value = null; return null; }
        if (value is not double v || tau <= 0) { value = x; return x; }
        double dt = Math.Clamp(dtSeconds, 0, 60);
        value = v + dt / (tau + dt) * (x - v);
        return value;
    }
}

/// <summary>Safety limits (independent of what the user picks).</summary>
public static class CoolerSafety
{
    /// <summary>Pump never below this (Armoury Crate: 20 % at the start of the curve; more conservative here).</summary>
    public const int PumpMinPercent = 30;
    /// <summary>Water at or above this: pump and radiator at 100 %.</summary>
    public const double WaterPanicC = 50;
    /// <summary>Curve source with no reading: uses this % (pump and radiator).</summary>
    public const int NoSourcePercent = 70;
    /// <summary>Value left on the cooler when Marz quits (it freezes at the last value): Standard at ~60 °C.</summary>
    public const int ExitPumpPercent = 60, ExitRadiatorPercent = 60, ExitEmbeddedPercent = 60;
}

public sealed record CoolerTargets(int PumpPercent, int RadiatorPercent, int EmbeddedPercent, string? Reason);

/// <summary>Computes each fan's % like Armoury Crate (curve on the host), with the safety locks.</summary>
public static class CoolerLogic
{
    public static IReadOnlyList<CurveNode> CurveOf(CoolerFanSetting s, RyujinCurves.Fan fan) =>
        s.Custom.Count >= 2 ? s.Custom : RyujinCurves.CurveFor(fan, s.Preset);

    public static int PercentFor(CoolerFanSetting s, RyujinCurves.Fan fan, double? sourceC)
    {
        if (s.Mode == FanMode.Fixed) return s.FixedPercent;
        if (s.Preset == RyujinCurves.Preset.FullSpeed && s.Custom.Count < 2) return 100;
        if (sourceC is not double t) return CoolerSafety.NoSourcePercent;
        return (int)Math.Round(RyujinCurves.Interpolate(CurveOf(s, fan), t));
    }

    public static CoolerTargets Compute(CoolerConfig c, double? sourceC, double? waterC)
    {
        int pump = PercentFor(c.Pump, RyujinCurves.Fan.Pump, sourceC);
        int rad = PercentFor(c.Radiator, RyujinCurves.Fan.Radiator, sourceC);
        int emb = PercentFor(c.Embedded, RyujinCurves.Fan.Embedded, sourceC);
        string? reason = sourceC is null && (c.Pump.Mode == FanMode.Smart || c.Radiator.Mode == FanMode.Smart) ? "no reading of the curve temperature" : null;
        if (waterC is double w && w >= CoolerSafety.WaterPanicC) { pump = 100; rad = 100; reason = $"water {w:0.0} °C"; }
        pump = Math.Clamp(Math.Max(pump, CoolerSafety.PumpMinPercent), 0, 100);
        return new CoolerTargets(pump, Math.Clamp(rad, 0, 100), Math.Clamp(emb, 0, 100), reason);
    }

    /// <summary>Smooths ramp-up/down like AC's spinUp/spinDown: rises fast, falls slowly (per ~1 s tick).</summary>
    public static int Smooth(int current, int target, int upStep = 10, int downStep = 3) =>
        target > current ? Math.Min(target, current + upStep) : Math.Max(target, current - downStep);
}

/// <summary>
/// Popup buttons CURVES/SILENT/STD/TURBO/FULL: a single preset on pump and radiator, or the custom setup (CURVES).
/// They apply to pump, radiator and embedded fan; the curve source and the radiator table do not change.
/// </summary>
public static class CoolerModes
{
    /// <summary>Single preset on pump and radiator; null = custom setup (curve, fixed % or different presets).</summary>
    public static RyujinCurves.Preset? UniformPreset(CoolerConfig c) =>
        c.Pump.Mode == FanMode.Smart && c.Radiator.Mode == FanMode.Smart && c.Pump.Preset == c.Radiator.Preset
        && c.Pump.Custom.Count == 0 && c.Radiator.Custom.Count == 0 ? c.Pump.Preset : null;

    /// <summary>Pump, radiator and embedded fan on the preset (Enabled does not change).</summary>
    public static CoolerConfig WithPreset(CoolerConfig c, RyujinCurves.Preset p) => c with
    {
        Pump = c.Pump with { Mode = FanMode.Smart, Preset = p, Custom = [] },
        Radiator = c.Radiator with { Mode = FanMode.Smart, Preset = p, Custom = [] },
        Embedded = c.Embedded with { Mode = FanMode.Smart, Preset = p, Custom = [] },
    };

    /// <summary>Embedded fan with a custom setup (your own curve or fixed %), not a preset.</summary>
    public static bool OwnEmbedded(CoolerConfig c) => c.Embedded.Mode == FanMode.Fixed || c.Embedded.Custom.Count >= 2;

    /// <summary>
    /// What to keep before applying a preset: the current custom setup. On a preset (pump/radiator), keeps the one already saved,
    /// but saves the embedded fan if it has a custom setup (the preset now changes it too). null = nothing to keep.
    /// </summary>
    public static CoolerConfig? ToKeep(CoolerConfig c, CoolerConfig? kept = null) =>
        UniformPreset(c) == null ? c : OwnEmbedded(c) ? (kept ?? c) with { Embedded = c.Embedded } : null;

    /// <summary>
    /// CURVES: already on a custom setup, it only turns control on; otherwise pump and radiator go back to the custom setup saved
    /// before the preset; with none saved, default curves (the STD points as a custom curve, editable in Thermal control).
    /// </summary>
    public static CoolerConfig Curves(CoolerConfig current, CoolerConfig? saved)
    {
        if (UniformPreset(current) == null) return current with { Enabled = true };
        var emb = saved != null && OwnEmbedded(saved) ? saved.Embedded : DefaultCurve(current.Embedded, RyujinCurves.Fan.Embedded);
        if (saved != null && UniformPreset(saved) == null) return current with { Enabled = true, Pump = saved.Pump, Radiator = saved.Radiator, Embedded = emb };
        return current with { Enabled = true, Pump = DefaultCurve(current.Pump, RyujinCurves.Fan.Pump), Radiator = DefaultCurve(current.Radiator, RyujinCurves.Fan.Radiator), Embedded = emb };
    }

    /// <summary>
    /// Leaving game mode by any path: each part still as game mode left it (FULL without a curve or the game's fixed %)
    /// goes back to what it was before; parts the user changed during game mode stay. Enabled follows the current value.
    /// </summary>
    public static CoolerConfig UndoGame(CoolerConfig current, CoolerConfig before, int gamePercent)
    {
        bool G(CoolerFanSetting s) => gamePercent >= 100
            ? s.Mode == FanMode.Smart && s.Preset == RyujinCurves.Preset.FullSpeed && s.Custom.Count < 2
            : s.Mode == FanMode.Fixed && s.FixedPercent == gamePercent;
        return current with
        {
            Pump = G(current.Pump) ? before.Pump : current.Pump,
            Radiator = G(current.Radiator) ? before.Radiator : current.Radiator,
            Embedded = G(current.Embedded) ? before.Embedded : current.Embedded,
        };
    }

    /// <summary>Outside game mode the embedded fan never stays on the FULL preset (leftover of a game mode not undone): back to STD.</summary>
    public static CoolerConfig? UnstickEmbedded(CoolerConfig c) =>
        c.Embedded.Mode == FanMode.Smart && c.Embedded.Preset == RyujinCurves.Preset.FullSpeed
            ? c with { Embedded = c.Embedded with { Preset = RyujinCurves.Preset.Standard } } : null;

    private static CoolerFanSetting DefaultCurve(CoolerFanSetting s, RyujinCurves.Fan fan) =>
        s with { Mode = FanMode.Smart, Preset = RyujinCurves.Preset.Standard, Custom = RyujinCurves.CurveFor(fan, RyujinCurves.Preset.Standard).ToList() };
}
