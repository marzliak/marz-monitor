namespace MonitorTray.Core.History;

/// <summary>
/// Per-second rate (network) history, up to 2 h. Each second keeps the peak of that second's ticks.
/// View groups into clock-aligned buckets (the chart only scrolls when a bucket closes) using each bucket's peak,
/// so short peaks do not vanish in long windows. ~115 KB per series.
/// </summary>
public sealed class RateHistory
{
    public const int MaxSeconds = 7200;
    public const int MaxPoints = 120;
    private readonly RingBuffer<double> secs = new(MaxSeconds + 1);
    private long cur = -1;
    private double curMax;

    public void Add(double value, long second)
    {
        if (!double.IsFinite(value) || value < 0) value = 0;
        if (cur < 0) { cur = second; curMax = value; return; }
        if (second < cur) return; // out of order: ignored (never erases history)
        if (second == cur) { curMax = Math.Max(curMax, value); return; }
        secs.Push(curMax);
        for (long s = cur + 1; s < second && s - cur <= MaxSeconds; s++) secs.Push(0); // gap (sleep): no traffic
        cur = second; curMax = value;
    }

    /// <summary>Seconds per point for the window (1 s up to 120 s; beyond that window/120 rounded up).</summary>
    public static int BucketSeconds(int window) => Math.Max(1, (int)Math.Ceiling(window / (double)MaxPoints));
    public static int Points(int window) => Math.Max(2, window / BucketSeconds(window));

    private bool TryGet(long s, out double v)
    {
        if (s == cur) { v = curMax; return true; }
        long back = cur - s; // 1 = last closed second
        if (back >= 1 && back <= secs.Count) { v = secs[secs.Count - (int)back]; return true; }
        v = 0; return false;
    }

    /// <summary>Up to Points+1 values (the oldest sits outside the area so scrolling does not open a hole); the last is the bucket in progress.</summary>
    public double[] View(int window)
    {
        if (cur < 0) return [];
        int b = BucketSeconds(window), n = Points(window);
        long last = cur / b;
        var list = new List<double>(n + 1);
        for (long k = last - n; k <= last; k++)
        {
            bool any = false; double m = 0;
            for (long s = k * b; s < (k + 1) * b; s++)
                if (TryGet(s, out var v)) { any = true; if (v > m) m = v; }
            if (any) list.Add(m); else if (list.Count > 0) list.Add(0);
        }
        return list.ToArray();
    }
}
