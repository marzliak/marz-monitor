using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Fans;

/// <summary>
/// Board fans turned off by the user in the popup (card click). In memory only: on exit/restart everything is back on.
/// Rules: CPU/pumps never turn off; with no CPU reading or CPU &gt; 85 °C / GPU &gt; 80 °C nothing turns off and all turn back on;
/// FULL and game mode turn all back on; the other modes leave the ones that are off or in MANUAL alone (FULL takes them out of MANUAL). On the chip, "off" is a protection curve
/// (0% up to 80 °C, 100% from 85 °C), so the fan comes back on its own even with the app closed/frozen.
/// </summary>
public enum BoardFanClick { None, TurnOn, TurnOff, Blocked }

public sealed class BoardFanOff
{
    public const float CpuLimitC = TempLimits.CpuC, GpuLimitC = TempLimits.GpuC;
    public const int CurveOffUntilC = 80, CurveFullFromC = 85;

    private readonly HashSet<string> off = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> manual = new(StringComparer.Ordinal);

    /// <summary>Lowest MANUAL % on the board (below that the case fans stop; for 0 there is OFF).</summary>
    public const int ManualMinPercent = 20;

    /// <summary>Header that never turns off (lock): CPU fan and pumps.</summary>
    public static bool Locked(string fan) =>
        fan.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || fan.Contains("Pump", StringComparison.OrdinalIgnoreCase);

    /// <summary>Temperature above the limit: nothing stays off.</summary>
    public static bool TooHot(float? cpuC, float? gpuC) => cpuC > CpuLimitC || gpuC > GpuLimitC;

    /// <summary>Why this fan cannot turn off right now (null = it can).</summary>
    public static string? BlockReason(string fan, float? cpuC, float? gpuC) =>
        Locked(fan) ? "CPU and pumps never turn off"
        : cpuC is null ? "no CPU temperature reading"
        : TooHot(cpuC, gpuC) ? $"CPU above {CpuLimitC:0} °C or GPU above {GpuLimitC:0} °C"
        : null;

    /// <summary>
    /// Protection curve written in place of the fan curve: <paramref name="points"/> points (°C, PWM 0..255),
    /// PWM 0 up to 80 °C and 255 from 85 °C. Increasing temperatures (the first points 10 °C apart below 80).
    /// </summary>
    public static int[][] ProtectionCurve(int points) => HoldCurve(points, 0);

    /// <summary>
    /// Flat curve at the requested PWM (0..255) up to 80 °C and 255 from 85 °C: used by OFF (PWM 0) and by MANUAL.
    /// The chip ramps the fan up on its own if it heats up, even with Marz closed/frozen.
    /// </summary>
    public static int[][] HoldCurve(int points, int pwm)
    {
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points));
        pwm = Math.Clamp(pwm, 0, 255);
        var c = new int[points][];
        for (int i = 0; i < points - 1; i++) c[i] = [CurveOffUntilC - (points - 2 - i) * 10, pwm];
        c[points - 1] = [CurveFullFromC, 255];
        return c;
    }

    /// <summary>Clamped MANUAL % (minimum <see cref="ManualMinPercent"/>, maximum 100).</summary>
    public static int ClampManual(int percent) => Math.Clamp(percent, ManualMinPercent, 100);

    /// <summary>Fixed % of the fan in MANUAL (null = follows the curve/mode).</summary>
    public int? ManualPercent(string fan) { lock (off) return manual.TryGetValue(fan, out var p) ? p : null; }
    public bool AnyManual { get { lock (off) return manual.Count > 0; } }
    public IReadOnlyList<string> Manual { get { lock (off) return manual.Keys.ToList(); } }

    /// <summary>Puts it in MANUAL (leaves OFF). Returns the applied %.</summary>
    public int SetManual(string fan, int percent)
    {
        var p = ClampManual(percent);
        lock (off) { off.Remove(fan); manual[fan] = p; }
        return p;
    }

    /// <summary>Goes back to the curve. true = it was in MANUAL.</summary>
    public bool ClearManual(string fan) { lock (off) return manual.Remove(fan); }
    public IReadOnlyList<string> ClearAllManual() { lock (off) { var l = manual.Keys.ToList(); manual.Clear(); return l; } }

    public IReadOnlyList<string> Off { get { lock (off) return off.ToList(); } }
    public bool IsOff(string fan) { lock (off) return off.Contains(fan); }
    public bool Any { get { lock (off) return off.Count > 0; } }

    /// <summary>Marks it as off if the rules allow. true = changed.</summary>
    public bool TurnOff(string fan, float? cpuC, float? gpuC)
    {
        if (BlockReason(fan, cpuC, gpuC) != null) return false;
        lock (off) { manual.Remove(fan); return off.Add(fan); }
    }

    public bool TurnOn(string fan) { lock (off) return off.Remove(fan); }

    /// <summary>
    /// What clicking the card does. "TURN OFF BOARD FAN" option off or CPU/pump: nothing (the card does not even look clickable).
    /// Off: turns it back on. On: turns it off, or Blocked when the rules do not allow it now (BlockReason gives the reason).
    /// </summary>
    public BoardFanClick ClickAction(string fan, bool optionOn, float? cpuC, float? gpuC) =>
        !optionOn || Locked(fan) ? BoardFanClick.None
        : IsOff(fan) ? BoardFanClick.TurnOn
        : BlockReason(fan, cpuC, gpuC) != null ? BoardFanClick.Blocked
        : BoardFanClick.TurnOff;

    /// <summary>The fan card reacts to the mouse (hand cursor, click) only with the option on and when not locked.</summary>
    public static bool Clickable(string fan, bool optionOn) => optionOn && !Locked(fan);

    public IReadOnlyList<string> ClearAll() { lock (off) { var l = off.ToList(); off.Clear(); return l; } }

    /// <summary>Check on every reading: no CPU reading or above the limit turns all back on. Returns the ones turned back on.</summary>
    public IReadOnlyList<string> Check(float? cpuC, float? gpuC) => cpuC is null || TooHot(cpuC, gpuC) ? ClearAll() : [];

    /// <summary>
    /// Fans that receive a mode. FULL includes the ones that are off (turns all back on, even outside the list) and clears the state;
    /// the other modes skip the ones that are off, which stay off.
    /// </summary>
    public IReadOnlyList<string> ModeTargets(string mode, IEnumerable<string> fans)
    {
        lock (off)
        {
            if (mode == "full") { var all = fans.Union(off).Union(manual.Keys).ToList(); off.Clear(); manual.Clear(); return all; }
            return fans.Where(f => !off.Contains(f) && !manual.ContainsKey(f)).ToList();
        }
    }
}
