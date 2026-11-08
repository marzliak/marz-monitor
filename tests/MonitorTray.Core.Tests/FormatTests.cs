using MonitorTray.Core.Format;
using Xunit;

namespace MonitorTray.Core.Tests;

public class FormatTests
{
    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(3, 5)] [InlineData(7, 10)]
    [InlineData(19.5, 20)] [InlineData(1000, 1000)] [InlineData(1001, 2000)]
    public void Nice(double v, double expected) => Assert.Equal(expected, NiceScale.Nice(v));

    [Theory]
    [InlineData(54.04, "degC", "54.0 °C")]
    [InlineData(35.4, "%", "35 %")]
    [InlineData(3.14, "%", "3.1 %")]
    [InlineData(512, "B/s", "512 B/s")]
    [InlineData(2048, "B/s", "2 KB/s")]
    [InlineData(1048576 * 11.3, "B/s", "11.3 MB/s")]
    [InlineData(800, "MiB", "800 MB")]
    [InlineData(2048, "MiB", "2.0 GB")]
    [InlineData(582.67, "GiB", "582.7 GB")]
    [InlineData(2048, "GiB", "2.00 TB")]
    [InlineData(810, "MHz", "810 MHz")]
    [InlineData(5700, "MHz", "5.70 GHz")]
    [InlineData(1.2341, "V", "1.234 V")]
    [InlineData(14.2, "A", "14.20 A")]
    [InlineData(120.04, "W", "120.0 W")]
    [InlineData(596.4, "RPM", "596 RPM")]
    [InlineData(0, "L/h", "0.0 L/h")]
    [InlineData(7395, "1", "7395 1")]
    [InlineData(16.5, "ns", "16.5 ns")]
    [InlineData(0.125, "ns", "0.13 ns")]
    [InlineData(10, "ns", "10 ns")]
    public void Units(double v, string unit, string expected) => Assert.Equal(expected, Formatter.Format(v, unit).ToString());

    [Fact]
    public void NullIsDash()
    {
        Assert.Equal("—", Formatter.Format(null, "degC").ToString());
        Assert.Equal("—", Formatter.Format(double.NaN, "W").ToString());
    }

    [Theory]
    [InlineData(5100, "MHz", "5.1")] [InlineData(1.234, "V", "1.23")] [InlineData(54.6, "degC", "55")]
    public void Short(double v, string unit, string expected) => Assert.Equal(expected, Formatter.Short(v, unit));

    [Theory]
    [InlineData(0, "0", "KB/s")] [InlineData(1023 * 1024, "1023", "KB/s")] [InlineData(1024 * 1024 * 19.5, "19.5", "MB/s")]
    public void Net(double bps, string v, string u) { var f = Formatter.Net(bps); Assert.Equal(v, f.Value); Assert.Equal(u, f.Unit); }

    [Fact]
    public void DiskAlwaysMBps() => Assert.Equal("54.0", Formatter.DiskMBps(54 * 1048576));

    [Fact]
    public void UsedTotalFollowsTotalUnit()
    {
        const ulong G = 1024UL * 1024 * 1024;
        Assert.Equal("418/931 GB", Formatter.UsedTotal(418 * G, 931 * G));
        Assert.Equal("1.20/1.82 TB", Formatter.UsedTotal((ulong)(1.2 * 1024 * G), (ulong)(1.82 * 1024 * G)));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3090", "RTX 3090")]
    [InlineData("NVIDIA GeForce RTX 5090", "RTX 5090")]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", "RTX 4070 Ti SUPER")]
    [InlineData("NVIDIA RTX A6000", "RTX A6000")]
    [InlineData("AMD Radeon RX 7900 XTX", "RX 7900 XTX")]
    [InlineData("NVIDIA", "NVIDIA")]
    public void GpuShortName(string raw, string expected) => Assert.Equal(expected, Formatter.ShortGpuName(raw));

    [Fact]
    public void RefreshLabels()
    {
        Assert.Equal("0.5s", Formatter.RefreshLabel(500));
        Assert.Equal("1s", Formatter.RefreshLabel(1000));
        Assert.Equal("2s", Formatter.RefreshLabel(2000));
    }

    [Fact]
    public void RunsBreakOnNullAndNeverInterpolate()
    {
        float?[] v = [1, 2, null, 3, 4, null, 5];
        var runs = ChartGeometry.Runs(v, 60, 20, null, false);
        Assert.Equal(2, runs.Count); // o "5" isolado não forma trecho
        Assert.Equal(0, runs[0][0].X); Assert.Equal(10, runs[0][1].X);
        Assert.Equal(30, runs[1][0].X);
    }

    [Fact]
    public void SmoothNeverOvershootsNeighbours()
    {
        var pts = new[] { new Pt(0, 10), new Pt(10, 50), new Pt(20, 12), new Pt(30, 12), new Pt(40, 60), new Pt(50, 59) };
        var segs = ChartGeometry.Smooth(pts);
        Assert.Equal(pts.Length - 1, segs.Count);
        for (int i = 0; i < segs.Count; i++)
        {
            double lo = Math.Min(pts[i].Y, pts[i + 1].Y), hi = Math.Max(pts[i].Y, pts[i + 1].Y);
            Assert.InRange(segs[i].C1.Y, lo - 1e-9, hi + 1e-9);
            Assert.InRange(segs[i].C2.Y, lo - 1e-9, hi + 1e-9);
            Assert.Equal(pts[i + 1], segs[i].P);
        }
    }

    [Fact]
    public void RunsPlaceNewestAtRightEdgeWithExtraPointOffscreen()
    {
        float?[] v = [1, 2, 3, 4];
        var runs = ChartGeometry.Runs(v, 10, -10, 20, 0, 10);
        Assert.Equal(-10, runs[0][0].X);
        Assert.Equal(20, runs[0][^1].X);
    }

    [Fact]
    public void MirrorScalesAndClamps()
    {
        var pts = ChartGeometry.Mirror([0, 50, 200], 3, 100, 280, 32, 30, 1);
        Assert.Equal(5, pts.Count);
        Assert.Equal(32, pts[1].Y); Assert.Equal(17, pts[2].Y); Assert.Equal(2, pts[3].Y);
        var up = ChartGeometry.Mirror([100], 3, 100, 280, 32, 30, -1);
        Assert.Equal(62, up[1].Y);
    }

    [Fact]
    public void GpuFansShowRpmOnlyWhenSpinning()
    {
        Assert.Equal("52% 1240  50% 1180", Formatter.GpuFans([(52u, 1240u), (50u, 1180u)]));
        Assert.Equal("0%  30%", Formatter.GpuFans([(0u, 0u), (30u, null)])); // rpm 0 ou sem leitura: só o %
        Assert.Equal("— 900", Formatter.GpuFans([(null, 900u)]));
        Assert.Equal("", Formatter.GpuFans([]));
    }
}


[Collection("NetBits")]
public class NetBitsTests
{
    [Fact]
    public void BitsModeUsesDecimalBitUnitsAndSettingRoundTrips()
    {
        try
        {
            Formatter.NetBits = true;
            Assert.Equal(new Formatted("932.0", "Mbps"), Formatter.Net(116.5e6));
            Assert.Equal(new Formatted("2.50", "Gbps"), Formatter.Net(312.5e6));
            Assert.Equal(new Formatted("80", "Kbps"), Formatter.Net(10_000));
            Assert.Equal(new Formatted("8.0", "Mbps"), Formatter.Format(1e6, "B/s"));
            var (sd, _) = NiceScale.NetScales([12e6], [1], false); // 96 Mbps → teto 100 Mbps
            Assert.Equal(new Formatted("100.0", "Mbps"), Formatter.Net(sd));
            Formatter.NetBits = false;
            Assert.Equal("MB/s", Formatter.Net(116.5e6).Unit);
        }
        finally { Formatter.NetBits = false; }
        var st = MonitorTray.Core.Model.Settings.Parse(new MonitorTray.Core.Model.Settings { NetUnit = "bits" }.ToJson());
        Assert.Equal("bits", st.NetUnit);
        Assert.Equal("bytes", MonitorTray.Core.Model.Settings.Parse("{\"version\":1,\"netUnit\":\"xx\"}").NetUnit);
    }
}
