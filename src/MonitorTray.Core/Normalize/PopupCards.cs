using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

/// <summary>
/// Alert limits of the temperature cards (°C). CPU, GPU, VRAM and liquid match the app's real protections;
/// the others have no protection in the app and use conservative limits documented here.
/// </summary>
public static class TempLimits
{
    /// <summary>CPU (Package, hottest core, socket): above this, the board fans' OFF turns everything back on (BoardFanOff).</summary>
    public const float CpuC = 85;
    /// <summary>GPU Core: GpuSafety blocks lowering the fan from 80 °C (or slowdown − 3, whichever is lower).</summary>
    public const float GpuC = 80;
    /// <summary>VRAM (Memory Junction): GpuSafety blocks lowering the fan from 95 °C.</summary>
    public const float VramC = 95;
    /// <summary>Water cooler liquid: pump and radiator go to 100% from 50 °C (CoolerSafety.WaterPanicC); Auto Tuning aborts above 45 °C.</summary>
    public const float LiquidC = 50;
    /// <summary>GPU Hot Spot: no dedicated protection in the app; 15 °C above the core limit, below the driver's throttle limit.</summary>
    public const float HotSpotC = 95;
    /// <summary>Board VRM: components handle ~125 °C; 100 °C leaves room to warn before the BIOS throttles.</summary>
    public const float VrmC = 100;
    /// <summary>Chipset: the junction handles ~100 °C; 90 °C is already abnormal under sustained load.</summary>
    public const float ChipsetC = 90;
    /// <summary>"Motherboard" sensor (air inside the case, near the board): above 70 °C airflow is poor.</summary>
    public const float MotherboardC = 70;
    /// <summary>RAM (DIMM): 85 °C is the JEDEC normal operating limit (above it the memory doubles the refresh rate).</summary>
    public const float RamC = 85;
    /// <summary>"Near the limit": from limit − 5 °C (alert color on the card and on the tray icon).</summary>
    public const float NearMarginC = 5;
}

/// <summary>Alert level of a temperature relative to the role's limit.</summary>
public enum TempLevel { Normal, Near, Over }

/// <summary>Temperature chosen for the tray icon: the highest among the enabled cards and the worst level among them.</summary>
public sealed record TrayTemp(string Id, float Value, TempLevel Worst);

/// <summary>
/// Popup temperature cards: fixed catalog of roles, resolved by family + LHM sensor name
/// (never by hardware model). A role without a reading is not in the dictionary: the card is omitted.
/// </summary>
public static class TempCards
{
    /// <param name="LimitC">Alert limit (TempLimits): near from limit − 5 °C; the bar is full at the limit.</param>
    public sealed record Def(string Id, string Label, string Tip, float LimitC);

    public static readonly IReadOnlyList<Def> All =
    [
        new("cpu", "CPU", "CPU Package", TempLimits.CpuC),
        new("coremax", "CMAX", "CPU · hottest core (Core Max)", TempLimits.CpuC),
        new("gpu", "GPU", "GPU Core", TempLimits.GpuC),
        new("hot", "HOT", "GPU Hot Spot", TempLimits.HotSpotC),
        new("vram", "VRAM", "GPU Memory Junction (VRAM)", TempLimits.VramC),
        new("socket", "SOCK", "Board · CPU socket", TempLimits.CpuC),
        new("mobo", "MOBO", "Board · Motherboard", TempLimits.MotherboardC),
        new("chip", "CHIP", "Board · Chipset", TempLimits.ChipsetC),
        new("vrm", "VRM", "Board · VRM", TempLimits.VrmC),
        new("liq", "LIQ", "Water cooler liquid", TempLimits.LiquidC),
        new("ram", "RAM", "Memory · hottest DIMM", TempLimits.RamC),
    ];

    public static readonly IReadOnlyList<string> DefaultIds = ["cpu", "gpu", "hot", "vram", "vrm", "chip", "liq", "ram"];
    /// <summary>8 cards fit in the popup width (344 px).</summary>
    public const int MaxCards = 8;
    public static readonly IReadOnlyList<string> Ids = All.Select(d => d.Id).ToList();

    public static Def? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>Normal, near (≥ limit − 5 °C) or at the limit (≥ limit).</summary>
    public static TempLevel Level(Def d, double value) =>
        value >= d.LimitC ? TempLevel.Over : value >= d.LimitC - TempLimits.NearMarginC ? TempLevel.Near : TempLevel.Normal;

    /// <summary>
    /// Highest temperature among the enabled cards that have a reading, and the worst level among them (a card near its limit
    /// may not be the hottest: VRAM 70 °C normal and GPU 77 °C near). null = no enabled card with a reading.
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
    /// Tray icon temperature. No choice: the hottest among the cards. With a choice: the ones that have a reading, rotating
    /// every <paramref name="rotateSec"/> seconds (clock <paramref name="nowMs"/>). The level (alert color) is the worst
    /// among the cards and the chosen ones, even if the number shown at the moment is a different one.
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
    /// Merges the sources: LHM first; GPU/Hot Spot/VRAM from NVML when LHM lacks them; liquid from the cooler.
    /// Values outside 1..150 °C (or 0 from NVML) are left out.
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

    /// <summary>Reads the roles available in the raw readings (°C). "liq" comes from the cooler, not from LHM.</summary>
    public static Dictionary<string, float> Resolve(IReadOnlyList<RawSensor> raw) => Resolve(raw, null);

    /// <param name="sources">Receives the LHM sensor id of each role (to open Details on it).</param>
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

/// <summary>Minimum and maximum of each temperature role since the app started (card tooltip).</summary>
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

/// <summary>Settings pick lists (what shows in the popup and in which order).</summary>
public static class PickList
{
    /// <summary>Known ids only, no repeats, at most <paramref name="max"/>; keeps the order.</summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string> list, IReadOnlyCollection<string> known, int max = int.MaxValue) =>
        list.Where(known.Contains).Distinct().Take(max).ToList();

    /// <summary>Turns an id on (goes to the end) or off. Turning on beyond <paramref name="max"/> changes nothing.</summary>
    public static IReadOnlyList<string> Toggle(IReadOnlyList<string> list, string id, bool on, int max = int.MaxValue)
    {
        if (!on) return list.Where(x => x != id).ToList();
        if (list.Contains(id) || list.Count >= max) return list;
        return [.. list, id];
    }

    /// <summary>Moves an enabled id one position up (-1) or down (+1); at the ends nothing changes.</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> list, string id, int delta)
    {
        var l = list.ToList();
        int i = l.IndexOf(id), j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= l.Count) return list;
        (l[i], l[j]) = (l[j], l[i]);
        return l;
    }

    /// <summary>Settings list order: enabled ones in the chosen order, then the disabled ones in catalog order.</summary>
    public static IReadOnlyList<string> Display(IReadOnlyList<string> on, IEnumerable<string> catalog) =>
        [.. on, .. catalog.Where(x => !on.Contains(x))];
}
