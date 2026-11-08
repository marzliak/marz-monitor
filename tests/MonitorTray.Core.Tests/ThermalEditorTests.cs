using MonitorTray.Core.Fans;
using Xunit;

namespace MonitorTray.Core.Tests;

public class CurveEditTests
{
    static readonly CurveLimits Cooler = new(20, 60, 0, 2, 8);
    static readonly CurveLimits Pump = new(20, 100, 30, 2, 8);
    static readonly CurveLimits Board = new(20, 100, 0, 4, 4);
    static List<CurvePoint> P(params (double T, double P)[] p) => p.Select(x => new CurvePoint(x.T, x.P)).ToList();

    [Fact]
    public void NormalizeSortsRoundsClampsAndDropsRepeatedTemps()
    {
        var n = CurveEdit.Normalize(P((45.4, 100.6), (25, -3), (30.2, 35), (30, 40), (70, 80)), Cooler);
        Assert.Equal(P((25, 0), (30, 35), (45, 100), (60, 80)), n);
    }

    [Fact]
    public void NormalizeKeepsAtMostMaxPoints()
    {
        var many = Enumerable.Range(0, 12).Select(i => new CurvePoint(20 + i * 3, i * 8)).ToList();
        Assert.Equal(8, CurveEdit.Normalize(many, Cooler).Count);
    }

    [Fact]
    public void NormalizeAppliesPumpMinimum() => Assert.Equal(30, CurveEdit.Normalize(P((20, 20), (70, 100)), Pump)[0].Percent);

    [Fact]
    public void MoveStaysBetweenNeighboursAndInsideLimits()
    {
        var c = P((25, 25), (30, 35), (35, 55));
        Assert.Equal(new CurvePoint(34, 100), CurveEdit.Move(c, 1, 50, 140, Cooler)[1]); // não passa do vizinho (35 − 1)
        Assert.Equal(new CurvePoint(26, 0), CurveEdit.Move(c, 1, 10, -9, Cooler)[1]);   // nem do anterior (25 + 1)
        Assert.Equal(new CurvePoint(20, 25), CurveEdit.Move(c, 0, 3, 25, Cooler)[0]);   // primeiro: limite do eixo
        Assert.Equal(new CurvePoint(60, 55), CurveEdit.Move(c, 2, 99, 55, Cooler)[2]);  // último: limite do eixo
        Assert.Equal(30, CurveEdit.Move(P((20, 40), (70, 100)), 0, 20, 5, Pump)[0].Percent); // bomba ≥ 30%
        Assert.Equal(c, CurveEdit.Move(c, 7, 30, 30, Cooler)); // índice inválido: nada muda
    }

    [Fact]
    public void AddInsertsOnTheCurveSortedAndRespectsTheLimit()
    {
        var c = P((20, 20), (40, 60));
        var (pts, idx) = CurveEdit.Add(c, 30.4, Cooler)!.Value;
        Assert.Equal(P((20, 20), (30, 40), (40, 60)), pts);
        Assert.Equal(1, idx);
        Assert.Null(CurveEdit.Add(c, 40, Cooler));  // já tem ponto nessa temperatura
        Assert.Null(CurveEdit.Add(c, 75, Cooler));  // fora do eixo
        var full = Enumerable.Range(0, 8).Select(i => new CurvePoint(20 + i * 5, i * 10)).ToList();
        Assert.Null(CurveEdit.Add(full, 22, Cooler)); // 8 pontos: limite
        Assert.Null(CurveEdit.Add(P((20, 0), (40, 30), (60, 60), (80, 100)), 50, Board)); // chip: quantidade fixa
    }

    [Fact]
    public void AddAfterUsesTheMiddleOfTheNextSegment()
    {
        var c = P((20, 20), (40, 60), (60, 100));
        Assert.Equal(new CurvePoint(30, 40), CurveEdit.AddAfter(c, 0, Cooler)!.Value.Points[1]);
        Assert.Equal(new CurvePoint(50, 80), CurveEdit.AddAfter(c, 2, Cooler)!.Value.Points[2]); // último: trecho anterior
        Assert.Null(CurveEdit.AddAfter(P((20, 0), (21, 10)), 0, Cooler)); // sem espaço
    }

    [Fact]
    public void RemoveKeepsTheMinimumAndTheChipCount()
    {
        Assert.Equal(P((20, 20), (60, 100)), CurveEdit.Remove(P((20, 20), (40, 60), (60, 100)), 1, Cooler));
        Assert.Null(CurveEdit.Remove(P((20, 20), (60, 100)), 0, Cooler));
        Assert.Null(CurveEdit.Remove(P((20, 0), (40, 30), (60, 60), (80, 100)), 1, Board));
    }

    [Fact]
    public void ChangedCountsMovedAddedAndRemoved()
    {
        var a = P((25, 30), (30, 45), (35, 65), (40, 90), (45, 100));
        Assert.Equal(0, CurveEdit.Changed(a, a));
        Assert.Equal(2, CurveEdit.Changed(a, P((25, 25), (30, 35), (35, 65), (40, 90), (45, 100))));
        Assert.Equal(1, CurveEdit.Changed(a, a.Take(4).ToList()));
        Assert.Equal(1, CurveEdit.Changed(a, a.Append(new CurvePoint(50, 100)).ToList()));
    }

    [Fact]
    public void FitsRejectsPointsOutsideTheAxisOrUnordered()
    {
        Assert.True(CurveEdit.Fits(P((44, 21), (48, 29), (60, 62), (66, 100)), Board));
        Assert.False(CurveEdit.Fits(P((255, 40), (255, 40), (255, 40), (255, 40)), Board)); // modo fixo do Armoury Crate
        Assert.False(CurveEdit.Fits(P((20, 0), (60, 100)), Board)); // chip tem 4
    }

    [Fact]
    public void FixedClampsToTheMinimum()
    {
        Assert.Equal(30, CurveEdit.Fixed(12, Pump));
        Assert.Equal(100, CurveEdit.Fixed(140, Cooler));
        Assert.Equal(62, CurveEdit.Fixed(61.6, Cooler));
    }

    [Fact]
    public void PwmRoundTripKeepsEveryIntegerPercent()
    {
        for (int p = 0; p <= 100; p++) Assert.Equal(p, CurveEdit.PercentFromPwm(CurveEdit.PwmFromPercent(p)));
        Assert.Equal(255, CurveEdit.PwmFromPercent(100));
        Assert.Equal(0, CurveEdit.PercentFromPwm(-4));
    }

    [Fact]
    public void ChipCurveConvertsBothWays()
    {
        // Curva lida do chip no log do serviço: (44,54)(48,74)(60,158)(66,255).
        int[][] chip = [[44, 54], [48, 74], [60, 158], [66, 255]];
        var pts = CurveEdit.FromChip(chip);
        Assert.Equal(P((44, 21), (48, 29), (60, 62), (66, 100)), pts);
        Assert.Equal(new[] { 44, 54 }, CurveEdit.ToChip(pts)[0]);
        Assert.Equal(new[] { 66, 255 }, CurveEdit.ToChip(pts)[3]);
    }

    [Fact]
    public void InterpolateHoldsTheEnds()
    {
        var c = P((25, 25), (45, 100));
        Assert.Equal(25, CurveEdit.Interpolate(c, 10));
        Assert.Equal(62.5, CurveEdit.Interpolate(c, 35));
        Assert.Equal(100, CurveEdit.Interpolate(c, 80));
        Assert.Equal(0, CurveEdit.Interpolate([], 50));
    }
}

public class ThermalMapTests
{
    [Fact]
    public void IdsRoundTripAndDevices()
    {
        Assert.Equal("Chassis Fan 2", ThermalMap.BoardFan(ThermalMap.BoardId("Chassis Fan 2")));
        Assert.Null(ThermalMap.BoardFan(ThermalMap.PumpId));
        Assert.Equal(ThermalDevice.Board, ThermalMap.DeviceOf("mb:CPU Fan"));
        Assert.Equal(ThermalDevice.Cooler, ThermalMap.DeviceOf(ThermalMap.RadiatorId));
        Assert.Equal(ThermalDevice.Gpu, ThermalMap.DeviceOf(ThermalMap.GpuId));
        Assert.Null(ThermalMap.DeviceOf("x"));
    }

    [Theory]
    [InlineData("CPU Fan", "CPU_FAN")] [InlineData("Chassis Fan 4", "CHA_FAN4")] [InlineData("Water Pump+", "W_PUMP+")]
    [InlineData("AIO Pump", "AIO_PUMP")] [InlineData("CPU Optional Fan", "CPU_OPT")] [InlineData("Extra Fan", "EXTRA_FAN")]
    public void BoardHeaders(string ac, string header) => Assert.Equal(header, ThermalMap.BoardHeader(ac));

    [Fact]
    public void SourcesPerActuator()
    {
        const string water = "ryujin/water";
        Assert.Equal(ThermalMap.GpuSource, ThermalMap.SourceOf(ThermalMap.GpuId, water));
        Assert.Equal(ThermalMap.GpuSource, ThermalMap.SourceOf(ThermalMap.BoardId("Chassis Fan 1"), water)); // exaustor segue a GPU
        Assert.Equal(ThermalMap.CpuSource, ThermalMap.SourceOf(ThermalMap.BoardId("Chassis Fan 2"), water));
        Assert.Equal(ThermalMap.CpuSource, ThermalMap.SourceOf(ThermalMap.BoardId("CPU Fan"), water));
        Assert.Equal(water, ThermalMap.SourceOf(ThermalMap.RadiatorId, water));
        Assert.Equal(water, ThermalMap.SourceOf(ThermalMap.PumpId, water));
    }

    [Fact]
    public void AxisForLiquidAndOtherSourcesGrowsToFitPoints()
    {
        Assert.Equal((20, 60, 5), ThermalMap.Axis(true, [25, 45]));
        Assert.Equal((20, 75, 5), ThermalMap.Axis(true, [20, 65, 74]));
        Assert.Equal((20, 100, 10), ThermalMap.Axis(false, [60, 98]));
        Assert.Equal((0, 100, 10), ThermalMap.Axis(false, [0, 100, 100])); // preset FULL: (0,100)
        Assert.Equal((20, 120, 10), ThermalMap.Axis(false, [255])); // limite do eixo
    }

    [Fact]
    public void LimitsPerActuator()
    {
        var axis = (20d, 100d, 10d);
        var pump = ThermalMap.Limits(ThermalMap.PumpId, axis, 30);
        Assert.Equal((30d, 2, 8), (pump.PMin, pump.MinPoints, pump.MaxPoints));
        Assert.Equal(0, ThermalMap.Limits(ThermalMap.RadiatorId, axis, 30).PMin);
        var cha = ThermalMap.Limits(ThermalMap.BoardId("Chassis Fan 3"), axis, 30);
        Assert.True(cha.FixedCount); Assert.Equal(4, cha.MaxPoints); Assert.Equal(0, cha.PMin);
        Assert.Equal(ThermalMap.BoardLockedMinPercent, ThermalMap.Limits(ThermalMap.BoardId("CPU Fan"), axis, 30).PMin);
        Assert.Equal(ThermalMap.BoardLockedMinPercent, ThermalMap.Limits(ThermalMap.BoardId("AIO Pump"), axis, 30).PMin);
    }

    [Fact]
    public void BoardEditOnlyInCurvesWithTheFanOnAndNothingPending()
    {
        Assert.Null(ThermalMap.BoardEditBlock(true, null, null, false, false, true));
        Assert.NotNull(ThermalMap.BoardEditBlock(false, null, null, false, false, true));
        Assert.NotNull(ThermalMap.BoardEditBlock(true, "standard", null, false, false, true));
        Assert.NotNull(ThermalMap.BoardEditBlock(true, "full", null, false, false, true));
        Assert.NotNull(ThermalMap.BoardEditBlock(true, null, "turbo", false, false, true));
        Assert.Contains("religue", ThermalMap.BoardEditBlock(true, null, null, true, false, true));
        Assert.NotNull(ThermalMap.BoardEditBlock(true, null, null, false, true, true));
        Assert.NotNull(ThermalMap.BoardEditBlock(true, null, null, false, false, false));
    }

    [Fact]
    public void BoardStripShowsEveryConnectorWithMeasuredRpmOnly()
    {
        var rpm = new Dictionary<int, float> { [0] = 0, [1] = 1268, [2] = 834 };
        var ctl = new Dictionary<int, float> { [0] = 0, [1] = 80, [2] = 55, [5] = 100 };
        var items = ThermalMap.BoardStrip(["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Water Pump+"],
            f => f == "Chassis Fan 1" ? "Exaustor" : null, rpm, ctl, f => f == "Chassis Fan 1");
        Assert.Equal(["CPU_FAN", "CHA_FAN1", "CHA_FAN2", "W_PUMP+"], items.Select(i => i.Name));
        Assert.Equal((null, 80, false, true), (items[0].Rpm, items[0].Percent, items[0].Off, items[0].Locked)); // rpm da CPU não medido
        Assert.Equal((0, 0, true, "Exaustor"), (items[1].Rpm, items[1].Percent, items[1].Off, items[1].Nick));
        Assert.Equal((834, 55), (items[2].Rpm, items[2].Percent));
        Assert.True(items[3].Locked);
        Assert.Equal("mb:Chassis Fan 2", items[2].ActuatorId);
    }

    [Fact]
    public void CoolerAndGpuStrips()
    {
        var c = ThermalMap.CoolerStrip(2050, 59, [1268, 1281, 1276], 55, 3200, 60);
        Assert.Equal(["BOMBA", "RAD 1", "RAD 2", "RAD 3", "EMBUTIDO"], c.Select(i => i.Name));
        Assert.All(c.Skip(1).Take(3), i => Assert.Equal(ThermalMap.RadiatorId, i.ActuatorId));
        Assert.Equal(["BOMBA", "RADIADOR", "EMBUTIDO"], ThermalMap.CoolerStrip(null, null, [], null, null, null).Select(i => i.Name));
        var g = ThermalMap.GpuStrip([(53u, 1620u), (53u, null)]);
        Assert.Equal((1620, 53), (g[0].Rpm, g[0].Percent));
        Assert.Null(g[1].Rpm);
        Assert.All(g, i => Assert.Equal(ThermalMap.GpuId, i.ActuatorId));
    }
}
