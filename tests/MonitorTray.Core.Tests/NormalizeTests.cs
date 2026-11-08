using MonitorTray.Core.Detail;
using MonitorTray.Core.Fans;
using MonitorTray.Core.History;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Sensors;
using Xunit;

namespace MonitorTray.Core.Tests;

public class NormalizeTests
{
    private static string DataPath => Path.Combine(AppContext.BaseDirectory, "data", "sensors.json");

    internal static (IReadOnlyList<Device> Devices, SensorTracker Tracker, SimulatedSource Src) Simulate(int ticks = 12, bool fake = false)
    {
        var src = new SimulatedSource(DataPath, seed: 42);
        src.Open();
        if (fake) src.AddFakeDevice();
        var tracker = new SensorTracker();
        IReadOnlyList<RawSensor> raw = [];
        for (int i = 0; i < ticks; i++) { raw = src.Read(); tracker.BeginTick(); foreach (var r in raw) tracker.Push(r.Id, r.Value); }
        return (DeviceBuilder.Build(raw, tracker, _ => false), tracker, src);
    }

    [Fact]
    public void PrimaryCanonicalCases()
    {
        var (devs, _, _) = Simulate();
        var cpu = devs.First(d => d.Category == "CPU");
        var gpu = devs.First(d => d.Category == "GPU");
        string P(Device d, string kind) => Primary.Pick(d.Sensors.Where(s => s.KindRaw == kind && Roles.Graphable(s.Role, s.Quality, s.Unit)).ToList(), s => s.Name, s => s.Value)!.Name;
        Assert.Equal("CPU Package", P(cpu, "Temperature"));
        Assert.Equal("CPU Total", P(cpu, "Load"));
        Assert.Equal("GPU Core", P(gpu, "Temperature"));
        var mem = devs.Single(d => d.Family == "Memory");
        Assert.StartsWith("Physical · Memory", P(mem, "Load"));
        foreach (var ssd in devs.Where(d => d.Family == "Storage"))
            Assert.Equal("Composite Temperature", DetailModel.HotSensor(ssd)!.Name);
        Assert.Equal("CPU Package", DetailModel.HotSensor(cpu)!.Name);
    }

    [Fact]
    public void PrimaryExcludesDistanceToTjMax()
    {
        var list = new[] { ("Distance to TjMax", 70f), ("Core #1", 50f) };
        Assert.Equal("Core #1", Primary.Pick(list.Select(x => new Box(x.Item1, x.Item2)).ToList(), b => b.N, b => b.V)!.N);
    }
    private sealed record Box(string N, float V);

    [Fact]
    public void MemoryConsolidatedWithSlots()
    {
        var (devs, _, _) = Simulate();
        var mem = devs.Where(d => d.Category == "Memory").ToList();
        Assert.Single(mem);
        var m = mem[0];
        Assert.Equal("family:Memory", m.Id);
        Assert.Equal([(1, 1), (2, 3)], m.Modules.Select(x => (x.Index, x.Slot)));
        Assert.Contains(m.Sensors, s => s.Name.StartsWith("Module 1 · ", StringComparison.Ordinal));
        Assert.Contains(m.Sensors, s => s.Name.StartsWith("Virtual · ", StringComparison.Ordinal));
        var panel = DetailModel.Panel(m, m.Sensors, new SensorTracker());
        Assert.Contains("2 modules · module 1 in slot 1, module 2 in slot 3", panel.Meta);
    }

    [Fact]
    public void NetworkGenericNamesAndDuplicates()
    {
        var (devs, _, _) = Simulate();
        var net = devs.Single(d => d.Category == "Network");
        Assert.Equal("Network", net.DisplayName);
        Assert.Contains(net.Sensors, s => s.Name.StartsWith("Network adapter 1 · ", StringComparison.Ordinal));
        Assert.Contains(net.Sensors, s => s.Name.StartsWith("Network adapter 2 · ", StringComparison.Ordinal));
        var kc = devs.Where(d => d.RawName == "KINGSTON SKC3000D2048G").Select(d => d.DisplayName).Order().ToList();
        Assert.Equal(["KINGSTON SKC3000D2048G · hw-1", "KINGSTON SKC3000D2048G · hw-2"], kc);
    }

    [Fact]
    public void CategoryOrder()
    {
        var (devs, _, _) = Simulate();
        var cats = devs.Select(d => d.Category).Distinct().ToList();
        Assert.Equal(["CPU", "GPU", "Memory", "Motherboard", "Storage", "Network"], cats);
    }

    [Fact]
    public void RolesAndTags()
    {
        var (devs, _, _) = Simulate();
        var ssd = devs.First(d => d.Family == "Storage");
        Assert.Equal(SensorRole.Limit, ssd.Sensors.First(s => s.Name == "Warning Temperature").Role);
        Assert.Equal(SensorRole.Limit, ssd.Sensors.First(s => s.Name == "Critical Temperature").Role);
        Assert.Equal("cumulative", ssd.Sensors.First(s => s.Name == "Data Read").Tag);
        Assert.Equal("estimate", ssd.Sensors.First(s => s.Name == "Life").Tag);
        Assert.Equal(SensorRole.Reading, ssd.Sensors.First(s => s.Name == "Composite Temperature").Role);
        var mb = devs.Where(d => d.Category == "Motherboard").SelectMany(d => d.Sensors).ToList();
        foreach (var n in new[] { "Water In", "Water Out", "T Sensor" })
        {
            var s = mb.First(x => x.Name == n);
            Assert.Equal("no reading", s.Tag); Assert.Null(s.Value);
        }
        Assert.True(mb.First(x => x.Name == "Water Flow").AllZero);
        var mem = devs.Single(d => d.Family == "Memory");
        Assert.All(mem.Sensors.Where(s => s.KindRaw == "Timing"), s => Assert.Equal(SensorRole.Metadata, s.Role));
        var cpu = devs.First(d => d.Category == "CPU");
        Assert.All(cpu.Sensors.Where(s => s.Name.Contains("Distance to TjMax")), s => Assert.Equal("margin", s.Tag));
    }

    [Fact]
    public void LimitsHiddenByDefault()
    {
        var (devs, tracker, _) = Simulate();
        var ssdId = devs.First(d => d.Family == "Storage").Id;
        var v = DetailModel.Build(devs, tracker, Filters.Default, ssdId);
        Assert.DoesNotContain(v.Panel!.Sections.SelectMany(s => s.Tiles), t => t.Name == "Warning Temperature");
        var on = DetailModel.Build(devs, tracker, Filters.Default with { ShowLimits = true }, ssdId);
        var w = on.Panel!.Sections.SelectMany(s => s.Tiles).First(t => t.Name == "Warning Temperature");
        Assert.Equal("limit", w.Tag);
        Assert.True(v.HiddenLimits > 0 && v.HiddenZero > 0);
    }

    [Fact]
    public void CpuGridsAndCards()
    {
        var (devs, tracker, _) = Simulate(30);
        var cpu = devs.First(d => d.Category == "CPU");
        var v = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id);
        var p = v.Panel!;
        Assert.Equal("CPU Package", p.Cards.First(c => c.Label == "Temperature").SensorName);
        Assert.Equal("CPU Total", p.Cards.First(c => c.Label == "Load").SensorName);
        foreach (var kind in new[] { "Load", "Clock", "Voltage" })
            Assert.True(p.Sections.First(s => s.KindRaw == kind).IsGrid, kind);
        Assert.All(p.Sections.Where(s => s.IsGrid), s => Assert.True(s.Cells.Count >= 9));
    }

    [Fact]
    public void ClockCardCanShowAverageMaxOrAnyCore()
    {
        var (devs, tracker, _) = Simulate(30);
        var cpu = devs.First(d => d.Category == "CPU");
        var key = cpu.Id + "|Clock";
        var def = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.Contains(def.Options, o => o.Key == DetailModel.PickAvg);
        Assert.True(def.Options.Count >= 10); // average, max and each core (list comes from the hardware)
        var avg = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id, new Dictionary<string, string> { [key] = "avg" }).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.StartsWith("Average", avg.SensorName);
        var clocks = cpu.Sensors.Where(s => s.KindRaw == "Clock" && s.Unit == "MHz" && s.Value != null).Select(s => (double)s.Value!.Value).ToList();
        Assert.Equal(clocks.Average(), avg.Raw!.Value, 3);
        var core = def.Options.Last();
        var one = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id, new Dictionary<string, string> { [key] = core.Key }).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.Equal(core.Label, one.SensorName);
        var bad = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id, new Dictionary<string, string> { [key] = "gone" }).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.Equal(def.SensorName, bad.SensorName); // invalid choice falls back to the default
    }

    [Fact]
    public void AggregateAlignsRightAndSkipsNulls()
    {
        var r = DetailModel.Aggregate([[1, 2, 3], [null, 4, null]], max: false);
        Assert.Equal([1f, 3f, 3f], r);
        Assert.Equal([1f, 4f, 3f], DetailModel.Aggregate([[1, 2, 3], [null, 4, null]], max: true));
    }

    [Fact]
    public void SearchIsAccentInsensitive()
    {
        var (devs, tracker, _) = Simulate();
        var v = DetailModel.Build(devs, tracker, Filters.Default with { Query = "VOLTÄGE" }, null); // case and diacritics are folded
        Assert.NotEmpty(v.Devices);
        Assert.All(v.Panel!.Sections, s => Assert.Equal("Voltage", s.KindRaw));
        var none = DetailModel.Build(devs, tracker, Filters.Default with { Query = "xyzzy" }, null);
        Assert.Empty(none.Devices); Assert.Null(none.Panel);
    }

    [Fact]
    public void UnknownTypeInPsuRenders()
    {
        var (devs, tracker, _) = Simulate(fake: true);
        var psu = devs.Single(d => d.Category == "PSU");
        var v = DetailModel.Build(devs, tracker, Filters.Default, psu.Id);
        Assert.Contains(v.Panel!.Sections, s => s.Label == "Current");
    }

    [Fact]
    public void NoHardcodedModelNamesInSrc()
    {
        var src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var bad = new[] { "14900", "3090", "Kingston", "Corsair", "NCT" };
        var hits = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => bad.Where(b => File.ReadAllText(f).Contains(b, StringComparison.Ordinal)).Select(b => Path.GetFileName(f) + ":" + b)).ToList();
        Assert.Empty(hits);
    }
}

public class SummaryTests
{
    private static RawSensor S(string dev, string fam, string kind, string name, float? v) => new(dev + "/" + kind + "/" + name, dev, "x", fam, name, kind, "", v);

    [Fact]
    public void ZeroPackagePowerIsNoReading()
    {
        var raw = new[] { S("/cpu/0", "Cpu", "Power", "CPU Package", 0), S("/cpu/0", "Cpu", "Load", "CPU Total", 5) };
        var (load, temp, power) = SummaryPicker.Cpu(raw);
        Assert.Equal(5f, load); Assert.Null(temp); Assert.Null(power);
    }

    [Fact]
    public void GpuPicksCoreAndFirstDeviceById()
    {
        var raw = new[]
        {
            S("/gpu-nvidia/1", "GpuNvidia", "Temperature", "GPU Core", 70),
            S("/gpu-nvidia/0", "GpuNvidia", "Temperature", "GPU Hot Spot", 50),
            S("/gpu-nvidia/0", "GpuNvidia", "Temperature", "GPU Core", 41),
            S("/gpu-nvidia/0", "GpuNvidia", "Power", "GPU Package", 42.4f),
        };
        var (_, temp, power) = SummaryPicker.Gpu(raw);
        Assert.Equal(41f, temp); Assert.Equal(42.4f, power);
    }

    [Theory]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "Intel(R) Ethernet Controller I226-V-WFP Native MAC Layer LightWeight Filter-0000", true)]
    [InlineData("Ethernet-QoS Packet Scheduler-0000", "Intel(R) Ethernet Controller I226-V-QoS Packet Scheduler-0000", true)]
    [InlineData("Local Area Connection* 6", "WAN Miniport (IP)", true)]
    [InlineData("Ethernet (Kernel Debugger)", "Microsoft Kernel Debug Network Adapter", true)]
    [InlineData("Ethernet", "Intel(R) Ethernet Controller I226-V", false)]
    [InlineData("WiFi", "Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    public void NetworkFilterRule(string name, string desc, bool filtered) => Assert.Equal(filtered, NetworkFilter.IsFilterOrVirtual(name, desc));
}

public class NetworkPanelTests
{
    [Fact]
    public void OneNetworkTabWithSectionsPerTrafficTypeThenAdapters()
    {
        RawSensor S(string dev, string devName, string name, float v) => new(dev + "/throughput/" + name, dev, devName, "Network", name, "Throughput", "B/s", v);
        float?[] _ = [];
        var raw = new List<RawSensor>
        {
            S("/nic/abc", "Ethernet", "Download Speed", 5000), S("/nic/abc", "Ethernet", "Upload Speed", 100),
            S(NetTrafficIds.Internet, "Internet traffic", "Download", 3000), S(NetTrafficIds.Internet, "Internet traffic", "Upload", 50),
            S(NetTrafficIds.Local, "Local network traffic", "Download", 2000), S(NetTrafficIds.Local, "Local network traffic", "Upload", 50),
            S(NetTrafficIds.Total, "Total traffic", "Download", 5000), S(NetTrafficIds.Total, "Total traffic", "Upload", 100),
        };
        var t = new SensorTracker();
        for (int i = 0; i < 3; i++) { t.BeginTick(); foreach (var r in raw) t.Push(r.Id, r.Value + i); }
        var devs = DeviceBuilder.Build(raw, t, _ => false);
        var net = devs.Single(d => d.Category == "Network");
        var panel = DetailModel.Panel(net, net.Sensors, t);
        Assert.Equal(["Total", "Local network", "Internet", "Ethernet"], panel.Sections.Select(s => s.Label));
        Assert.Equal(["Total", "Local network", "Internet"], panel.Cards.Select(c => c.Label));
        Assert.All(panel.Cards, c => Assert.Equal("Download", c.SensorName));
        Assert.Equal(["Download", "Upload"], panel.Cards[2].Options.Select(o => o.Label));
        var up = DetailModel.Panel(net, net.Sensors, t, new Dictionary<string, string> { [net.Id + "|src:Internet"] = NetTrafficIds.Internet + "/throughput/Upload" });
        Assert.Equal("Upload", up.Cards[2].SensorName);
    }
}

public class NetScopeTests
{
    private static byte[] V4(string s) => s.Split('.').Select(byte.Parse).ToArray();
    private static byte[] V6(string s) => System.Net.IPAddress.Parse(s).GetAddressBytes();

    [Theory]
    [InlineData("192.168.1.20", TrafficScope.Local)] [InlineData("10.0.0.5", TrafficScope.Local)] [InlineData("172.20.1.1", TrafficScope.Local)]
    [InlineData("172.32.0.1", TrafficScope.Internet)] [InlineData("100.101.2.3", TrafficScope.Local)] [InlineData("169.254.9.9", TrafficScope.Local)]
    [InlineData("239.255.255.250", TrafficScope.Local)] [InlineData("8.8.8.8", TrafficScope.Internet)] [InlineData("127.0.0.1", TrafficScope.Ignore)]
    public void V4Rules(string ip, TrafficScope s) => Assert.Equal(s, NetScope.Classify(V4(ip)));

    [Theory]
    [InlineData("::1", TrafficScope.Ignore)] [InlineData("fe80::1", TrafficScope.Local)] [InlineData("fd12:3456::1", TrafficScope.Local)]
    [InlineData("2001:4860:4860::8888", TrafficScope.Internet)] [InlineData("::ffff:192.168.0.1", TrafficScope.Local)] [InlineData("::ffff:1.1.1.1", TrafficScope.Internet)]
    public void V6Rules(string ip, TrafficScope s) => Assert.Equal(s, NetScope.Classify(V6(ip)));

    [Fact]
    public void PacketIsInternetIfEitherEndIsPublic()
    {
        Assert.Equal(TrafficScope.Internet, NetScope.Of(V4("192.168.0.10"), V4("142.250.1.1")));
        Assert.Equal(TrafficScope.Local, NetScope.Of(V4("192.168.0.10"), V4("192.168.0.21")));
        Assert.Equal(TrafficScope.Ignore, NetScope.Of(V4("127.0.0.1"), V4("127.0.0.1")));
    }
}

public class TrackerTests
{
    [Fact]
    public void QualityWindow()
    {
        var t = new SensorTracker();
        for (int i = 0; i < 10; i++) { t.BeginTick(); t.Push("a", null); t.Push("b", i == 3 ? null : 1); t.Push("c", 0); }
        Assert.Equal(SensorQuality.Unavailable, t.Get("a")!.Quality);
        Assert.Equal(SensorQuality.Partial, t.Get("b")!.Quality);
        Assert.True(t.Get("c")!.AllZero);
        t.BeginTick(); t.Push("c", 2);
        Assert.False(t.Get("c")!.AllZero);
    }

    [Fact]
    public void LateSensorGetsGapsNotInventedHistory()
    {
        var t = new SensorTracker();
        for (int i = 0; i < 5; i++) { t.BeginTick(); t.Push("a", 1); }
        t.BeginTick(); t.Push("late", 7);
        var h = t.Get("late")!.History.ToArray();
        Assert.Equal(6, h.Length); // aligned to the 6 ticks of the other sensors
        Assert.All(h.Take(5), v => Assert.Null(v));
        Assert.Equal(7f, h[^1]);
    }

    [Fact]
    public void RingBufferOrder()
    {
        var r = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++) r.Push(i);
        Assert.Equal([3, 4, 5], r.ToArray());
        Assert.Equal([4, 5], r.ToArray(2));
    }
}

public class SettingsTests
{
    [Fact]
    public void CorruptOrInvalidFallsBackToDefaults()
    {
        Assert.Equal(Settings.Default, Settings.Parse("{not json"));
        Assert.Equal(Settings.Default, Settings.Parse("{\"version\":2}"));
        var s = Settings.Parse("{\"version\":1,\"theme\":\"purple\",\"opacity\":42,\"refreshMs\":500,\"palette\":\"lime\"}");
        Assert.Equal("dark", s.Theme); Assert.Equal(90, s.Opacity); Assert.Equal(500, s.RefreshMs); Assert.Equal("lime", s.Palette);
    }

    [Fact]
    public void RoundTrip()
    {
        var s = Settings.Default with { Theme = "light", History = 3600, StartHidden = false, CloseToTray = false, CardPicks = new Dictionary<string, string> { ["/intelcpu/0|Clock"] = "avg" }, DetailsWindow = new DetailsWindowState(10, 20, 960, 640, true) };
        Assert.Equal(s, Settings.Parse(s.ToJson()));
    }

    [Fact]
    public void AtomicSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(Settings.Default with { Palette = "ember" }); store.Flush();
            Assert.Equal("ember", store.Load().Palette);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class SettingsMigrationTests
{
    // legacy (0.1.33) values: real settings.json written by a Portuguese version of the app
    private const string LegacyUserFile = """
        {"version":1,"theme":"escuro","palette":"eletrico","opacity":90,"refreshMs":1000,"history":60,"scale":"independente","netUnit":"bytes","autohide":false,"closeToTray":true,"acModes":["curvas","silent","standard","turbo","full"],"acFans":["Chassis Fan 1","Chassis Fan 2","Chassis Fan 3","Chassis Fan 4"],"gameTargets":["gpu","board","pump","radiator","embedded"],"gameGpuPercent":100,"gameCoolerPercent":100,"popupTemps":["cpu","gpu","hot","vram","vrm","chip","liq","ram"],"popupBoardFans":["CPU Fan","Chassis Fan 1","Chassis Fan 2","Chassis Fan 3","Chassis Fan 4"],"popupCoolerFans":["pump","radiator","embedded"],"boardFanOff":true,"trayTemp":false,"trayAlert":false,"trayFontSize":"pp","trayTemps":[],"trayRotateSec":3,"gameExes":[],"acFanNames":{"Chassis Fan 1":"Exaustor","Chassis Fan 2":"Front top","Chassis Fan 3":"Front down","Chassis Fan 4":"Rear"},"autostart":true,"startHidden":true}
        """;

    private static Settings With(string key, string value) => Settings.Parse("{\"version\":1,\"" + key + "\":\"" + value + "\"}");

    [Fact]
    public void LegacyUserFileKeepsEveryChoice()
    {
        var s = Settings.Parse(LegacyUserFile);
        Assert.Equal("dark", s.Theme);
        Assert.Equal("electric", s.Palette);
        Assert.Equal("independent", s.Scale);
        Assert.Equal(["curves", "silent", "standard", "turbo", "full"], s.AcModes);
        Assert.Equal("xs", s.TrayFontSize);
        Assert.Empty(s.AcFanNames); // the old built-in nicknames are dropped: the board header shows
        Assert.Equal("CHA_FAN1", s.BoardFanName("Chassis Fan 1")); Assert.Equal("CHA_FAN3", s.BoardFanName("Chassis Fan 3"));
        Assert.True(s.BoardFanOff); Assert.True(s.Autostart); Assert.True(s.StartHidden);
        var json = s.ToJson();
        foreach (var old in new[] { "escuro", "eletrico", "independente", "curvas", "\"pp\"", "Exaustor", "Front down" }) // legacy (0.1.33) values
            Assert.DoesNotContain(old, json);
    }

    [Theory]
    [InlineData("theme", "claro", "light")] // legacy (0.1.33) value
    [InlineData("theme", "escuro", "dark")] // legacy (0.1.33) value
    [InlineData("theme", "sistema", "system")] // legacy (0.1.33) value
    [InlineData("palette", "eletrico", "electric")] // legacy (0.1.33) value
    [InlineData("palette", "lima", "lime")] // legacy (0.1.33) value
    [InlineData("palette", "brasa", "ember")] // legacy (0.1.33) value
    [InlineData("palette", "violeta", "violet")] // legacy (0.1.33) value
    [InlineData("palette", "aco", "steel")] // legacy (0.1.33) value
    [InlineData("palette", "neon", "neon")]
    [InlineData("scale", "independente", "independent")] // legacy (0.1.33) value
    [InlineData("scale", "compartilhada", "shared")] // legacy (0.1.33) value
    [InlineData("trayFontSize", "pp", "xs")] // legacy (0.1.33) value
    [InlineData("trayFontSize", "p", "s")] // legacy (0.1.33) value
    [InlineData("trayFontSize", "m", "m")]
    [InlineData("trayFontSize", "g", "l")] // legacy (0.1.33) value
    public void EachLegacyIdMapsToTheNewId(string key, string old, string expected)
    {
        var s = With(key, old);
        var actual = key switch { "theme" => s.Theme, "palette" => s.Palette, "scale" => s.Scale, _ => s.TrayFontSize };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NewIdsAreReadAsIs()
    {
        Assert.Equal("system", With("theme", "system").Theme);
        Assert.Equal("violet", With("palette", "violet").Palette);
        Assert.Equal("shared", With("scale", "shared").Scale);
        Assert.Equal("l", With("trayFontSize", "l").TrayFontSize);
    }

    [Fact]
    public void InvalidOrMisplacedValuesFallBackToDefault()
    {
        Assert.Equal(Settings.Default.Theme, With("theme", "purple").Theme);
        Assert.Equal(Settings.Default.Palette, With("palette", "claro").Palette); // legacy (0.1.33) theme value used as palette
        Assert.Equal(Settings.Default.Palette, With("palette", "light").Palette);
        Assert.Equal(Settings.Default.Scale, With("scale", "sistema").Scale); // legacy (0.1.33) value of another key
        Assert.Equal(Settings.Default.TrayFontSize, With("trayFontSize", "xxl").TrayFontSize);
    }

    [Theory]
    [InlineData("Chassis Fan 1", "Exaustor")] // legacy (0.1.33) value
    [InlineData("Chassis Fan 1", "Exhaust")]
    [InlineData("Chassis Fan 2", "Front top")]
    [InlineData("Chassis Fan 3", "Front down")]
    [InlineData("Chassis Fan 3", "Front bottom")]
    [InlineData("Chassis Fan 4", "Rear")]
    public void OldDefaultFanNamesAreDropped(string fan, string name)
    {
        var s = Settings.Parse("{\"version\":1,\"acFanNames\":{\"" + fan + "\":\"" + name + "\"}}");
        Assert.Empty(s.AcFanNames);
        Assert.Equal(ThermalMap.BoardHeader(fan), s.BoardFanName(fan));
        Assert.Equal(ThermalMap.BoardHeader(fan), s.BoardFanTip(fan));
    }

    [Fact]
    public void UserFanNamesAreKept()
    {
        // A default name on another connector is a user's choice; so is any other text.
        var s = Settings.Parse("{\"version\":1,\"acFanNames\":{\"Chassis Fan 1\":\"Rear\",\"Chassis Fan 2\":\"Meu fan\",\"Chassis Fan 3\":\"  Top intake  \",\"Chassis Fan 4\":\"\",\"CPU Fan\":\"A very long fan name here\"}}"); // legacy (0.1.33) custom name
        Assert.Equal("Rear", s.AcFanNames["Chassis Fan 1"]);
        Assert.Equal("Meu fan", s.AcFanNames["Chassis Fan 2"]);
        Assert.Equal("Top intake", s.AcFanNames["Chassis Fan 3"]); // trimmed
        Assert.False(s.AcFanNames.ContainsKey("Chassis Fan 4")); // empty = original
        Assert.Equal("A very long fan", s.AcFanNames["CPU Fan"]); // cut to 16 (and trimmed)
        Assert.Equal("Rear", s.BoardFanName("Chassis Fan 1"));
        Assert.Equal("Rear · CHA_FAN1", s.BoardFanTip("Chassis Fan 1"));
        Assert.Equal("CHA_FAN4", s.BoardFanName("Chassis Fan 4"));
        Assert.Equal(s, Settings.Parse(s.ToJson()));
    }

    [Fact]
    public void DefaultFanNamesAreTheHeaders()
    {
        Assert.Empty(Settings.Default.AcFanNames);
        Assert.Empty(Settings.Default.CoolerNames);
        Assert.Equal("CPU_FAN", Settings.Default.BoardFanName("CPU Fan"));
        Assert.Equal("CPU_OPT", Settings.Default.BoardFanName("CPU Optional Fan"));
        Assert.Equal("W_PUMP+", Settings.Default.BoardFanName("Water Pump+"));
        Assert.Equal("AIO_PUMP", Settings.Default.BoardFanName("AIO Pump"));
        Assert.Equal("CHA_FAN7", Settings.Default.BoardFanName("Chassis Fan 7")); // derived, not a fixed list
        Assert.Equal("PUMP", Settings.Default.CoolerName("pump"));
        Assert.Equal("RADIATOR", Settings.Default.CoolerName("radiator"));
        Assert.Equal("EMB.", Settings.Default.CoolerName("embedded"));
    }

    [Fact]
    public void CoolerNamesRoundTripAndOnlyKnownParts()
    {
        var s = Settings.Parse("{\"version\":1,\"coolerNames\":{\"pump\":\"Main pump\",\"radiator\":\" \",\"fan\":\"x\"}}");
        Assert.Equal(new Dictionary<string, string> { ["pump"] = "Main pump" }, s.CoolerNames);
        Assert.Equal("Main pump", s.CoolerName("pump"));
        Assert.Equal("Main pump · PUMP", s.CoolerTip("pump"));
        Assert.Equal("RADIATOR", s.CoolerName("radiator"));
        var back = Settings.Parse(s.ToJson());
        Assert.Equal(s, back);
        Assert.NotEqual(Settings.Default, Settings.Default with { CoolerNames = new Dictionary<string, string> { ["pump"] = "P" } });
        Assert.NotEqual(Settings.Default, Settings.Default with { AcFanNames = new Dictionary<string, string> { ["CPU Fan"] = "P" } });
    }

    [Fact]
    public void RenameSetsClearsAndCuts()
    {
        IReadOnlyDictionary<string, string> d = new Dictionary<string, string>();
        d = FanNames.Rename(d, "Chassis Fan 1", "  Exhaust ", "CHA_FAN1");
        Assert.Equal("Exhaust", d["Chassis Fan 1"]);
        d = FanNames.Rename(d, "Chassis Fan 2", "12345678901234567890", "CHA_FAN2");
        Assert.Equal("1234567890123456", d["Chassis Fan 2"]);
        d = FanNames.Rename(d, "Chassis Fan 1", "", "CHA_FAN1"); // empty = back to the original
        Assert.False(d.ContainsKey("Chassis Fan 1"));
        d = FanNames.Rename(d, "Chassis Fan 2", "cha_fan2", "CHA_FAN2"); // the original itself = not renamed
        Assert.Empty(d);
        Assert.Equal("pump", FanNames.CoolerPart(ThermalMap.PumpId));
        Assert.Equal("radiator", FanNames.CoolerPart(ThermalMap.RadiatorId));
        Assert.Equal("embedded", FanNames.CoolerPart(ThermalMap.EmbeddedId));
        Assert.Null(FanNames.CoolerPart(ThermalMap.GpuId));
    }

    [Fact]
    public void CoolerStripUsesTheUserNames()
    {
        var items = ThermalMap.CoolerStrip(2000, 60, [1000, 1010], 50, 3000, 70, p => p == "radiator" ? "Top rad" : null);
        Assert.Equal([null, "Top rad 1", "Top rad 2", null], items.Select(i => i.Nick));
        Assert.Equal(["PUMP", "RAD 1", "RAD 2", "EMBEDDED"], items.Select(i => i.Name));
    }

    [Fact]
    public void AcModeCurvasBecomesCurves()
    {
        Assert.Equal("curves", Settings.MigrateAcMode("curvas")); // legacy (0.1.33) value
        Assert.Equal("silent", Settings.MigrateAcMode("silent"));
        Assert.Equal("curves", Settings.MigrateAcMode("curves"));
        Assert.Null(Settings.MigrateAcMode(null));
        Assert.Equal(["curves", "full"], Settings.Parse("{\"version\":1,\"acModes\":[\"curvas\",\"full\",\"curves\"]}").AcModes); // legacy (0.1.33) value
    }
}


public class RateHistoryTests
{
    [Fact]
    public void OneMinuteIsPerSecondWithPeakOfTicks()
    {
        var h = new RateHistory();
        for (long s = 1000; s < 1100; s++) { h.Add(s, s); h.Add(0, s); } // 2 ticks/s: peak = s
        var v = h.View(60);
        Assert.Equal(61, v.Length); Assert.Equal(1099, v[^1]); Assert.Equal(1039, v[0]);
        Assert.Equal(60, RateHistory.Points(60));
    }

    [Fact]
    public void TwoHoursIs120PointsOfOneMinutePeaks()
    {
        Assert.Equal(60, RateHistory.BucketSeconds(7200)); Assert.Equal(120, RateHistory.Points(7200));
        Assert.Equal(3, RateHistory.BucketSeconds(300)); Assert.Equal(100, RateHistory.Points(300));
        var h = new RateHistory();
        for (long s = 0; s <= 7260; s++) h.Add(s == 5000 ? 999 : 1, s);
        var v = h.View(7200);
        Assert.Equal(121, v.Length);
        Assert.Contains(999, v);           // short peak stays visible
        Assert.Equal(1, v[^1]);
    }

    [Fact]
    public void ShortUptimeGivesFewerPointsAndGapsAreZero()
    {
        var h = new RateHistory();
        h.Add(5, 100); h.Add(5, 101); h.Add(7, 110); // 102..109 suspended
        var v = h.View(60);
        Assert.Equal(11, v.Length); Assert.Equal(0, v[3]); Assert.Equal(7, v[^1]);
        Assert.Empty(new RateHistory().View(60));
        h.Add(1000, 50); Assert.Equal(7, h.View(60)[^1]); // an old second is ignored
    }

    [Fact]
    public void OldHistoryValuesFallBackToOneMinute()
    {
        Assert.Equal(60, Settings.Parse("{\"version\":1,\"history\":120}").History);
        Assert.Equal(7200, Settings.Parse("{\"version\":1,\"history\":7200}").History);
        Assert.Equal(60, Settings.Parse("{\"version\":1,\"history\":86400}").History);
    }
}
