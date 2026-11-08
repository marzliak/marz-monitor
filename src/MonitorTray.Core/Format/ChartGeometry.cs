namespace MonitorTray.Core.Format;

public readonly record struct Pt(double X, double Y);

/// <summary>Chart geometry (docs/06 §2–3). Pure, no WPF, so it can be tested.</summary>
public static class ChartGeometry
{
    /// <summary>Half of the mirrored chart: points from (0,C) to (W,C). dir=+1 goes up (download), -1 goes down (upload).</summary>
    public static IReadOnlyList<Pt> Mirror(IReadOnlyList<double> values, int slots, double scale, double width, double center, double half, int dir)
    {
        var pts = new List<Pt>(values.Count + 2);
        if (values.Count == 0 || slots < 2) return pts;
        var step = width / (slots - 1);
        var offset = slots - values.Count;
        pts.Add(new(Math.Max(0, offset) * step, center));
        for (int i = 0; i < values.Count; i++)
        {
            var f = scale > 0 ? Math.Clamp(values[i] / scale, 0, 1) : 0;
            pts.Add(new((i + offset) * step, center - dir * f * half));
        }
        pts.Add(new(width, center));
        return pts;
    }

    public static (double Min, double Max) MinMax(IReadOnlyList<float?> values)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in values) if (v is float f) { lo = Math.Min(lo, f); hi = Math.Max(hi, f); }
        return (lo, hi);
    }

    /// <summary>Vertical range (docs/06 §3): fixed, or min/max with 15% padding (zero base for rates).</summary>
    public static (double Lo, double Hi)? Range(IReadOnlyList<float?> values, (double Lo, double Hi)? fixedRange, bool zeroBase)
    {
        var (lo, hi) = MinMax(values);
        if (lo == double.MaxValue) return null;
        if (fixedRange is { } fr) return fr;
        if (zeroBase) lo = 0;
        var pad = (hi - lo) * 0.15;
        if (pad == 0) pad = Math.Abs(hi) * 0.05;
        if (pad == 0) pad = 1;
        if (!zeroBase) lo -= pad;
        hi += pad;
        return (lo, hi);
    }

    /// <summary>Contiguous runs of non-null values (≥ 2 points). Null breaks the line; gaps are never interpolated.</summary>
    public static IReadOnlyList<IReadOnlyList<Pt>> Runs(IReadOnlyList<float?> values, double step, double firstX, double height, double lo, double hi)
    {
        var result = new List<IReadOnlyList<Pt>>();
        var run = new List<Pt>();
        void Flush() { if (run.Count > 1) result.Add(run.ToArray()); run.Clear(); }
        var span = hi - lo; if (span <= 0) span = 1;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not float v) { Flush(); continue; }
            var t = Math.Clamp((v - lo) / span, 0, 1);
            run.Add(new(firstX + i * step, height - 1 - t * (height - 2)));
        }
        Flush();
        return result;
    }

    public static IReadOnlyList<IReadOnlyList<Pt>> Runs(IReadOnlyList<float?> values, double width, double height, (double Lo, double Hi)? fixedRange, bool zeroBase)
    {
        if (values.Count < 2) return [];
        if (Range(values, fixedRange, zeroBase) is not { } r) return [];
        return Runs(values, width / (values.Count - 1), 0, height, r.Lo, r.Hi);
    }

    /// <summary>
    /// Monotone curve (Fritsch–Carlson) as cubic Bézier segments. Passes through every real point and
    /// never overshoots the interval between two neighbours: smooths without inventing peaks or valleys.
    /// </summary>
    public static IReadOnlyList<(Pt C1, Pt C2, Pt P)> Smooth(IReadOnlyList<Pt> p)
    {
        int n = p.Count;
        var segs = new List<(Pt, Pt, Pt)>(Math.Max(0, n - 1));
        if (n < 2) return segs;
        var d = new double[n - 1];
        for (int i = 0; i < n - 1; i++) { var h = p[i + 1].X - p[i].X; d[i] = h == 0 ? 0 : (p[i + 1].Y - p[i].Y) / h; }
        var m = new double[n];
        m[0] = d[0]; m[n - 1] = d[n - 2];
        for (int i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (int i = 0; i < n - 1; i++)
        {
            if (d[i] == 0) { m[i] = 0; m[i + 1] = 0; continue; }
            double a = m[i] / d[i], b = m[i + 1] / d[i];
            if (a < 0) { m[i] = 0; a = 0; }
            if (b < 0) { m[i + 1] = 0; b = 0; }
            var s = a * a + b * b;
            if (s > 9) { var t = 3 / Math.Sqrt(s); m[i] = t * a * d[i]; m[i + 1] = t * b * d[i]; }
        }
        for (int i = 0; i < n - 1; i++)
        {
            var h = p[i + 1].X - p[i].X;
            segs.Add((new Pt(p[i].X + h / 3, p[i].Y + m[i] * h / 3), new Pt(p[i + 1].X - h / 3, p[i + 1].Y - m[i + 1] * h / 3), p[i + 1]));
        }
        return segs;
    }
}
