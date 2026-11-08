using MonitorTray.Ryujin;
using Xunit;

namespace MonitorTray.Core.Tests;

public class RyujinFanTests
{
    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // Captured Armoury Crate frames (hook8, 2026-11-02): pump 62 %/embedded 60 %, radiator 0x9e.
    [Fact] public void PumpFrameMatchesArmouryCrate() => Assert.StartsWith("ec1a003e3c00", Hex(RyujinProtocol.BuildPump(62, 60)));
    [Fact] public void RadiatorFrameMatchesArmouryCrate() => Assert.StartsWith("ec2100009e00", Hex(RyujinProtocol.BuildRadiator(0x9E)));
    [Fact] public void PumpFrameClamps() { var f = RyujinProtocol.BuildPump(150, -5); Assert.Equal(100, f[3]); Assert.Equal(0, f[4]); }

    [Fact]
    public void FanCommandsAreSeparateFromDisplay()
    {
        Assert.True(RyujinProtocol.IsFanCommand(0x1A)); Assert.True(RyujinProtocol.IsFanCommand(0x21));
        Assert.False(RyujinProtocol.IsFanCommand(0x53)); Assert.False(RyujinProtocol.IsDisplayCommand(0x21));
    }

    // Captured: CPU ~48 °C → Standard 51 %, Turbo 60 %, Silent 48 %.
    [Theory]
    [InlineData(RyujinCurves.Preset.Silent, 48)] [InlineData(RyujinCurves.Preset.Standard, 51)] [InlineData(RyujinCurves.Preset.Turbo, 60)]
    public void SmartMatchesArmouryCrate(RyujinCurves.Preset p, int pct)
    {
        var c = new CoolerConfig { Enabled = true, Pump = new() { Preset = p }, Radiator = new() { Preset = p } };
        var t = CoolerLogic.Compute(c, 48, 35);
        Assert.Equal(pct, t.PumpPercent); Assert.Equal(pct, t.RadiatorPercent);
    }

    [Fact]
    public void DefaultsAreUsersCurrentSetup()
    {
        var t = CoolerLogic.Compute(new CoolerConfig(), 48, 35);
        Assert.Equal(51, t.PumpPercent); Assert.Equal(51, t.RadiatorPercent); Assert.Equal(60, t.EmbeddedPercent);
        Assert.False(new CoolerConfig().Enabled);
    }

    [Fact]
    public void PumpNeverBelowFloor()
    {
        var c = new CoolerConfig { Pump = new() { Mode = FanMode.Fixed, FixedPercent = 0 } }.Normalized();
        Assert.Equal(CoolerSafety.PumpMinPercent, c.Pump.FixedPercent);
        Assert.True(CoolerLogic.Compute(new CoolerConfig { Pump = new() { Preset = RyujinCurves.Preset.Silent } }, 10, 30).PumpPercent >= CoolerSafety.PumpMinPercent);
    }

    [Fact]
    public void HotWaterForcesFull()
    {
        var c = new CoolerConfig { Pump = new() { Mode = FanMode.Fixed, FixedPercent = 40 }, Radiator = new() { Mode = FanMode.Fixed, FixedPercent = 20 } };
        var t = CoolerLogic.Compute(c, 40, CoolerSafety.WaterPanicC + 0.5);
        Assert.Equal(100, t.PumpPercent); Assert.Equal(100, t.RadiatorPercent);
        Assert.StartsWith("water", t.Reason);
    }

    [Fact]
    public void MissingSourceUsesSafeValue()
    {
        var t = CoolerLogic.Compute(new CoolerConfig(), null, 35);
        Assert.Equal(CoolerSafety.NoSourcePercent, t.RadiatorPercent);
        Assert.NotNull(t.Reason);
    }

    [Fact] public void FullSpeedPreset() => Assert.Equal(100, CoolerLogic.PercentFor(new() { Preset = RyujinCurves.Preset.FullSpeed }, RyujinCurves.Fan.Radiator, 20));

    [Fact]
    public void CustomCurveOverridesPreset()
    {
        var s = new CoolerFanSetting { Custom = [new(30, 30), new(60, 90)] };
        Assert.Equal(60, CoolerLogic.PercentFor(s, RyujinCurves.Fan.Radiator, 45));
    }

    [Theory]
    [InlineData(40, 80, 50)] [InlineData(80, 40, 77)] [InlineData(50, 52, 52)] [InlineData(50, 49, 49)]
    public void SmoothUpFastDownSlow(int cur, int target, int expected) => Assert.Equal(expected, CoolerLogic.Smooth(cur, target));

    [Fact]
    public void ConfigRoundTrip()
    {
        var c = new CoolerConfig { Enabled = true, Radiator = new() { Preset = RyujinCurves.Preset.Turbo }, RadiatorTable = [new(0, 0), new(100, 1890)] };
        var r = CoolerConfig.Parse(c.ToJson());
        Assert.True(r.Enabled); Assert.Equal(RyujinCurves.Preset.Turbo, r.Radiator.Preset); Assert.Equal(2, r.RadiatorTable.Count);
        Assert.False(CoolerConfig.Parse("{junk").Enabled);
    }
}

public class CoolerModesTests
{
    static CoolerConfig Preset(RyujinCurves.Preset p, bool enabled = true) =>
        CoolerModes.WithPreset(new CoolerConfig { Enabled = enabled, Embedded = new() { Mode = FanMode.Fixed, FixedPercent = 45 } }, p);

    static readonly CoolerConfig Own = new()
    {
        Enabled = true,
        Pump = new() { Mode = FanMode.Fixed, FixedPercent = 55 },
        Radiator = new() { Custom = [new(30, 20), new(70, 90)] },
    };

    [Fact]
    public void UniformPresetOnlyWhenBothFollowTheSamePreset()
    {
        Assert.Equal(RyujinCurves.Preset.Silent, CoolerModes.UniformPreset(Preset(RyujinCurves.Preset.Silent)));
        Assert.Null(CoolerModes.UniformPreset(Own));
        Assert.Null(CoolerModes.UniformPreset(Preset(RyujinCurves.Preset.Silent) with { Radiator = new() { Preset = RyujinCurves.Preset.Turbo } }));
    }

    [Fact]
    public void CurvesGoBackToTheKeptOwnConfig()
    {
        var silent = Preset(RyujinCurves.Preset.Silent);
        var c = CoolerModes.Curves(silent, Own);
        Assert.True(c.Enabled);
        Assert.Equal(Own.Pump, c.Pump); Assert.Equal(Own.Radiator.Custom, c.Radiator.Custom);
        Assert.Equal(Own.Embedded, c.Embedded); // embedded fan goes back to the kept own config
        Assert.Null(CoolerModes.UniformPreset(c)); // CURVES lights up
    }

    [Fact]
    public void CurvesWithoutKeptConfigUseDefaultCurvesNeverAPreset()
    {
        // User's case: pump and radiator on SILENT, nothing kept → default curves (STD points), without opening a window.
        foreach (var kept in new CoolerConfig?[] { null, Preset(RyujinCurves.Preset.Turbo) })
        {
            var c = CoolerModes.Curves(Preset(RyujinCurves.Preset.Silent), kept);
            Assert.True(c.Enabled);
            Assert.Null(CoolerModes.UniformPreset(c));
            Assert.Equal(FanMode.Smart, c.Pump.Mode);
            Assert.Equal(RyujinCurves.CurveFor(RyujinCurves.Fan.Pump, RyujinCurves.Preset.Standard), c.Pump.Custom);
            Assert.Equal(RyujinCurves.CurveFor(RyujinCurves.Fan.Radiator, RyujinCurves.Preset.Standard), c.Radiator.Custom);
            // Same % as STD: the default curve is the Armoury Crate one.
            Assert.Equal(CoolerLogic.Compute(Preset(RyujinCurves.Preset.Standard), 48, 35), CoolerLogic.Compute(c, 48, 35));
        }
    }

    [Fact]
    public void CurvesWhenAlreadyOwnOnlyEnablesControl()
    {
        var off = Own with { Enabled = false };
        Assert.Equal(Own, CoolerModes.Curves(off, null));
    }

    [Fact]
    public void KeepOnlyTheOwnConfigBeforeAPreset()
    {
        Assert.Equal(Own, CoolerModes.ToKeep(Own));
        Assert.Null(CoolerModes.ToKeep(Preset(RyujinCurves.Preset.FullSpeed))); // preset → keeps the stored one
        var p = CoolerModes.WithPreset(Own, RyujinCurves.Preset.Turbo);
        Assert.Equal(RyujinCurves.Preset.Turbo, CoolerModes.UniformPreset(p));
        Assert.Equal(RyujinCurves.Preset.Turbo, p.Embedded.Preset); Assert.Equal(FanMode.Smart, p.Embedded.Mode); // preset applies to the embedded fan too
        Assert.Equal(Own.Enabled, p.Enabled);
    }

    [Fact]
    public void PresetKeepsOwnEmbeddedForCurves()
    {
        var silent = Preset(RyujinCurves.Preset.Silent) with { Embedded = new() { Mode = FanMode.Fixed, FixedPercent = 35 } };
        var keep = CoolerModes.ToKeep(silent, Own)!;
        Assert.Equal(35, keep.Embedded.FixedPercent);
        Assert.Equal(Own.Pump, keep.Pump);
        Assert.Null(CoolerModes.ToKeep(Preset(RyujinCurves.Preset.Silent), Own)); // embedded on the preset: nothing new
    }

    [Fact]
    public void UndoGameRestoresOnlyWhatGameSet()
    {
        var before = Own;
        var game = CoolerModes.WithPreset(Own, RyujinCurves.Preset.FullSpeed);
        var userChanged = game with { Radiator = new() { Mode = FanMode.Fixed, FixedPercent = 40 } };
        var r = CoolerModes.UndoGame(userChanged, before, 100);
        Assert.Equal(before.Pump, r.Pump); Assert.Equal(before.Embedded, r.Embedded);
        Assert.Equal(40, r.Radiator.FixedPercent); // changed by the user during game mode: stays
        var g85 = Own with { Pump = new() { Mode = FanMode.Fixed, FixedPercent = 85 }, Embedded = new() { Mode = FanMode.Fixed, FixedPercent = 85 } };
        Assert.Equal(before.Embedded, CoolerModes.UndoGame(g85, before, 85).Embedded);
    }

    [Fact]
    public void EmbeddedStuckInFullGoesBackToStandard()
    {
        var stuck = Own with { Embedded = new() { Mode = FanMode.Smart, Preset = RyujinCurves.Preset.FullSpeed } };
        var fixedUp = CoolerModes.UnstickEmbedded(stuck)!;
        Assert.Equal(RyujinCurves.Preset.Standard, fixedUp.Embedded.Preset);
        Assert.Null(CoolerModes.UnstickEmbedded(Own));
        // User's case: own curve with a FULL preset underneath → curve kept, leaves FULL.
        var withCurve = stuck with { Embedded = stuck.Embedded with { Custom = [new(68, 34), new(80, 60), new(100, 100)] } };
        var r = CoolerModes.UnstickEmbedded(withCurve)!;
        Assert.Equal(3, r.Embedded.Custom.Count);
        Assert.Equal(34, CoolerLogic.PercentFor(r.Embedded, RyujinCurves.Fan.Embedded, 50));
    }

    [Fact]
    public void KeptConfigSurvivesJson()
    {
        var back = CoolerConfig.Parse(Own.Normalized().ToJson());
        Assert.Equal(FanMode.Fixed, back.Pump.Mode); Assert.Equal(55, back.Pump.FixedPercent);
        Assert.Equal(Own.Radiator.Custom, back.Radiator.Custom);
    }
}

public class GameModeSettingsTests
{
    [Fact]
    public void DefaultsEverythingAt100()
    {
        var s = MonitorTray.Core.Model.Settings.Default;
        Assert.Equal(MonitorTray.Core.Model.Settings.GameTargetIds, s.GameTargets);
        Assert.Equal(100, s.GameGpuPercent); Assert.Equal(100, s.GameCoolerPercent); Assert.Null(s.GameBoardFans);
    }

    [Fact]
    public void RoundTripAndValidation()
    {
        var s = MonitorTray.Core.Model.Settings.Default with { GameTargets = ["gpu", "pump"], GameBoardFans = ["Chassis Fan 1"], GameGpuPercent = 85, GameCoolerPercent = 70 };
        var r = MonitorTray.Core.Model.Settings.Parse(s.ToJson());
        Assert.Equal(s, r);
        var bad = MonitorTray.Core.Model.Settings.Parse("{\"version\":1,\"gameTargets\":[\"gpu\",\"xyz\"],\"gameGpuPercent\":33}");
        Assert.Equal(["gpu"], bad.GameTargets); Assert.Equal(100, bad.GameGpuPercent);
    }
}
