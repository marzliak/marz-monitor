using System.Globalization;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Format;

/// <summary>OFF protection turned board fans back on (CPU/GPU above the limit or no CPU reading).</summary>
public sealed record SafetyEvent(DateTimeOffset At, IReadOnlyList<string> Fans, float? CpuC, float? GpuC);

/// <summary>Automatic state summarized by the strip at the top of the popup.</summary>
public sealed record StripState
{
    public bool GameOn { get; init; }
    /// <summary>Executable that turned game mode on (null = turned on by hand).</summary>
    public string? GameBy { get; init; }
    public IReadOnlyCollection<string> GameTargets { get; init; } = [];
    public int GameGpuPercent { get; init; } = 100;
    public int GameCoolerPercent { get; init; } = 100;
    public SafetyEvent? BoardSafety { get; init; }
    /// <summary>GPU protection warning (fans raised to 100%).</summary>
    public string? GpuProtection { get; init; }
    /// <summary>Cooler water at the limit: pump and radiator at 100%.</summary>
    public double? CoolerPanicC { get; init; }
}

/// <summary>One strip item: short text and the tooltip detail.</summary>
public sealed record StripItem(string Text, string Tip);

/// <summary>
/// Thin strip at the top of the popup: what the app did on its own or is keeping active (protections and game mode).
/// Empty = the strip is hidden. Past events (fans turned back on) stay visible for <see cref="EventTtl"/>.
/// </summary>
public static class StatusStrip
{
    public static readonly TimeSpan EventTtl = TimeSpan.FromMinutes(5);

    private static string C(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "°";

    public static IReadOnlyList<StripItem> Items(StripState s, DateTimeOffset now, Func<string, string> fanName)
    {
        var l = new List<StripItem>();
        if (s.GpuProtection is { Length: > 0 } gp) l.Add(new("GPU 100% · protection", gp));
        if (s.CoolerPanicC is double w) l.Add(new($"COOLER 100% · water {w.ToString("0.0", CultureInfo.InvariantCulture)}°",
            $"Water cooler liquid at {w.ToString("0.0", CultureInfo.InvariantCulture)} °C: pump and radiator at 100% until it drops below {TempLimits.LiquidC:0} °C."));
        if (s.BoardSafety is { } e && now - e.At <= EventTtl && e.Fans.Count > 0) l.Add(Safety(e, fanName));
        if (s.GameOn)
        {
            var text = "GAME MODE · " + GameText(s.GameTargets, s.GameGpuPercent, s.GameCoolerPercent);
            l.Add(new(text, s.GameBy != null ? $"Turned on automatically: {s.GameBy} is running. Turns off when the game closes." : "Turned on with the game mode button."));
        }
        return l;
    }

    /// <summary>"CHA_FAN1 back on · CPU 86°" (or "no CPU reading"); fan names come from <paramref name="fanName"/>.</summary>
    public static StripItem Safety(SafetyEvent e, Func<string, string> fanName)
    {
        var names = string.Join(", ", e.Fans.Select(fanName));
        var why = e.CpuC is not float cpu ? "no CPU reading"
            : cpu > TempLimits.CpuC ? "CPU " + C(cpu)
            : e.GpuC is float gpu && gpu > TempLimits.GpuC ? "GPU " + C(gpu)
            : "CPU " + C(cpu);
        var text = $"{names} back on · {why}";
        var tip = $"{e.At.ToLocalTime():HH:mm:ss} · OFF protection: {names} went back to the current mode"
            + $" (CPU above {TempLimits.CpuC:0} °C, GPU above {TempLimits.GpuC:0} °C or no CPU reading). Click to dismiss.";
        return new(text, tip);
    }

    /// <summary>"all 100%" when everything game mode drives goes to 100%; otherwise the parts ("GPU 85% · board 100% · cooler 70%").</summary>
    public static string GameText(IReadOnlyCollection<string> targets, int gpuPercent, int coolerPercent)
    {
        bool cooler = targets.Contains("pump") || targets.Contains("radiator") || targets.Contains("embedded");
        var parts = new List<(string Name, int Pct)>();
        if (targets.Contains("gpu")) parts.Add(("GPU", gpuPercent));
        if (targets.Contains("board")) parts.Add(("board", 100));
        if (cooler) parts.Add(("cooler", coolerPercent));
        if (parts.Count == 0) return "nothing selected";
        if (parts.All(p => p.Pct >= 100)) return parts.Count == 3 ? "all 100%" : string.Join(" and ", parts.Select(p => p.Name)) + " 100%";
        return string.Join(" · ", parts.Select(p => $"{p.Name} {p.Pct}%"));
    }
}
