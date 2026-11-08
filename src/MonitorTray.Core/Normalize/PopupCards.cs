using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

/// <summary>
/// Limites de alerta dos cards de temperatura (°C). CPU, GPU, VRAM e líquido são os das proteções reais do app;
/// os demais não têm proteção no app e usam limites conservadores documentados aqui.
/// </summary>
public static class TempLimits
{
    /// <summary>CPU (Package, núcleo mais quente, soquete): acima disto o OFF dos fans da placa religa tudo (BoardFanOff).</summary>
    public const float CpuC = 85;
    /// <summary>GPU Core: GpuSafety bloqueia reduzir a fan a partir de 80 °C (ou slowdown − 3, o menor).</summary>
    public const float GpuC = 80;
    /// <summary>VRAM (Memory Junction): GpuSafety bloqueia reduzir a fan a partir de 95 °C.</summary>
    public const float VramC = 95;
    /// <summary>Líquido do water cooler: bomba e radiador vão a 100% a partir de 50 °C (CoolerSafety.WaterPanicC); o Auto Tuning aborta acima de 45 °C.</summary>
    public const float LiquidC = 50;
    /// <summary>GPU Hot Spot: sem proteção própria no app; 15 °C acima do limite do core, abaixo do limite de throttle do driver.</summary>
    public const float HotSpotC = 95;
    /// <summary>VRM da placa: componentes suportam ~125 °C; 100 °C deixa folga para avisar antes do throttle da BIOS.</summary>
    public const float VrmC = 100;
    /// <summary>Chipset: junção suporta ~100 °C; 90 °C já é anormal em uso contínuo.</summary>
    public const float ChipsetC = 90;
    /// <summary>Sensor "Motherboard" (ar dentro do gabinete, perto da placa): acima de 70 °C o fluxo de ar está ruim.</summary>
    public const float MotherboardC = 70;
    /// <summary>RAM (DIMM): 85 °C é o limite JEDEC de operação normal (acima a memória dobra o refresh).</summary>
    public const float RamC = 85;
    /// <summary>"Perto do limite": a partir de limite − 5 °C (cor de alerta no card e no ícone da bandeja).</summary>
    public const float NearMarginC = 5;
}

/// <summary>Nível de alerta de uma temperatura em relação ao limite do papel.</summary>
public enum TempLevel { Normal, Near, Over }

/// <summary>Temperatura escolhida para o ícone da bandeja: a maior entre os cards habilitados e o pior nível entre eles.</summary>
public sealed record TrayTemp(string Id, float Value, TempLevel Worst);

/// <summary>
/// Cards de temperatura do popup: catálogo fixo de papéis e resolução por família + nome do sensor do LHM
/// (nunca por modelo de hardware). Papel sem leitura não entra no dicionário: o card é omitido.
/// </summary>
public static class TempCards
{
    /// <param name="LimitC">Limite de alerta (TempLimits): perto a partir de limite − 5 °C; a barra enche no limite.</param>
    public sealed record Def(string Id, string Label, string Tip, float LimitC);

    public static readonly IReadOnlyList<Def> All =
    [
        new("cpu", "CPU", "CPU Package", TempLimits.CpuC),
        new("coremax", "CMAX", "CPU · núcleo mais quente (Core Max)", TempLimits.CpuC),
        new("gpu", "GPU", "GPU Core", TempLimits.GpuC),
        new("hot", "HOT", "GPU Hot Spot", TempLimits.HotSpotC),
        new("vram", "VRAM", "GPU Memory Junction (VRAM)", TempLimits.VramC),
        new("socket", "SOCK", "Placa · soquete da CPU", TempLimits.CpuC),
        new("mobo", "MOBO", "Placa · Motherboard", TempLimits.MotherboardC),
        new("chip", "CHIP", "Placa · Chipset", TempLimits.ChipsetC),
        new("vrm", "VRM", "Placa · VRM", TempLimits.VrmC),
        new("liq", "LÍQ", "Líquido do water cooler", TempLimits.LiquidC),
        new("ram", "RAM", "Memória · DIMM mais quente", TempLimits.RamC),
    ];

    public static readonly IReadOnlyList<string> DefaultIds = ["cpu", "gpu", "hot", "vram", "vrm", "chip", "liq", "ram"];
    /// <summary>Cabem 8 cards na largura do popup (344 px).</summary>
    public const int MaxCards = 8;
    public static readonly IReadOnlyList<string> Ids = All.Select(d => d.Id).ToList();

    public static Def? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>Normal, perto (≥ limite − 5 °C) ou no limite (≥ limite).</summary>
    public static TempLevel Level(Def d, double value) =>
        value >= d.LimitC ? TempLevel.Over : value >= d.LimitC - TempLimits.NearMarginC ? TempLevel.Near : TempLevel.Normal;

    /// <summary>
    /// Maior temperatura entre os cards habilitados que têm leitura, e o pior nível entre eles (um card perto do limite
    /// pode não ser o mais quente: VRAM 70 °C normal e GPU 77 °C perto). null = nenhum card habilitado com leitura.
    /// </summary>
    public static TrayTemp? Hottest(IEnumerable<string> enabled, IReadOnlyDictionary<string, float> values)
    {
        TrayTemp? best = null;
        var worst = TempLevel.Normal;
        foreach (var id in enabled)
        {
            if (Find(id) is not { } d || !values.TryGetValue(id, out var v)) continue;
            var l = Level(d, v);
            if (l > worst) worst = l;
            if (best == null || v > best.Value) best = new TrayTemp(id, v, TempLevel.Normal);
        }
        return best == null ? null : best with { Worst = worst };
    }

    /// <summary>
    /// Temperatura do ícone da bandeja. Sem escolha: a mais quente entre os cards. Com escolha: as que têm leitura, em
    /// rodízio a cada <paramref name="rotateSec"/> segundos (relógio <paramref name="nowMs"/>). O nível (cor de alerta) é o pior
    /// entre os cards e as escolhidas, mesmo que o número mostrado no momento seja outro.
    /// </summary>
    public static TrayTemp? ForTray(IReadOnlyList<string> chosen, IEnumerable<string> cards, IReadOnlyDictionary<string, float> values, long nowMs, int rotateSec)
    {
        var all = cards.Concat(chosen).Distinct().ToList();
        var worst = Hottest(all, values)?.Worst ?? TempLevel.Normal;
        var avail = chosen.Where(id => Find(id) != null && values.ContainsKey(id)).Distinct().ToList();
        if (avail.Count == 0) return Hottest(chosen.Count == 0 ? cards : all, values) is { } h ? h with { Worst = worst } : null;
        int i = avail.Count == 1 || rotateSec <= 0 ? 0 : (int)(Math.Max(0, nowMs) / 1000 / rotateSec % avail.Count);
        return new TrayTemp(avail[i], values[avail[i]], worst);
    }

    /// <summary>
    /// Junta as fontes: LHM primeiro; GPU/Hot Spot/VRAM pela NVML quando o LHM não tem; líquido pelo cooler.
    /// Valores fora de 1..150 °C (ou 0 da NVML) ficam de fora.
    /// </summary>
    public static Dictionary<string, float> Merge(IReadOnlyDictionary<string, float> lhm, double? liquidC, uint? gpuC, uint? hotC, uint? vramC)
    {
        var d = new Dictionary<string, float>(lhm);
        if (liquidC is double w and > 1 and < 150) d.TryAdd("liq", (float)w);
        if (gpuC is uint g and > 0 and < 150) d.TryAdd("gpu", g);
        if (hotC is uint h and > 0 and < 150) d.TryAdd("hot", h);
        if (vramC is uint m and > 0 and < 150) d.TryAdd("vram", m);
        return d;
    }

    /// <summary>Lê os papéis disponíveis nas leituras cruas (°C). "liq" vem do cooler, não do LHM.</summary>
    public static Dictionary<string, float> Resolve(IReadOnlyList<RawSensor> raw) => Resolve(raw, null);

    /// <param name="sources">Recebe o id do sensor do LHM de cada papel (abrir o Detalhes nele).</param>
    public static Dictionary<string, float> Resolve(IReadOnlyList<RawSensor> raw, Dictionary<string, string>? sources)
    {
        var d = new Dictionary<string, float>();
        void Put(string id, RawSensor? s)
        {
            if (s?.Value is float x and > 1 and < 150 && d.TryAdd(id, x)) sources?.TryAdd(id, s.Id);
        }
        static IEnumerable<RawSensor> First(IReadOnlyList<RawSensor> all, Func<string, bool> family)
        {
            var dev = all.Where(s => family(s.Family)).Select(s => s.DeviceId).Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
            return dev == null ? [] : all.Where(s => s.DeviceId == dev);
        }
        static RawSensor? Named(IEnumerable<RawSensor> s, string name) =>
            s.FirstOrDefault(x => x.KindRaw == "Temperature" && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) && x.Value is float);

        var cpu = First(raw, f => f == "Cpu").ToList();
        Put("cpu", Named(cpu, "CPU Package"));
        Put("coremax", Named(cpu, "Core Max"));
        var gpu = First(raw, SummaryPicker.IsGpuFamily).ToList();
        Put("gpu", Named(gpu, "GPU Core"));
        Put("hot", Named(gpu, "GPU Hot Spot"));
        Put("vram", Named(gpu, "GPU Memory Junction"));
        var sio = raw.Where(r => r.Family == "SuperIO").ToList();
        var board = raw.Where(r => r.Family is "SuperIO" or "EmbeddedController").ToList();
        Put("socket", Named(sio, "CPU"));
        Put("mobo", Named(board, "Motherboard"));
        Put("chip", Named(board, "Chipset"));
        Put("vrm", Named(board, "VRM"));
        Put("ram", raw.Where(r => r.Family == "Memory" && r.KindRaw == "Temperature" && r.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase) && r.Value is float v and > 1 and < 150)
            .MaxBy(r => r.Value));
        return d;
    }
}

/// <summary>Mínimo e máximo de cada papel de temperatura desde que o app abriu (tooltip dos cards).</summary>
public sealed class TempSession
{
    private readonly Dictionary<string, (float Min, float Max)> range = new();

    public void Push(IReadOnlyDictionary<string, float> values)
    {
        lock (range)
            foreach (var (id, v) in values)
                range[id] = range.TryGetValue(id, out var r) ? (Math.Min(r.Min, v), Math.Max(r.Max, v)) : (v, v);
    }

    public (float Min, float Max)? Range(string id) { lock (range) return range.TryGetValue(id, out var r) ? r : null; }
}

/// <summary>Listas de escolha das Configurações (o que aparece no popup e em que ordem).</summary>
public static class PickList
{
    /// <summary>Só ids conhecidos, sem repetição, no máximo <paramref name="max"/>; mantém a ordem.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string> list, IReadOnlyCollection<string> known, int max = int.MaxValue) =>
        list.Where(known.Contains).Distinct().Take(max).ToList();

    /// <summary>Liga (vai para o fim) ou desliga um id. Ligar além de <paramref name="max"/> não muda nada.</summary>
    public static IReadOnlyList<string> Toggle(IReadOnlyList<string> list, string id, bool on, int max = int.MaxValue)
    {
        if (!on) return list.Where(x => x != id).ToList();
        if (list.Contains(id) || list.Count >= max) return list;
        return [.. list, id];
    }

    /// <summary>Move um id ligado uma posição para cima (-1) ou para baixo (+1); nas pontas não muda.</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> list, string id, int delta)
    {
        var l = list.ToList();
        int i = l.IndexOf(id), j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= l.Count) return list;
        (l[i], l[j]) = (l[j], l[i]);
        return l;
    }

    /// <summary>Ordem da lista das Configurações: ligados na ordem escolhida, depois os desligados na ordem do catálogo.</summary>
    public static IReadOnlyList<string> Display(IReadOnlyList<string> on, IEnumerable<string> catalog) =>
        [.. on, .. catalog.Where(x => !on.Contains(x))];
}
