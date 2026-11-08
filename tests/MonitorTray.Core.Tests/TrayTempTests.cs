using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using Xunit;

namespace MonitorTray.Core.Tests;

public class TrayTempTests
{
    private static readonly Dictionary<string, float> V = new() { ["cpu"] = 52, ["gpu"] = 77, ["liq"] = 32, ["vram"] = 60 };
    private static readonly string[] Cards = ["cpu", "gpu", "liq", "vram"];

    [Fact]
    public void NoChoiceShowsHottestCard()
    {
        var t = TempCards.ForTray([], Cards, V, 0, 3)!;
        Assert.Equal("gpu", t.Id);
    }

    [Fact]
    public void SingleChoiceAlwaysShown()
    {
        for (long ms = 0; ms < 20000; ms += 1000) Assert.Equal("liq", TempCards.ForTray(["liq"], Cards, V, ms, 3)!.Id);
    }

    [Fact]
    public void RotatesEveryNSeconds()
    {
        string[] c = ["cpu", "liq"];
        Assert.Equal("cpu", TempCards.ForTray(c, Cards, V, 0, 3)!.Id);
        Assert.Equal("cpu", TempCards.ForTray(c, Cards, V, 2999, 3)!.Id);
        Assert.Equal("liq", TempCards.ForTray(c, Cards, V, 3000, 3)!.Id);
        Assert.Equal("cpu", TempCards.ForTray(c, Cards, V, 6000, 3)!.Id);
    }

    [Fact]
    public void SkipsChoicesWithoutReading()
    {
        Assert.Equal("cpu", TempCards.ForTray(["ram", "cpu"], Cards, V, 3000, 3)!.Id);
    }

    [Fact]
    public void AlertLevelIsWorstEvenIfNotShown()
    {
        var t = TempCards.ForTray(["liq"], Cards, V, 0, 3)!; // GPU 77 °C está perto do limite (80)
        Assert.Equal("liq", t.Id);
        Assert.Equal(TempLevel.Near, t.Worst);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = Settings.Default with { TrayTemps = ["liq", "cpu"], TrayRotateSec = 5 };
        var r = Settings.Parse(s.ToJson());
        Assert.Equal(["liq", "cpu"], r.TrayTemps);
        Assert.Equal(5, r.TrayRotateSec);
        Assert.Empty(Settings.Default.TrayTemps);
        Assert.Equal(3, Settings.Parse("{\"version\":1,\"trayRotateSec\":7}").TrayRotateSec);
    }
}
