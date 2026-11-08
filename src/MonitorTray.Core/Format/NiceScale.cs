namespace MonitorTray.Core.Format;

public static class NiceScale
{
    /// <summary>Menor valor 1/2/5/10 × 10^k que cobre v (docs/06 §1).</summary>
    public static double Nice(double v)
    {
        if (double.IsNaN(v) || v < 1) v = Math.Max(double.IsNaN(v) ? 1 : v, 1);
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1d, 2d, 5d, 10d }) if (m * p >= v) return m * p;
        return 10 * p;
    }

    public static (double Down, double Up) NetScales(IReadOnlyList<double> down, IReadOnlyList<double> up, bool shared)
    {
        double dMax = 1, uMax = 1;
        foreach (var d in down) if (d > dMax) dMax = d;
        foreach (var u in up) if (u > uMax) uMax = u;
        // Em bits/s o teto é "redondo" em bits (100/200/500 Mbps), convertido de volta para bytes/s.
        double N(double v) => Formatter.NetBits ? Nice(v * 8) / 8 : Nice(v);
        return shared ? (N(Math.Max(dMax, uMax)), N(Math.Max(dMax, uMax))) : (N(dMax), N(uMax));
    }
}
