using MonitorTray.Core.Keyboard;
using MonitorTray.Oled;
using Xunit;

namespace MonitorTray.Core.Tests;

public class WheelStateTests
{
    private static WheelState Make() { var s = new WheelState(); s.Configure([5000, 5000, 5000, 5000], 0); return s; }

    // Mesmos casos do self-test do GPU by MarzOps (R33Core.SelfTests).
    [Fact]
    public void SingleWaitsDoublePinsAndAutoRotates()
    {
        var a = Make();
        Assert.Equal(1000, a.Snapshot(0).Interval);
        a.Press(100); a.Release();
        Assert.Equal(0, a.Snapshot(449).Page);
        Assert.Equal(1, a.Snapshot(450).Page);
        a.Press(1000); a.Release(); a.Press(1150); a.Release();
        Assert.True(a.Snapshot(1160).Pinned); Assert.Equal(1, a.Snapshot(1160).Page);
        Assert.Equal(1, a.Snapshot(6200).Page);
        a.Press(6400); a.Release();
        Assert.Equal(2, a.Snapshot(6760).Page); Assert.True(a.Snapshot(6760).Pinned);
        a.Press(7000); a.Release(); a.Press(7150); a.Release();
        Assert.False(a.Snapshot(7160).Pinned);
        Assert.Equal(3, a.Snapshot(12150).Page);
        a.Next(12200); Assert.Equal(0, a.Snapshot(12200).Page);
    }

    [Fact]
    public void RepeatBounceAndStaleGesturesIgnored()
    {
        var b = Make(); b.Press(100); Assert.False(b.Press(130)); b.Release(); Assert.Equal(1, b.Snapshot(450).Singles);
        var c = Make(); c.Press(100); c.Release(); Assert.False(c.Press(120)); c.Release(); Assert.Equal(1, c.Snapshot(450).Singles);
        var f = Make(); f.Press(100); f.Release(); Assert.Equal(0, f.Snapshot(20000).Singles);
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_1038&PID_1640&MI_03&Col01#abc", true)]
    [InlineData(@"\\?\HID#VID_1038&PID_1640&MI_03&Col02#abc", false)]
    [InlineData(@"\\?\HID#VID_1038&PID_16400&MI_03&COL01#abc", false)]
    [InlineData("sem VID/PID", false)]
    public void WheelEndpointFilter(string path, bool ok) => Assert.Equal(ok, DeviceFilter.Matches(path));

    [Fact]
    public void MuteCompensationOnlyAfterOwnClick()
    {
        var m = new MuteCorrelation(); m.Reset(false); m.Click(100);
        var x = m.Notification(115, true, false);
        Assert.NotNull(x); Assert.True(x!.Expected); Assert.False(x.Desired);
        m.Reset(false); Assert.Null(m.Notification(100, true, false)); // sem clique, não mexe
    }
}

public class OledComposerTests
{
    [Fact]
    public void AlignsPrefixesAndValuesAndCutsAt20()
    {
        var page = new OledPage { Lines = [KeyboardConfig.L("a", "CPU uso ", "%", 0), KeyboardConfig.L("b", "CPU temp ", " C", 0)] };
        var lines = OledComposer.Compose(page, id => id == "a" ? 7.4 : 61.2, _ => "?");
        Assert.Equal("CPU uso   7%", lines[0]);
        Assert.Equal("CPU temp 61 C", lines[1]);
        var longp = new OledPage { Lines = [KeyboardConfig.L("a", "um prefixo bem comprido ", " unidade", 2), new()] };
        var l2 = OledComposer.Compose(longp, _ => 1, _ => "");
        Assert.Equal(20, l2[0].Length); Assert.Equal(" ", l2[1]);
    }

    [Fact]
    public void MissingValueIsDashAndPinMarkerNeverTruncates()
    {
        var page = new OledPage { Lines = [KeyboardConfig.L("x", "GPU ", "%", 0), new()] };
        Assert.Equal("GPU -%", OledComposer.Compose(page, _ => null, _ => "")[0]);
        Assert.Equal("* GPU 5%", OledComposer.DecoratePinned(["GPU 5%", " "], true)[0]);
        var full = new string('9', 20);
        Assert.Equal(full, OledComposer.DecoratePinned([full, " "], true)[0]);
    }
}

public class KeyboardConfigTests
{
    [Fact]
    public void CorruptGivesDefaultAndRangesAreClamped()
    {
        Assert.Equal(KeyboardConfig.DefaultPages.Count, KeyboardConfig.Parse("{lixo").Pages.Count);
        var c = new KeyboardConfig { Pages = [new OledPage { DurationMs = 10, IconId = 999, Lines = [KeyboardConfig.L("a", new string('x', 40), "", 9)] }] };
        var n = KeyboardConfig.Parse(c.ToJson());
        Assert.Equal(1000, n.Pages[0].DurationMs); Assert.Equal(255, n.Pages[0].IconId);
        Assert.Equal(3, n.Pages[0].Lines[0].Decimals); Assert.Equal(20, n.Pages[0].Lines[0].Prefix.Length);
        Assert.Equal(2, n.Pages[0].Lines.Count);
    }

    [Fact]
    public void RoundTripKeepsPagesOptionsAndLimitIs20()
    {
        var c = new KeyboardConfig { WheelEnabled = false, MuteCompensation = false, Pages = Enumerable.Range(0, 25).Select(i => new OledPage { DurationMs = 3000 + i }).ToList() };
        var r = KeyboardConfig.Parse(c.ToJson());
        Assert.False(r.WheelEnabled); Assert.False(r.MuteCompensation);
        Assert.Equal(20, r.Pages.Count); Assert.Equal(3005, r.Pages[5].DurationMs);
    }

    [Theory]
    [InlineData("/intelcpu/0/clock/max-ghz", "calc:cpu-clock-max")]
    [InlineData("/ram/data/0", "calc:mem-used")]
    [InlineData("/ram/load/0", "calc:mem-load")]
    [InlineData("/gpu-nvidia/0/temperature/0", "/gpu-nvidia/0/temperature/0")]
    public void LegacyIdsFromGpuByMarzOps(string id, string expected) => Assert.Equal(expected, CalculatedSensors.MapLegacy(id));

    [Fact]
    public void CoreClocksIgnoreBusSpeed()
    {
        Model.RawSensor S(string n, float v) => new("/cpu/0/clock/" + n, "/cpu/0", "x", "Cpu", n, "Clock", "MHz", v);
        var (max, avg) = CalculatedSensors.CoreClocksGHz([S("P-Core #1", 6000), S("E-Core #1", 4000), S("Bus Speed", 100)]);
        Assert.Equal(6.0, max); Assert.Equal(5.0, avg);
    }
}

public class OledOutputTests
{
    private sealed class Fake : IOledTransport
    {
        public readonly List<string> Calls = [];
        public bool FailSend;
        public Task BindAsync(int icon, CancellationToken t) { Calls.Add("bind" + icon); return Task.CompletedTask; }
        public Task SendAsync(string[] l, CancellationToken t) { if (FailSend) throw new HttpRequestException("x"); Calls.Add("send:" + l[0]); return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken t) { Calls.Add("release"); return Task.CompletedTask; }
    }

    [Fact]
    public async Task BindsOnceRebindsOnIconAndReleasesAfterFailure()
    {
        var f = new Fake(); var o = new OledOutput(f);
        await o.PresentAsync(["a", "b"], 0, default); await o.PresentAsync(["c", "d"], 0, default);
        await o.PresentAsync(["e", "f"], 5, default);
        Assert.Equal(["bind0", "send:a", "send:c", "bind5", "send:e"], f.Calls);
        f.FailSend = true; Assert.False(await o.PresentAsync(["g", ""], 5, default));
        f.FailSend = false; await o.PresentAsync(["h", ""], 5, default);
        Assert.Equal(["release", "bind5", "send:h"], f.Calls.Skip(5));
        await o.SetEnabledAsync(false, default);
        Assert.Equal("release", f.Calls[^1]);
        Assert.False(await o.PresentAsync(["i", ""], 5, default));
    }

    [Theory]
    [InlineData("127.0.0.1:62632", true)]
    [InlineData("http://localhost:1/", true)]
    [InlineData("10.0.0.5:62632", false)]
    [InlineData("127.0.0.1:1/path", false)]
    public void GameSenseOnlyLocal(string a, bool ok)
    {
        if (ok) GameSenseClient.ValidateLocal(a);
        else Assert.ThrowsAny<Exception>(() => GameSenseClient.ValidateLocal(a));
    }
}
