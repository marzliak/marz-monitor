namespace MonitorTray.Core.Fans;

/// <summary>Fan curve point: source temperature (°C) → output (%).</summary>
public readonly record struct CurvePoint(double TempC, double Percent);

/// <summary>
/// Editing limits of a curve: X axis range (°C), minimum % (pump ≥ 30%, board CPU/pumps never 0%)
/// and number of points (cooler 2..8; board = exactly the chip's points).
/// </summary>
public sealed record CurveLimits(double TMin, double TMax, double PMin, int MinPoints, int MaxPoints)
{
    /// <summary>Fixed number of points (board: the chip always has the same 4): nothing is added or removed.</summary>
    public bool FixedCount => MinPoints == MaxPoints;
}

/// <summary>
/// Point editing for the curve editor (pure logic; the window only draws and forwards the mouse).
/// Integer values (°C and %), strictly increasing temperatures, % between the minimum and 100.
/// </summary>
public static class CurveEdit
{
    /// <summary>Linear interpolation; outside the range it holds the end value (like Armoury Crate and the chip).</summary>
    public static double Interpolate(IReadOnlyList<CurvePoint> pts, double t)
    {
        if (pts.Count == 0) return 0;
        if (t <= pts[0].TempC) return pts[0].Percent;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            if (t <= b.TempC) return b.TempC == a.TempC ? b.Percent : a.Percent + (b.Percent - a.Percent) * (t - a.TempC) / (b.TempC - a.TempC);
        }
        return pts[^1].Percent;
    }

    private static CurvePoint Clamp(CurvePoint p, CurveLimits l) =>
        new(Math.Clamp(Math.Round(p.TempC), l.TMin, l.TMax), Math.Clamp(Math.Round(p.Percent), l.PMin, 100));

    /// <summary>Rounds, clamps, sorts and drops repeated temperatures (the first one stays); at most MaxPoints.</summary>
    public static List<CurvePoint> Normalize(IEnumerable<CurvePoint> pts, CurveLimits l)
    {
        var r = new List<CurvePoint>();
        foreach (var p in pts.Select(p => Clamp(p, l)).OrderBy(p => p.TempC))
            if (r.Count == 0 || p.TempC > r[^1].TempC) r.Add(p);
        return r.Take(l.MaxPoints).ToList();
    }

    /// <summary>Drags point <paramref name="i"/>: never past its neighbours (1 °C gap) or the limits.</summary>
    public static List<CurvePoint> Move(IReadOnlyList<CurvePoint> pts, int i, double t, double p, CurveLimits l)
    {
        var r = pts.ToList();
        if (i < 0 || i >= r.Count) return r;
        double lo = i > 0 ? r[i - 1].TempC + 1 : l.TMin, hi = i < r.Count - 1 ? r[i + 1].TempC - 1 : l.TMax;
        if (lo > hi) return r;
        r[i] = new CurvePoint(Math.Clamp(Math.Round(t), lo, hi), Math.Clamp(Math.Round(p), l.PMin, 100));
        return r;
    }

    /// <summary>Fixed % (FIXED mode): just the value, clamped to the actuator's minimum.</summary>
    public static int Fixed(double p, CurveLimits l) => (int)Math.Clamp(Math.Round(p), l.PMin, 100);

    /// <summary>
    /// Double click: new point at <paramref name="t"/> on the current curve. null = already at the point limit,
    /// outside the axis or a point already exists at that temperature.
    /// </summary>
    public static (List<CurvePoint> Points, int Index)? Add(IReadOnlyList<CurvePoint> pts, double t, CurveLimits l)
    {
        if (l.FixedCount || pts.Count >= l.MaxPoints) return null;
        t = Math.Round(t);
        if (t < l.TMin || t > l.TMax || pts.Any(p => p.TempC == t)) return null;
        var np = new CurvePoint(t, Math.Clamp(Math.Round(Interpolate(pts, t)), l.PMin, 100));
        var r = pts.Append(np).OrderBy(p => p.TempC).ToList();
        return (r, r.IndexOf(np));
    }

    /// <summary>"+ POINT" button: in the middle of the segment after the selected point (before it, if it is the last).</summary>
    public static (List<CurvePoint> Points, int Index)? AddAfter(IReadOnlyList<CurvePoint> pts, int sel, CurveLimits l)
    {
        if (pts.Count == 0) return Add(pts, (l.TMin + l.TMax) / 2, l);
        sel = Math.Clamp(sel, 0, pts.Count - 1);
        double a, b;
        if (pts.Count == 1) { a = pts[0].TempC; b = pts[0].TempC < l.TMax ? l.TMax : l.TMin; }
        else if (sel < pts.Count - 1) { a = pts[sel].TempC; b = pts[sel + 1].TempC; }
        else { a = pts[sel - 1].TempC; b = pts[sel].TempC; }
        if (Math.Abs(b - a) < 2) return null;
        return Add(pts, Math.Round((a + b) / 2), l);
    }

    /// <summary>Del: removes point <paramref name="i"/>. null = not allowed (minimum points or the chip's fixed count).</summary>
    public static List<CurvePoint>? Remove(IReadOnlyList<CurvePoint> pts, int i, CurveLimits l)
    {
        if (l.FixedCount || pts.Count <= l.MinPoints || i < 0 || i >= pts.Count) return null;
        var r = pts.ToList(); r.RemoveAt(i); return r;
    }

    /// <summary>How many points changed between the saved and the edited curve (moved, added or removed).</summary>
    public static int Changed(IReadOnlyList<CurvePoint> saved, IReadOnlyList<CurvePoint> edited)
    {
        int added = edited.Count(p => !saved.Contains(p)), gone = saved.Count(p => !edited.Contains(p));
        return Math.Max(added, gone);
    }

    /// <summary>All points fit the axis and limits unchanged (otherwise the curve can only be viewed, not edited).</summary>
    public static bool Fits(IReadOnlyList<CurvePoint> pts, CurveLimits l) =>
        pts.Count >= l.MinPoints && pts.Count <= l.MaxPoints
        && pts.All(p => p.TempC >= l.TMin && p.TempC <= l.TMax && p.Percent >= 0 && p.Percent <= 100)
        && pts.Zip(pts.Skip(1)).All(z => z.Second.TempC > z.First.TempC);

    /// <summary>Board chip PWM (0..255) → %.</summary>
    public static int PercentFromPwm(int pwm) => (int)Math.Round(Math.Clamp(pwm, 0, 255) * 100 / 255.0, MidpointRounding.AwayFromZero);

    /// <summary>% → board chip PWM (0..255).</summary>
    public static int PwmFromPercent(double percent) => (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0, MidpointRounding.AwayFromZero);

    /// <summary>Chip curve [°C, PWM] → points in %.</summary>
    public static List<CurvePoint> FromChip(IEnumerable<int[]> pts) => pts.Where(p => p.Length >= 2).Select(p => new CurvePoint(p[0], PercentFromPwm(p[1]))).ToList();

    /// <summary>Points in % → chip curve [°C, PWM].</summary>
    public static int[][] ToChip(IEnumerable<CurvePoint> pts) => pts.Select(p => new[] { (int)Math.Round(p.TempC), PwmFromPercent(p.Percent) }).ToArray();
}
