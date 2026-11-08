using MonitorTray.Ryujin;
using Xunit;

namespace MonitorTray.Core.Tests;

public class TempSmootherTests
{
    [Fact]
    public void FirstSampleIsTakenAsIs()
    {
        var s = new TempSmoother();
        Assert.Equal(50, s.Next(50, 1));
    }

    [Fact]
    public void SpikeIsDamped()
    {
        var s = new TempSmoother(5);
        s.Next(50, 1);
        var v = s.Next(80, 1)!.Value;
        Assert.InRange(v, 54, 56); // 1 s de um pico de +30 °C move ~5 °C
    }

    [Fact]
    public void StepConvergesInAFewTau()
    {
        var s = new TempSmoother(5);
        s.Next(40, 1);
        double v = 0;
        for (int i = 0; i < 25; i++) v = s.Next(70, 1)!.Value;
        Assert.InRange(v, 69, 70);
    }

    [Fact]
    public void ZeroDtDoesNotMove()
    {
        var s = new TempSmoother(5);
        s.Next(40, 1);
        Assert.Equal(40, s.Next(90, 0));
    }

    [Fact]
    public void MissingReadingResets()
    {
        var s = new TempSmoother(5);
        s.Next(40, 1);
        Assert.Null(s.Next(null, 1));
        Assert.Equal(75, s.Next(75, 1));
    }
}
