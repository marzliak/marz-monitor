using MonitorTray.Sensors;
using Xunit;

namespace MonitorTray.Core.Tests;

public class SensorPacingTests
{
    [Theory]
    [InlineData(500, true, 500)]
    [InlineData(500, false, 1000)]
    [InlineData(1000, false, 1000)]
    [InlineData(2000, false, 2000)]
    [InlineData(2000, true, 2000)]
    public void LoopPeriodIsAtLeastOneSecondWhenWindowsAreClosed(int refresh, bool open, int expected) =>
        Assert.Equal(expected, SensorService.PeriodMs(refresh, open));

    [Fact]
    public void LhmAtMostOncePerSecond()
    {
        Assert.True(SensorService.LhmDue(null));
        Assert.False(SensorService.LhmDue(500));
        Assert.True(SensorService.LhmDue(960));
        Assert.True(SensorService.LhmDue(1500));
    }
}
