using MonitorTray.Core.Detail;
using MonitorTray.Core.History;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Sensors;
using Xunit;

namespace MonitorTray.Core.Tests;

public class NormalizeTests
{
    private static string DataPath => Path.Combine(AppContext.BaseDirectory, "data", "sensores.json");

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
        Assert.StartsWith("Física · Memory", P(mem, "Load"));
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
        var mem = devs.Where(d => d.Category == "Memória").ToList();
        Assert.Single(mem);
        var m = mem[0];
        Assert.Equal("family:Memory", m.Id);
        Assert.Equal([(1, 1), (2, 3)], m.Modules.Select(x => (x.Index, x.Slot)));
        Assert.Contains(m.Sensors, s => s.Name.StartsWith("Módulo 1 · ", StringComparison.Ordinal));
        Assert.Contains(m.Sensors, s => s.Name.StartsWith("Virtual · ", StringComparison.Ordinal));
        var panel = DetailModel.Panel(m, m.Sensors, new SensorTracker());
        Assert.Contains("2 módulos · módulo 1 no slot 1, módulo 2 no slot 3", panel.Meta);
    }

    [Fact]
    public void NetworkGenericNamesAndDuplicates()
    {
        var (devs, _, _) = Simulate();
        var net = devs.Single(d => d.Category == "Rede");
        Assert.Equal("Rede", net.DisplayName);
        Assert.Contains(net.Sensors, s => s.Name.StartsWith("Adaptador de rede 1 · ", StringComparison.Ordinal));
        Assert.Contains(net.Sensors, s => s.Name.StartsWith("Adaptador de rede 2 · ", StringComparison.Ordinal));
        var kc = devs.Where(d => d.RawName == "KINGSTON SKC3000D2048G").Select(d => d.DisplayName).Order().ToList();
        Assert.Equal(["KINGSTON SKC3000D2048G · hw-1", "KINGSTON SKC3000D2048G · hw-2"], kc);
    }

    [Fact]
    public void CategoryOrder()
    {
        var (devs, _, _) = Simulate();
        var cats = devs.Select(d => d.Category).Distinct().ToList();
        Assert.Equal(["CPU", "GPU", "Memória", "Placa-mãe", "Armazenamento", "Rede"], cats);
    }

    [Fact]
    public void RolesAndTags()
    {
        var (devs, _, _) = Simulate();
        var ssd = devs.First(d => d.Family == "Storage");
        Assert.Equal(SensorRole.Limit, ssd.Sensors.First(s => s.Name == "Warning Temperature").Role);
        Assert.Equal(SensorRole.Limit, ssd.Sensors.First(s => s.Name == "Critical Temperature").Role);
        Assert.Equal("acumulado", ssd.Sensors.First(s => s.Name == "Data Read").Tag);
        Assert.Equal("estimativa", ssd.Sensors.First(s => s.Name == "Life").Tag);
        Assert.Equal(SensorRole.Reading, ssd.Sensors.First(s => s.Name == "Composite Temperature").Role);
        var mb = devs.Where(d => d.Category == "Placa-mãe").SelectMany(d => d.Sensors).ToList();
        foreach (var n in new[] { "Water In", "Water Out", "T Sensor" })
        {
            var s = mb.First(x => x.Name == n);
            Assert.Equal("sem leitura", s.Tag); Assert.Null(s.Value);
        }
        Assert.True(mb.First(x => x.Name == "Water Flow").AllZero);
        var mem = devs.Single(d => d.Family == "Memory");
        Assert.All(mem.Sensors.Where(s => s.KindRaw == "Timing"), s => Assert.Equal(SensorRole.Metadata, s.Role));
        var cpu = devs.First(d => d.Category == "CPU");
        Assert.All(cpu.Sensors.Where(s => s.Name.Contains("Distance to TjMax")), s => Assert.Equal("margem", s.Tag));
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
        Assert.Equal("limite", w.Tag);
        Assert.True(v.HiddenLimits > 0 && v.HiddenZero > 0);
    }

    [Fact]
    public void CpuGridsAndCards()
    {
        var (devs, tracker, _) = Simulate(30);
        var cpu = devs.First(d => d.Category == "CPU");
        var v = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id);
        var p = v.Panel!;
        Assert.Equal("CPU Package", p.Cards.First(c => c.Label == "Temperatura").SensorName);
        Assert.Equal("CPU Total", p.Cards.First(c => c.Label == "Carga").SensorName);
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
        Assert.True(def.Options.Count >= 10); // média, máximo e cada núcleo (lista vem do hardware)
        var avg = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id, new Dictionary<string, string> { [key] = "avg" }).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.StartsWith("Média", avg.SensorName);
        var clocks = cpu.Sensors.Where(s => s.KindRaw == "Clock" && s.Unit == "MHz" && s.Value != null).Select(s => (double)s.Value!.Value).ToList();
        Assert.Equal(clocks.Average(), avg.Raw!.Value, 3);
        var core = def.Options.Last();
        var one = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id, new Dictionary<string, string> { [key] = core.Key }).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.Equal(core.Label, one.SensorName);
        var bad = DetailModel.Build(devs, tracker, Filters.Default, cpu.Id, new Dictionary<string, string> { [key] = "sumiu" }).Panel!.Cards.First(c => c.Label == "Clock");
        Assert.Equal(def.SensorName, bad.SensorName); // escolha inválida volta ao padrão
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
        var v = DetailModel.Build(devs, tracker, Filters.Default with { Query = "tensao" }, null);
        Assert.NotEmpty(v.Devices);
        Assert.All(v.Panel!.Sections, s => Assert.Equal("Voltage", s.KindRaw));
        var none = DetailModel.Build(devs, tracker, Filters.Default with { Query = "xyzzy" }, null);
        Assert.Empty(none.Devices); Assert.Null(none.Panel);
    }

    [Fact]
    public void UnknownTypeInPsuRenders()
    {
        var (devs, tracker, _) = Simulate(fake: true);
        var psu = devs.Single(d => d.Category == "Fonte");
        var v = DetailModel.Build(devs, tracker, Filters.Default, psu.Id);
        Assert.Contains(v.Panel!.Sections, s => s.Label == "Corrente");
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
            S(NetTrafficIds.Internet, "Tráfego internet", "Download", 3000), S(NetTrafficIds.Internet, "Tráfego internet", "Upload", 50),
            S(NetTrafficIds.Local, "Tráfego rede local", "Download", 2000), S(NetTrafficIds.Local, "Tráfego rede local", "Upload", 50),
            S(NetTrafficIds.Total, "Tráfego total", "Download", 5000), S(NetTrafficIds.Total, "Tráfego total", "Upload", 100),
        };
        var t = new SensorTracker();
        for (int i = 0; i < 3; i++) { t.BeginTick(); foreach (var r in raw) t.Push(r.Id, r.Value + i); }
        var devs = DeviceBuilder.Build(raw, t, _ => false);
        var net = devs.Single(d => d.Category == "Rede");
        var panel = DetailModel.Panel(net, net.Sensors, t);
        Assert.Equal(["Total", "Rede local", "Internet", "Ethernet"], panel.Sections.Select(s => s.Label));
        Assert.Equal(["Total", "Rede local", "Internet"], panel.Cards.Select(c => c.Label));
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
        Assert.Equal(6, h.Length); // alinhado aos 6 ticks dos demais sensores
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
        var s = Settings.Parse("{\"version\":1,\"theme\":\"roxo\",\"opacity\":42,\"refreshMs\":500,\"palette\":\"lima\"}");
        Assert.Equal("escuro", s.Theme); Assert.Equal(90, s.Opacity); Assert.Equal(500, s.RefreshMs); Assert.Equal("lima", s.Palette);
    }

    [Fact]
    public void RoundTrip()
    {
        var s = Settings.Default with { Theme = "claro", History = 3600, StartHidden = false, CloseToTray = false, CardPicks = new Dictionary<string, string> { ["/intelcpu/0|Clock"] = "avg" }, DetailsWindow = new DetailsWindowState(10, 20, 960, 640, true) };
        Assert.Equal(s, Settings.Parse(s.ToJson()));
    }

    [Fact]
    public void AtomicSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            store.Save(Settings.Default with { Palette = "brasa" }); store.Flush();
            Assert.Equal("brasa", store.Load().Palette);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, true); }
    }
}


public class RateHistoryTests
{
    [Fact]
    public void OneMinuteIsPerSecondWithPeakOfTicks()
    {
        var h = new RateHistory();
        for (long s = 1000; s < 1100; s++) { h.Add(s, s); h.Add(0, s); } // 2 ticks/s: pico = s
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
        Assert.Contains(999, v);           // pico curto continua visível
        Assert.Equal(1, v[^1]);
    }

    [Fact]
    public void ShortUptimeGivesFewerPointsAndGapsAreZero()
    {
        var h = new RateHistory();
        h.Add(5, 100); h.Add(5, 101); h.Add(7, 110); // 102..109 suspenso
        var v = h.View(60);
        Assert.Equal(11, v.Length); Assert.Equal(0, v[3]); Assert.Equal(7, v[^1]);
        Assert.Empty(new RateHistory().View(60));
        h.Add(1000, 50); Assert.Equal(7, h.View(60)[^1]); // segundo antigo é ignorado
    }

    [Fact]
    public void OldHistoryValuesFallBackToOneMinute()
    {
        Assert.Equal(60, Settings.Parse("{\"version\":1,\"history\":120}").History);
        Assert.Equal(7200, Settings.Parse("{\"version\":1,\"history\":7200}").History);
        Assert.Equal(60, Settings.Parse("{\"version\":1,\"history\":86400}").History);
    }
}
