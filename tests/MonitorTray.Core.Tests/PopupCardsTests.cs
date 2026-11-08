using MonitorTray.Core.Fans;
using MonitorTray.Core.Format;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Sensors;
using Xunit;

namespace MonitorTray.Core.Tests;

public class TempCardsTests
{
    private static RawSensor S(string dev, string fam, string name, float? v, string kind = "Temperature") => new(dev + "/" + kind + "/" + name, dev, "x", fam, name, kind, "", v);

    [Fact]
    public void ResolvesRolesByFamilyAndSensorName()
    {
        var raw = new[]
        {
            S("/cpu/0", "Cpu", "CPU Package", 51), S("/cpu/0", "Cpu", "Core Max", 55), S("/cpu/0", "Cpu", "P-Core #1 Distance to TjMax", 40),
            S("/gpu-nvidia/1", "GpuNvidia", "GPU Core", 70),
            S("/gpu-nvidia/0", "GpuNvidia", "GPU Core", 48), S("/gpu-nvidia/0", "GpuNvidia", "GPU Hot Spot", 58), S("/gpu-nvidia/0", "GpuNvidia", "GPU Memory Junction", 54),
            S("/lpc/0", "SuperIO", "CPU", 44), S("/lpc/0", "SuperIO", "Motherboard", 36), S("/lpc/0", "SuperIO", "Chipset", 58), S("/lpc/0", "SuperIO", "Temperature #8", 3),
            S("/ec/0", "EmbeddedController", "VRM", 51),
            S("/mem/1", "Memory", "DIMM #1", 42), S("/mem/3", "Memory", "DIMM #3", 41), S("/mem/1", "Memory", "Thermal Sensor Critical High Limit", 85),
        };
        var t = TempCards.Resolve(raw);
        Assert.Equal(51f, t["cpu"]); Assert.Equal(55f, t["coremax"]);
        Assert.Equal(48f, t["gpu"]); Assert.Equal(58f, t["hot"]); Assert.Equal(54f, t["vram"]); // first GPU by id
        Assert.Equal(44f, t["socket"]); Assert.Equal(36f, t["mobo"]); Assert.Equal(58f, t["chip"]); Assert.Equal(51f, t["vrm"]);
        Assert.Equal(42f, t["ram"]); // hottest DIMM; SPD limits left out
        Assert.False(t.ContainsKey("liq")); // comes from the cooler
    }

    [Fact]
    public void MissingOrImplausibleSensorIsOmitted()
    {
        var t = TempCards.Resolve([S("/cpu/0", "Cpu", "CPU Package", 0), S("/cpu/0", "Cpu", "Core Max", null), S("/ec/0", "EmbeddedController", "VRM", 255)]);
        Assert.Empty(t);
    }

    [Fact]
    public void SimulatedDumpHasDefaultCardsExceptLiquid()
    {
        var src = new SimulatedSource(Path.Combine(AppContext.BaseDirectory, "data", "sensors.json"), seed: 1);
        src.Open();
        IReadOnlyList<RawSensor> raw = [];
        for (int i = 0; i < 3; i++) raw = src.Read();
        var t = TempCards.Resolve(raw);
        foreach (var id in TempCards.DefaultIds.Where(x => x != "liq")) Assert.True(t.ContainsKey(id), id);
    }

    [Fact]
    public void CatalogIsConsistent()
    {
        Assert.Equal(TempCards.Ids.Count, TempCards.Ids.Distinct().Count());
        Assert.All(TempCards.DefaultIds, id => Assert.NotNull(TempCards.Find(id)));
        Assert.True(TempCards.DefaultIds.Count <= TempCards.MaxCards);
    }
}

public class TempLimitsTests
{
    [Fact]
    public void LimitsAreTheAppProtections()
    {
        Assert.Equal(BoardFanOff.CpuLimitC, TempLimits.CpuC);
        Assert.Equal(BoardFanOff.GpuLimitC, TempLimits.GpuC);
        Assert.Equal(MonitorTray.Ryujin.CoolerSafety.WaterPanicC, TempLimits.LiquidC);
        Assert.Equal(TempLimits.CpuC, TempCards.Find("cpu")!.LimitC);
        Assert.Equal(TempLimits.GpuC, TempCards.Find("gpu")!.LimitC);
        Assert.Equal(TempLimits.VramC, TempCards.Find("vram")!.LimitC);
        Assert.Equal(TempLimits.LiquidC, TempCards.Find("liq")!.LimitC);
        Assert.Equal(TempLimits.VrmC, TempCards.Find("vrm")!.LimitC);
        Assert.Equal(TempLimits.ChipsetC, TempCards.Find("chip")!.LimitC);
        Assert.Equal(TempLimits.RamC, TempCards.Find("ram")!.LimitC);
        Assert.All(TempCards.All, d => Assert.InRange(d.LimitC, 40, 110));
    }

    private static MonitorTray.Gpu.GpuReading Gpu(uint core, uint? vram) =>
        new(new("uuid", "gpu", 1), DateTimeOffset.UtcNow, core, [new MonitorTray.Gpu.FanReading(0, 50, 50, 1)])
        { Telemetry = new() { MinFanPercent = 0, MaxFanPercent = 100, ClockEventReasons = 0, MemoryTemperatureC = vram } };

    [Fact]
    public void GpuLimitsMatchGpuSafety()
    {
        Assert.Null(MonitorTray.Gpu.GpuSafety.ReductionBlock(Gpu((uint)TempLimits.GpuC - 1, (uint)TempLimits.VramC - 1), 50));
        Assert.NotNull(MonitorTray.Gpu.GpuSafety.ReductionBlock(Gpu((uint)TempLimits.GpuC, 60), 50));
        Assert.NotNull(MonitorTray.Gpu.GpuSafety.ReductionBlock(Gpu(60, (uint)TempLimits.VramC), 50));
    }

    [Theory]
    [InlineData("cpu", 79.9, TempLevel.Normal)]
    [InlineData("cpu", 80, TempLevel.Near)]
    [InlineData("cpu", 85, TempLevel.Over)]
    [InlineData("gpu", 74, TempLevel.Normal)]
    [InlineData("gpu", 75, TempLevel.Near)]
    [InlineData("gpu", 80, TempLevel.Over)]
    [InlineData("vram", 90, TempLevel.Near)]
    [InlineData("vram", 95, TempLevel.Over)]
    [InlineData("liq", 44.9, TempLevel.Normal)]
    [InlineData("liq", 45, TempLevel.Near)]
    [InlineData("liq", 50, TempLevel.Over)]
    public void LevelIsNearFromLimitMinus5AndOverAtLimit(string id, double v, TempLevel want) =>
        Assert.Equal(want, TempCards.Level(TempCards.Find(id)!, v));

    [Fact]
    public void HottestPicksMaxAmongEnabledAndWorstLevel()
    {
        var v = new Dictionary<string, float> { ["cpu"] = 70, ["gpu"] = 77, ["vram"] = 82, ["vrm"] = 99, ["liq"] = 33 };
        var h = TempCards.Hottest(["cpu", "gpu", "vram", "liq"], v)!;
        Assert.Equal("vram", h.Id); Assert.Equal(82f, h.Value);
        Assert.Equal(TempLevel.Near, h.Worst); // GPU 77 is near (75..80) even though it is not the hottest
        Assert.Equal("vrm", TempCards.Hottest(["vrm", "cpu"], v)!.Id); // VRM only counts if enabled
        Assert.Equal(TempLevel.Near, TempCards.Hottest(["vrm"], v)!.Worst);
        Assert.Equal(TempLevel.Normal, TempCards.Hottest(["cpu", "liq"], v)!.Worst);
        Assert.Null(TempCards.Hottest(["ram", "hot"], v)); // enabled but no reading
        Assert.Null(TempCards.Hottest([], v));
    }

    [Fact]
    public void MergePrefersLhmAndAddsNvmlAndLiquid()
    {
        var m = TempCards.Merge(new Dictionary<string, float> { ["gpu"] = 50 }, 34.8, 60, 70, 0);
        Assert.Equal(50f, m["gpu"]); // LHM first
        Assert.Equal(70f, m["hot"]);
        Assert.False(m.ContainsKey("vram")); // 0 from NVML = no reading
        Assert.Equal(34.8f, m["liq"], 0.01f);
    }

    [Fact]
    public void SessionKeepsMinAndMax()
    {
        var s = new TempSession();
        s.Push(new Dictionary<string, float> { ["cpu"] = 50 });
        s.Push(new Dictionary<string, float> { ["cpu"] = 40, ["gpu"] = 70 });
        s.Push(new Dictionary<string, float> { ["cpu"] = 61 });
        Assert.Equal((40f, 61f), s.Range("cpu"));
        Assert.Equal((70f, 70f), s.Range("gpu"));
        Assert.Null(s.Range("ram"));
    }

    [Fact]
    public void ResolveReportsSensorIdsForDetails()
    {
        var src = new Dictionary<string, string>();
        var raw = new RawSensor[] { new("/cpu/0/temperature/0", "/cpu/0", "x", "Cpu", "CPU Package", "Temperature", "", 51), new("/mem/1/t", "/mem/1", "x", "Memory", "DIMM #1", "Temperature", "", 40), new("/mem/2/t", "/mem/2", "x", "Memory", "DIMM #2", "Temperature", "", 44) };
        var t = TempCards.Resolve(raw, src);
        Assert.Equal("/cpu/0/temperature/0", src["cpu"]);
        Assert.Equal("/mem/2/t", src["ram"]); // the hottest DIMM
        Assert.Equal(44f, t["ram"]);
    }
}

public class BoardFanMapTests
{
    private static readonly Dictionary<int, float> Rpm = new() { [0] = 1400, [1] = 1268, [2] = 611, [5] = 2100, [6] = 1800 };
    private static readonly Dictionary<int, float> Ctl = new() { [0] = 70, [1] = 76, [2] = 40, [5] = 84, [6] = 84 };

    [Theory]
    [InlineData("CPU Fan")]
    [InlineData("Water Pump+")]
    [InlineData("AIO Pump")]
    public void UnmeasuredConnectorsShowPercentButNeverRpm(string fan)
    {
        var r = BoardFanMap.Read(fan, Rpm, Ctl);
        Assert.False(r.RpmMeasured);
        Assert.Null(r.Rpm); // even with a reading on the estimated output
        Assert.NotNull(r.Percent);
    }

    [Fact]
    public void ChassisRpmIsMeasuredAndNeverDerivedFromPercent()
    {
        var r = BoardFanMap.Read("Chassis Fan 2", Rpm, Ctl);
        Assert.True(r.RpmMeasured); Assert.Equal(611, r.Rpm); Assert.Equal(40, r.Percent);
        var noRpm = BoardFanMap.Read("Chassis Fan 3", Rpm, new Dictionary<int, float> { [3] = 55 });
        Assert.Null(noRpm.Rpm); // only the % exists: rpm stays "—"
        Assert.Equal(55, noRpm.Percent);
        var unknown = BoardFanMap.Read("CPU Optional Fan", Rpm, Ctl);
        Assert.Null(unknown.Rpm); Assert.Null(unknown.Percent);
    }

    [Theory]
    [InlineData("/lpc/nct6798d/0/fan/2", 2)]
    [InlineData("/lpc/nct6798d/0/control/6", 6)]
    [InlineData("/Motherboard/hw-1/fan/2#Fan #3", 2)]
    [InlineData("/Motherboard/hw-1/control/0#Fan #1", 0)]
    [InlineData("/lpc/nct6798d/0/fan/x", null)]
    public void TrailingIndexToleratesExportSuffix(string id, int? want) => Assert.Equal(want, BoardFanMap.TrailingIndex(id));
}

public class AutoGameTests
{
    private static readonly IReadOnlyList<string> Exes = ["cs2.exe", "EldenRing.exe"];

    [Fact]
    public void NormalizeAndClean()
    {
        Assert.Equal("cs2", AutoGame.Normalize(@"C:\Games\CS2.EXE"));
        Assert.Equal("cs2", AutoGame.Normalize("cs2"));
        Assert.Equal("cs2.exe", AutoGame.Clean("  CS2.exe "));
        Assert.Equal("eldenring.exe", AutoGame.Clean(@"D:\x\eldenring"));
        Assert.Null(AutoGame.Clean(""));
        Assert.Null(AutoGame.Clean("a*b.exe"));
        Assert.Null(AutoGame.Clean("a|b"));
    }

    [Fact]
    public void GameStartTurnsOnAndAllClosedTurnsOff()
    {
        var a = new AutoGame();
        Assert.Equal(AutoGameAction.None, a.Step(["explorer"], Exes, false));
        Assert.Equal(AutoGameAction.TurnOn, a.Step(["explorer", "eldenring"], Exes, false));
        Assert.True(a.Owned); Assert.Equal("EldenRing.exe", a.Game);
        Assert.Equal(AutoGameAction.None, a.Step(["eldenring", "cs2"], Exes, true)); // stays on
        Assert.Equal(AutoGameAction.None, a.Step(["cs2"], Exes, true)); // one closed, the other keeps running
        Assert.Equal(AutoGameAction.TurnOff, a.Step(["explorer"], Exes, true));
        Assert.False(a.Owned);
        Assert.Equal(AutoGameAction.None, a.Step(["explorer"], Exes, false));
    }

    [Fact]
    public void ManualOnIsNeverTurnedOffByAutomation()
    {
        var a = new AutoGame();
        a.ManualToggle(true, false);
        Assert.Equal(AutoGameAction.None, a.Step(["cs2"], Exes, true));
        Assert.Equal(AutoGameAction.None, a.Step(["explorer"], Exes, true)); // game closed: stays on (the user turned it on)
        Assert.False(a.Owned);
    }

    [Fact]
    public void ManualOnWhileAutoOwnedTransfersOwnership()
    {
        var a = new AutoGame();
        Assert.Equal(AutoGameAction.TurnOn, a.Step(["cs2"], Exes, false));
        a.ManualToggle(false, true); a.ManualToggle(true, true); // turned off and back on with the button
        Assert.Equal(AutoGameAction.None, a.Step([], Exes, true));
    }

    [Fact]
    public void ManualOffDuringGameIsRespectedUntilAllClose()
    {
        var a = new AutoGame();
        Assert.Equal(AutoGameAction.TurnOn, a.Step(["cs2"], Exes, false));
        a.ManualToggle(false, true);
        Assert.Equal(AutoGameAction.None, a.Step(["cs2"], Exes, false)); // does not turn back on
        Assert.Equal(AutoGameAction.None, a.Step([], Exes, false));
        Assert.Equal(AutoGameAction.TurnOn, a.Step(["cs2"], Exes, false)); // next game turns it on again
    }

    [Fact]
    public void TurnedOffElsewhereWhileOwnedIsNotFoughtBack()
    {
        var a = new AutoGame();
        a.Step(["cs2"], Exes, false);
        Assert.Equal(AutoGameAction.None, a.Step(["cs2"], Exes, false)); // the app turned it off (e.g. user changed the GPU)
        Assert.False(a.Owned);
        Assert.Equal(AutoGameAction.None, a.Step(["cs2"], Exes, false));
    }

    [Fact]
    public void RestoredOwnershipTurnsOffWhenGameIsGone()
    {
        var a = new AutoGame();
        a.Restore(true, "cs2.exe");
        Assert.Equal(AutoGameAction.TurnOff, a.Step([], Exes, true));
        var b = new AutoGame();
        b.Restore(true, "cs2.exe");
        Assert.Equal(AutoGameAction.TurnOff, b.Step(["cs2"], [], true)); // list emptied: hands it back
    }

    [Fact]
    public void SettingsRoundTripGameExesAndTrayOptions()
    {
        var s = Settings.Default with { GameExes = ["cs2.exe"], TrayTemp = true, TrayAlert = true };
        var back = Settings.Parse(s.ToJson());
        Assert.Equal(s, back);
        Assert.Equal(["cs2.exe", "game.exe"], Settings.Parse("{\"version\":1,\"gameExes\":[\"CS2.EXE\",\"bad*\",\"game\",\"cs2.exe\"]}").GameExes);
        Assert.False(Settings.Default.TrayTemp); Assert.False(Settings.Default.TrayAlert); Assert.Empty(Settings.Default.GameExes);
    }
}

public class StatusStripTests
{
    private static string Short(string f) => f.Replace("Chassis Fan ", "CHA");

    [Fact]
    public void EmptyWhenNothingAutomatic() => Assert.Empty(StatusStrip.Items(new StripState(), DateTimeOffset.Now, Short));

    [Fact]
    public void GameTextSaysAll100OnlyWhenEverythingIs100()
    {
        Assert.Equal("all 100%", StatusStrip.GameText(Settings.GameTargetIds, 100, 100));
        Assert.Equal("GPU 85% · board 100% · cooler 100%", StatusStrip.GameText(Settings.GameTargetIds, 85, 100));
        Assert.Equal("GPU and board 100%", StatusStrip.GameText(["gpu", "board"], 100, 70));
        Assert.Equal("nothing selected", StatusStrip.GameText([], 100, 100));
    }

    [Fact]
    public void SafetyEventShowsFanAndReasonAndExpires()
    {
        var now = DateTimeOffset.Now;
        var st = new StripState { BoardSafety = new SafetyEvent(now.AddSeconds(-10), ["Chassis Fan 1"], 86, 60) };
        Assert.Equal("CHA1 back on · CPU 86°", Assert.Single(StatusStrip.Items(st, now, Short)).Text);
        Assert.Equal("CHA1, CHA2 back on · GPU 81°", StatusStrip.Safety(new SafetyEvent(now, ["Chassis Fan 1", "Chassis Fan 2"], 60, 81), Short).Text);
        Assert.Equal("CHA3 back on · no CPU reading", StatusStrip.Safety(new SafetyEvent(now, ["Chassis Fan 3"], null, 50), Short).Text);
        Assert.Empty(StatusStrip.Items(st, now + StatusStrip.EventTtl + TimeSpan.FromSeconds(11), Short));
    }

    [Fact]
    public void GameAndProtectionsListed()
    {
        var st = new StripState { GameOn = true, GameBy = "cs2.exe", GameTargets = Settings.GameTargetIds, GpuProtection = "PROTECTION 100%: x", CoolerPanicC = 50.4 };
        var items = StatusStrip.Items(st, DateTimeOffset.Now, Short);
        Assert.Equal(["GPU 100% · protection", "COOLER 100% · water 50.4°", "GAME MODE · all 100%"], items.Select(i => i.Text));
        Assert.Contains("cs2.exe", items[2].Tip);
    }
}

public class PickListTests
{
    [Fact]
    public void ToggleAppendsRemovesAndRespectsMax()
    {
        IReadOnlyList<string> l = ["cpu", "gpu"];
        Assert.Equal(["cpu", "gpu", "vrm"], PickList.Toggle(l, "vrm", true));
        Assert.Equal(["gpu"], PickList.Toggle(l, "cpu", false));
        Assert.Equal(l, PickList.Toggle(l, "cpu", true));
        Assert.Equal(l, PickList.Toggle(l, "vrm", true, max: 2));
    }

    [Fact]
    public void MoveSwapsNeighboursAndStopsAtEnds()
    {
        IReadOnlyList<string> l = ["a", "b", "c"];
        Assert.Equal(["b", "a", "c"], PickList.Move(l, "b", -1));
        Assert.Equal(["a", "c", "b"], PickList.Move(l, "b", +1));
        Assert.Equal(l, PickList.Move(l, "a", -1));
        Assert.Equal(l, PickList.Move(l, "c", +1));
        Assert.Equal(l, PickList.Move(l, "x", +1));
    }

    [Fact]
    public void DisplayShowsChosenFirstThenRestInCatalogOrder()
    {
        Assert.Equal(["vrm", "cpu", "gpu", "hot"], PickList.Display(["vrm", "cpu"], ["cpu", "gpu", "hot", "vrm"]));
    }

    [Fact]
    public void CleanDropsUnknownAndDuplicates()
    {
        Assert.Equal(["cpu", "gpu"], PickList.Clean(["cpu", "zzz", "cpu", "gpu"], TempCards.Ids.ToList()));
        Assert.Equal(["cpu"], PickList.Clean(["cpu", "gpu"], TempCards.Ids.ToList(), max: 1));
    }
}

public class PopupSettingsTests
{
    [Fact]
    public void DefaultsMatchTheApprovedPopup()
    {
        var d = Settings.Default;
        Assert.Equal(["cpu", "gpu", "hot", "vram", "vrm", "chip", "liq", "ram"], d.PopupTemps);
        Assert.Equal(["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"], d.PopupBoardFans);
        Assert.Equal(["pump", "radiator", "embedded"], d.PopupCoolerFans);
        Assert.False(d.BoardFanOff);
        Assert.Equal(["curves", "silent", "standard", "turbo", "full"], d.AcModes); // board: every mode visible
        Assert.Equal("xs", d.TrayFontSize);
    }

    [Fact]
    public void AcModesChoiceIsKeptAndCleaned()
    {
        var s = Settings.Default with { AcModes = ["curves", "full"] };
        Assert.Equal(["curves", "full"], Settings.Parse(s.ToJson()).AcModes);
        Assert.Empty(Settings.Parse("{\"version\":1,\"acModes\":[]}").AcModes); // hiding all is a valid choice
        Assert.Equal(["turbo"], Settings.Parse("{\"version\":1,\"acModes\":[\"turbo\",\"nope\"]}").AcModes);
        Assert.Equal(Settings.AcModeIds, Settings.Parse("{\"version\":1}").AcModes);
    }

    [Fact]
    public void TrayFontSizeRoundTripsAndFallsBack()
    {
        Assert.Equal("l", Settings.Parse((Settings.Default with { TrayFontSize = "l" }).ToJson()).TrayFontSize);
        Assert.Equal("xs", Settings.Parse("{\"version\":1,\"trayFontSize\":\"xl\"}").TrayFontSize);
        Assert.NotEqual(Settings.Default, Settings.Default with { TrayFontSize = "m" });
        Assert.True(Settings.TrayFontScale("xs") < Settings.TrayFontScale("s"));
        Assert.True(Settings.TrayFontScale("s") < Settings.TrayFontScale("m"));
        Assert.True(Settings.TrayFontScale("m") < Settings.TrayFontScale("l"));
        Assert.Equal(1.0, Settings.TrayFontScale("l"));
        Assert.Equal(Settings.TrayFontScale("xs"), Settings.TrayFontScale("?"));
    }

    [Fact]
    public void RoundTripsChoicesAndOrder()
    {
        var s = Settings.Default with { PopupTemps = ["ram", "cpu", "liq"], PopupBoardFans = ["Chassis Fan 2"], PopupCoolerFans = ["pump"], BoardFanOff = true };
        var back = Settings.Parse(s.ToJson());
        Assert.Equal(s, back);
        Assert.Equal(["ram", "cpu", "liq"], back.PopupTemps);
        Assert.NotEqual(s, s with { PopupTemps = ["cpu", "ram", "liq"] });
    }

    [Fact]
    public void ParseCleansInvalidLists()
    {
        var s = Settings.Parse("{\"version\":1,\"popupTemps\":[\"cpu\",\"nope\",\"cpu\",\"gpu\",\"hot\",\"vram\",\"vrm\",\"chip\",\"liq\",\"ram\",\"mobo\"],\"popupCoolerFans\":[\"pump\",\"x\"]}");
        Assert.Equal(["cpu", "gpu", "hot", "vram", "vrm", "chip", "liq", "ram"], s.PopupTemps); // max. 8, no unknown ids
        Assert.Equal(["pump"], s.PopupCoolerFans);
        Assert.Empty(Settings.Parse("{\"version\":1,\"popupTemps\":[]}").PopupTemps); // no card is a valid choice
    }
}

public class BoardFanOffTests
{
    [Theory]
    [InlineData("CPU Fan", true)]
    [InlineData("CPU Optional Fan", true)]
    [InlineData("Water Pump+", true)]
    [InlineData("AIO Pump", true)]
    [InlineData("Chassis Fan 1", false)]
    public void CpuAndPumpsAreLocked(string fan, bool locked) => Assert.Equal(locked, BoardFanOff.Locked(fan));

    [Fact]
    public void TurnOffOnlyWhenSafe()
    {
        var o = new BoardFanOff();
        Assert.False(o.TurnOff("CPU Fan", 50, 50));
        Assert.False(o.TurnOff("Chassis Fan 1", null, 50)); // no CPU reading
        Assert.False(o.TurnOff("Chassis Fan 1", 86, 50));
        Assert.False(o.TurnOff("Chassis Fan 1", 50, 81));
        Assert.True(o.TurnOff("Chassis Fan 1", 85, 80)); // limits are "above"
        Assert.True(o.IsOff("Chassis Fan 1"));
        Assert.False(o.TurnOff("Chassis Fan 1", 50, 50)); // already off
    }

    [Fact]
    public void CheckTurnsEverythingBackOnWhenHotOrUnread()
    {
        var o = new BoardFanOff();
        o.TurnOff("Chassis Fan 1", 50, 50); o.TurnOff("Chassis Fan 2", 50, 50);
        Assert.Empty(o.Check(84, 79));
        Assert.True(o.Any);
        Assert.Equal(["Chassis Fan 1", "Chassis Fan 2"], o.Check(86, 40).Order());
        Assert.False(o.Any);
        o.TurnOff("Chassis Fan 3", 50, 50);
        Assert.Equal(["Chassis Fan 3"], o.Check(50, 81));
        o.TurnOff("Chassis Fan 3", 50, 50);
        Assert.Equal(["Chassis Fan 3"], o.Check(null, 40));
    }

    [Fact]
    public void FullTurnsAllBackOnOtherModesSkipOffFans()
    {
        var o = new BoardFanOff();
        o.TurnOff("Chassis Fan 1", 50, 50); o.TurnOff("Chassis Fan 4", 50, 50);
        Assert.Equal(["Chassis Fan 2", "Chassis Fan 3"], o.ModeTargets("standard", ["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3"]));
        Assert.True(o.IsOff("Chassis Fan 1"));
        var full = o.ModeTargets("full", ["Chassis Fan 1", "Chassis Fan 2"]);
        Assert.Equal(["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 4"], full.Order()); // includes the fan that is off outside the list
        Assert.False(o.Any);
    }

    [Fact]
    public void ClickDoesNothingWithOptionOffOrLocked()
    {
        var o = new BoardFanOff();
        Assert.Equal(BoardFanClick.None, o.ClickAction("Chassis Fan 1", false, 50, 50));
        Assert.Equal(BoardFanClick.None, o.ClickAction("CPU Fan", true, 50, 50));
        Assert.False(BoardFanOff.Clickable("Chassis Fan 1", false)); // no hand cursor
        Assert.False(BoardFanOff.Clickable("AIO Pump", true));
        Assert.True(BoardFanOff.Clickable("Chassis Fan 1", true));
    }

    [Fact]
    public void ClickTurnsOffThenOnAndReportsBlocked()
    {
        var o = new BoardFanOff();
        Assert.Equal(BoardFanClick.TurnOff, o.ClickAction("Chassis Fan 1", true, 50, 50));
        Assert.Equal(BoardFanClick.Blocked, o.ClickAction("Chassis Fan 1", true, 86, 50));
        Assert.Equal(BoardFanClick.Blocked, o.ClickAction("Chassis Fan 1", true, null, 50));
        o.TurnOff("Chassis Fan 1", 50, 50);
        Assert.Equal(BoardFanClick.TurnOn, o.ClickAction("Chassis Fan 1", true, 86, 50)); // turning back on is always allowed
        Assert.Equal(BoardFanClick.None, o.ClickAction("Chassis Fan 1", false, 50, 50));
    }

    [Fact]
    public void ProtectionCurveIsZeroUntil80AndFullFrom85()
    {
        var c = BoardFanOff.ProtectionCurve(4);
        Assert.Equal([[60, 0], [70, 0], [80, 0], [85, 255]], c);
        for (int i = 1; i < c.Length; i++) Assert.True(c[i][0] > c[i - 1][0]);
        Assert.Equal([[80, 0], [85, 255]], BoardFanOff.ProtectionCurve(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoardFanOff.ProtectionCurve(1));
    }
}
