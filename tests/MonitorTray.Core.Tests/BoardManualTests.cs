using MonitorTray.Core.Fans;
using Xunit;

namespace MonitorTray.Core.Tests;

public class BoardManualTests
{
    [Fact]
    public void HoldCurveKeepsPwmUntil80AndFullFrom85()
    {
        var c = BoardFanOff.HoldCurve(4, 128);
        Assert.Equal([[60, 128], [70, 128], [80, 128], [85, 255]], c);
        Assert.Equal(BoardFanOff.ProtectionCurve(4), BoardFanOff.HoldCurve(4, 0));
        Assert.Equal(255, BoardFanOff.HoldCurve(2, 999)[0][1]);
    }

    [Fact]
    public void ManualIsClampedAndLeavesOff()
    {
        var o = new BoardFanOff();
        Assert.True(o.TurnOff("Chassis Fan 2", 50, 50));
        Assert.Equal(20, o.SetManual("Chassis Fan 2", 5));
        Assert.False(o.IsOff("Chassis Fan 2"));
        Assert.Equal(20, o.ManualPercent("Chassis Fan 2"));
        Assert.Equal(100, o.SetManual("Chassis Fan 2", 140));
    }

    [Fact]
    public void OffClearsManual()
    {
        var o = new BoardFanOff();
        o.SetManual("Chassis Fan 3", 40);
        Assert.True(o.TurnOff("Chassis Fan 3", 50, 50));
        Assert.Null(o.ManualPercent("Chassis Fan 3"));
    }

    [Fact]
    public void ModesSkipManualAndFullClearsIt()
    {
        var o = new BoardFanOff();
        o.SetManual("Chassis Fan 1", 40);
        Assert.Equal(["Chassis Fan 2"], o.ModeTargets("standard", ["Chassis Fan 1", "Chassis Fan 2"]));
        Assert.Equal(40, o.ManualPercent("Chassis Fan 1"));
        var full = o.ModeTargets("full", ["Chassis Fan 2"]);
        Assert.Contains("Chassis Fan 1", full);
        Assert.False(o.AnyManual);
    }

    [Fact]
    public void ClearManualReturnsToCurve()
    {
        var o = new BoardFanOff();
        o.SetManual("Chassis Fan 4", 60);
        Assert.True(o.ClearManual("Chassis Fan 4"));
        Assert.False(o.ClearManual("Chassis Fan 4"));
        Assert.Null(o.ManualPercent("Chassis Fan 4"));
    }
}
