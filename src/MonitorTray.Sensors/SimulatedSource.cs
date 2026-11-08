using System.Text.Json;
using MonitorTray.Core.Model;

namespace MonitorTray.Sensors;

/// <summary>--simulate: catálogo do kit com passeio aleatório ±15% do span por tick (docs/06 §8).</summary>
public sealed class SimulatedSource : IHardwareSource
{
    private sealed record Entry(RawSensor Template, double? Min, double? Mean, double? Max, bool AllZero, bool Unavailable);
    private readonly string path;
    private readonly Random rng;
    private List<Entry> entries = [];
    private readonly Dictionary<string, double?> live = new();
    public bool IsSimulated => true;
    public string? Notice { get; private set; }

    public SimulatedSource(string path, int? seed = null) { this.path = path; rng = seed is int s ? new Random(s) : new Random(); }

    public void Open()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<Entry>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string S(string k) => e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
            double? N(string k) => e.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
            var family = S("family");
            var hwId = S("hwId");
            if (family == "CpuGpu") family = hwId.Contains("gpu-nvidia") ? "GpuNvidia" : hwId.Contains("gpu-amd") ? "GpuAmd" : hwId.Contains("gpu-intel") ? "GpuIntel" : "Cpu";
            if (family == "Motherboard" && !hwId.EndsWith("hw-0", StringComparison.Ordinal))
                family = S("hardware").Contains("Embedded", StringComparison.OrdinalIgnoreCase) ? "EmbeddedController" : "SuperIO";
            var unav = S("state") == "indisponível";
            var t = new RawSensor(S("id"), hwId, S("hardware"), family, S("name"), S("type"), S("unit"), null);
            list.Add(new Entry(t, N("min"), N("mean"), N("max"), e.TryGetProperty("allZero", out var az) && az.ValueKind == JsonValueKind.True, unav));
        }
        // Tráfego por tipo (como o ETW real produz), para a aba Rede no modo simulado.
        foreach (var (dev, name, d, u) in new[] { (Core.Normalize.NetTrafficIds.Total, "Tráfego total", 6.0e6, 3.0e5), (Core.Normalize.NetTrafficIds.Local, "Tráfego rede local", 2.0e6, 1.0e5), (Core.Normalize.NetTrafficIds.Internet, "Tráfego internet", 4.0e6, 2.0e5) })
        {
            list.Add(new Entry(new RawSensor(dev + "/throughput/0", dev, name, "Network", "Download", "Throughput", "B/s", null), 0, d / 2, d, false, false));
            list.Add(new Entry(new RawSensor(dev + "/throughput/1", dev, name, "Network", "Upload", "Throughput", "B/s", null), 0, u / 2, u, false, false));
        }
        entries = list;
        Notice = "Modo simulado: " + Path.GetFileName(path);
    }

    /// <summary>Adiciona um dispositivo inventado (teste de tipos desconhecidos e da tag NOVO).</summary>
    public void AddFakeDevice()
    {
        (string Type, string Name, string Unit, double Min, double Max)[] fake =
            [("Power", "Total Output", "W", 180, 262), ("Voltage", "+12V", "V", 12.04, 12.12), ("Current", "+12V", "A", 14.2, 20.6), ("Temperature", "VRM", "degC", 38, 41.5), ("Fan", "Fan", "RPM", 0, 0)];
        for (int i = 0; i < fake.Length; i++)
        {
            var f = fake[i];
            entries.Add(new Entry(new RawSensor("/psu/sim/0/" + i, "/psu/sim/0", "Fonte simulada", "Psu", f.Name, f.Type, f.Unit, null), f.Min, (f.Min + f.Max) / 2, f.Max, f.Max == 0, false));
        }
    }

    public IReadOnlyList<RawSensor> Read()
    {
        var list = new List<RawSensor>(entries.Count);
        foreach (var e in entries)
        {
            double? v;
            if (e.Unavailable || e.Mean is null) v = null;
            else if (e.AllZero) v = 0;
            else if (e.Min == e.Max) v = e.Min;
            else
            {
                var span = e.Max!.Value - e.Min!.Value;
                var prev = live.TryGetValue(e.Template.Id, out var p) && p is double pv ? pv : e.Mean.Value;
                v = Math.Clamp(prev + (rng.NextDouble() - 0.5) * span * 0.3, e.Min.Value, e.Max.Value);
            }
            live[e.Template.Id] = v;
            list.Add(e.Template with { Value = v is double d ? (float)d : null });
        }
        return list;
    }

    public void Dispose() { }
}

/// <summary>Resumo simulado (sem LHM): passeio com saltos de 6% (docs/06 §8).</summary>
public sealed class SimulatedSummary
{
    private readonly Random rng;
    private double cpu = 18, gpu = 31, mem = 11.4, rd = 2.1, wr = 0.8, down = 900, up = 60;
    public SimulatedSummary(int? seed = null) { rng = seed is int s ? new Random(s) : new Random(); }

    private double Walk(double v, double lo, double hi, double step) =>
        rng.NextDouble() < 0.06 ? rng.NextDouble() * hi : Math.Clamp(v + (rng.NextDouble() - 0.5) * step * 2, lo, hi);

    public SummaryData Next()
    {
        down = Walk(down, 0, 14000, 1200); up = Walk(up, 0, 900, 90);
        gpu = Math.Round(Walk(gpu, 0, 100, 7)); cpu = Math.Round(Walk(cpu, 2, 100, 8));
        mem = Walk(mem, 8, 30, 0.15);
        rd = rng.NextDouble() < 0.15 ? rng.NextDouble() * 420 : Math.Max(0, rd * 0.6 + rng.NextDouble() * 3);
        wr = rng.NextDouble() < 0.1 ? rng.NextDouble() * 260 : Math.Max(0, wr * 0.6 + rng.NextDouble() * 2);
        const double GiB = 1024d * 1024 * 1024, MiB = 1024d * 1024;
        return new SummaryData((float)cpu, (float)(38 + cpu * 0.4), (float)(12 + cpu * 1.1),
            (float)gpu, (float)(34 + gpu * 0.45), (float)Math.Round(18 + gpu * 2.6 + (rng.NextDouble() - 0.5) * 12),
            (ulong)(mem * GiB), (ulong)(32 * GiB), rd * MiB, wr * MiB, down * 1024, up * 1024);
    }
}
