namespace MonitorTray.Ryujin;

public readonly record struct CurveNode(double TempC, double Percent);
public readonly record struct RpmPoint(int Percent, int Rpm);

/// <summary>Armoury Crate curves (default.json) and %/rpm conversions (Auto Tuning rpmMappingTable).</summary>
public static class RyujinCurves
{
    public enum Preset { Silent, Standard, Turbo, FullSpeed }
    public enum Fan { Embedded, Pump, Radiator }

    static readonly CurveNode[] EmbSilent = [new(60, 0), new(80, 40), new(98, 100)];
    static readonly CurveNode[] EmbStandard = [new(60, 0), new(75, 40), new(93, 100)];
    static readonly CurveNode[] EmbTurbo = [new(60, 40), new(65, 70), new(93, 100)];
    static readonly CurveNode[] PumpSilent = [new(20, 20), new(70, 70), new(75, 100)];
    static readonly CurveNode[] PumpStandard = [new(20, 20), new(65, 70), new(70, 100)];
    static readonly CurveNode[] PumpTurbo = [new(20, 20), new(55, 70), new(83, 100)];
    static readonly CurveNode[] Full = [new(0, 100), new(100, 100), new(100, 100)];

    public static CurveNode[] CurveFor(Fan fan, Preset preset) => preset switch
    {
        Preset.FullSpeed => Full,
        Preset.Silent => fan == Fan.Embedded ? EmbSilent : PumpSilent,
        Preset.Standard => fan == Fan.Embedded ? EmbStandard : PumpStandard,
        _ => fan == Fan.Embedded ? EmbTurbo : PumpTurbo,
    };

    /// <summary>Fixed-mode % per preset.</summary>
    public static int FixedPercent(Fan fan, Preset preset) => preset switch
    {
        Preset.Silent => fan == Fan.Embedded ? 30 : 40,
        Preset.Standard => 60,
        Preset.Turbo => 80,
        _ => 100,
    };

    /// <summary>Linear interpolation between nodes; outside the range it holds the end value (like AC).</summary>
    public static double Interpolate(IReadOnlyList<CurveNode> nodes, double tempC)
    {
        if (nodes.Count == 0) return 0;
        if (tempC <= nodes[0].TempC) return nodes[0].Percent;
        for (int i = 1; i < nodes.Count; i++)
        {
            var a = nodes[i - 1]; var b = nodes[i];
            if (tempC <= b.TempC)
                return b.TempC == a.TempC ? b.Percent : a.Percent + (b.Percent - a.Percent) * (tempC - a.TempC) / (b.TempC - a.TempC);
        }
        return nodes[^1].Percent;
    }

    public static double RpmToPercent(IReadOnlyList<RpmPoint> table, double rpm)
    {
        if (table.Count == 0) return 0;
        if (rpm <= table[0].Rpm) return table[0].Percent;
        for (int i = 1; i < table.Count; i++)
        {
            var a = table[i - 1]; var b = table[i];
            if (rpm <= b.Rpm)
                return b.Rpm == a.Rpm ? b.Percent : a.Percent + (b.Percent - a.Percent) * (rpm - a.Rpm) / (double)(b.Rpm - a.Rpm);
        }
        return table[^1].Percent;
    }

    public static double PercentToRpm(IReadOnlyList<RpmPoint> table, double percent)
    {
        if (table.Count == 0) return 0;
        if (percent <= table[0].Percent) return table[0].Rpm;
        for (int i = 1; i < table.Count; i++)
        {
            var a = table[i - 1]; var b = table[i];
            if (percent <= b.Percent)
                return a.Rpm + (b.Rpm - a.Rpm) * (percent - a.Percent) / (b.Percent - a.Percent);
        }
        return table[^1].Rpm;
    }

    /// <summary>Radiator duty (0x21) = % * 255 / 100, truncated like AC does.</summary>
    public static byte RadiatorDuty(double percent) => (byte)Math.Clamp((int)(Math.Round(percent) * 255 / 100), 0, 255);
}
