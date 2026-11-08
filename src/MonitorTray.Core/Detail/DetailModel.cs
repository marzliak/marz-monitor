using System.Globalization;
using System.Text;
using MonitorTray.Core.Format;
using MonitorTray.Core.History;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Detail;

public sealed record Filters(string Query, bool ShowZero, bool ShowLimits)
{
    public static Filters Default { get; } = new("", false, false);
}

public sealed record DeviceListItem(Device Device, string Headline, float?[] Spark);

public sealed record CardOption(string Key, string Label);

public sealed record CardModel(string PickKey, string Selected, IReadOnlyList<CardOption> Options, string Label, string SensorName, Formatted Value, double? Raw, string Unit, bool IsGauge, double Fraction, bool Alt, float?[] History, (double, double)? Fixed, bool ZeroBase, string Lo, string Hi);

public sealed record CellModel(double Fraction, bool Emphasis, string Text, string Tooltip, double? Raw, string Unit);

public enum TileVisual { Bar, Spark, Text }

public sealed record TileModel(SensorReading Sensor, string Name, string Tooltip, Formatted Value, string Tag, TileVisual Visual, double Fraction, bool Emphasis, float?[] Spark, bool ZeroBase, string Range);

public sealed record SectionModel(string KindRaw, string Label, string Meta, bool IsGrid, IReadOnlyList<CellModel> Cells, IReadOnlyList<TileModel> Tiles);

public sealed record DevicePanel(Device Device, string Meta, IReadOnlyList<CardModel> Cards, IReadOnlyList<SectionModel> Sections, int VisibleCount);

public sealed record DetailView(IReadOnlyList<DeviceListItem> Devices, int DeviceCount, int Visible, int HiddenZero, int HiddenLimits, DevicePanel? Panel);

/// <summary>Reproduz detail()/devices() do protótipo (docs/04 §C, docs/06).</summary>
public static class DetailModel
{
    public const double Emphasis = 0.85;

    public static string Fold(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static bool Hidden(SensorReading s, Filters f, ref int zero, ref int limits)
    {
        if ((s.Role is SensorRole.Limit or SensorRole.Metadata) && !f.ShowLimits) { limits++; return true; }
        if (s.AllZero && !f.ShowZero) { zero++; return true; }
        return false;
    }

    private static bool Matches(SensorReading s, Device d, string q) =>
        q.Length == 0 || Fold($"{s.Name} {d.DisplayName} {d.RawName} {s.KindRaw} {Units.TypeInfo(s.KindRaw).Label}").Contains(q, StringComparison.Ordinal);

    public static IReadOnlyList<SensorReading> Visible(Device d, Filters f, ref int zero, ref int limits)
    {
        var q = Fold(f.Query.Trim());
        var list = new List<SensorReading>();
        foreach (var s in d.Sensors)
        {
            if (Hidden(s, f, ref zero, ref limits)) continue;
            if (!Matches(s, d, q)) continue;
            list.Add(s);
        }
        return list;
    }

    public static SensorReading? HotSensor(Device d) =>
        Primary.Pick(d.Sensors.Where(s => s.KindRaw == "Temperature" && s.Role == SensorRole.Reading && s.Value != null && !s.AllZero).ToList(), s => s.Name, s => s.Value);

    public static DetailView Build(IReadOnlyList<Device> devices, SensorTracker tracker, Filters f, string? selectedId, IReadOnlyDictionary<string, string>? picks = null)
    {
        int zero = 0, limits = 0, total = 0;
        var q = f.Query.Trim();
        var items = new List<(DeviceListItem Item, IReadOnlyList<SensorReading> Visible)>();
        foreach (var d in devices)
        {
            var vis = Visible(d, f, ref zero, ref limits);
            total += vis.Count;
            if (q.Length > 0 && vis.Count == 0) continue;
            var hot = HotSensor(d);
            var headline = hot?.Value is float t ? Math.Round(t, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " °C" : vis.Count.ToString(CultureInfo.InvariantCulture);
            var spark = hot != null ? tracker.Get(hot.Id)?.History.ToArray(41) ?? [] : [];
            items.Add((new DeviceListItem(d, headline, spark), vis));
        }
        var sel = items.FirstOrDefault(i => i.Item.Device.Id == selectedId);
        if (sel.Item == null && items.Count > 0) sel = items[0];
        DevicePanel? panel = sel.Item == null ? null : Panel(sel.Item.Device, sel.Visible, tracker, picks);
        return new DetailView(items.Select(i => i.Item).ToList(), devices.Count, total, zero, limits, panel);
    }

    private static string Meta(Device d, int count)
    {
        var parts = new List<string> { $"{count} sensores" };
        if (d.Family == "Memory" && d.Modules.Count > 0)
            parts.Add($"{d.Modules.Count} módulos · " + string.Join(", ", d.Modules.Select(m => $"módulo {m.Index} no slot {m.Slot}")));
        else parts.Add(d.SourceIds.Count > 1 ? d.SourceIds.Count + " fontes" : d.Id);
        return string.Join(" · ", parts);
    }

    private static bool Graphable(SensorReading s) => Roles.Graphable(s.Role, s.Quality, s.Unit);

    private static string Fmt(double? v, string unit) => Formatter.Format(v, unit).ToString();

    public const string PickAvg = "avg", PickMax = "max";

    /// <summary>Média/máximo por tick entre vários históricos alinhados pela direita (null onde todos são null).</summary>
    public static float?[] Aggregate(IReadOnlyList<float?[]> series, bool max)
    {
        var n = series.Count == 0 ? 0 : series.Max(s => s.Length);
        var r = new float?[n];
        for (int i = 0; i < n; i++)
        {
            double acc = max ? double.MinValue : 0; int c = 0;
            foreach (var s in series)
            {
                int j = s.Length - n + i;
                if (j < 0 || s[j] is not float v) continue;
                if (max) acc = Math.Max(acc, v); else acc += v;
                c++;
            }
            r[i] = c == 0 ? null : (float)(max ? acc : acc / c);
        }
        return r;
    }

    private static readonly string[] TrafficOrder = ["Total", "Rede local", "Internet"];
    private static (string Src, string Name) SplitSrc(string n) { var i = n.IndexOf(" · ", StringComparison.Ordinal); return i < 0 ? ("", n) : (n[..i], n[(i + 3)..]); }

    /// <summary>Rede numa aba só: cartões e seções por tipo de tráfego (Total, Rede local, Internet) e depois por placa.</summary>
    private static DevicePanel NetworkPanel(Device d, IReadOnlyList<SensorReading> visible, SensorTracker tracker, IReadOnlyDictionary<string, string>? picks)
    {
        var cards = new List<CardModel>(); var sections = new List<SectionModel>();
        var groups = visible.GroupBy(s => SplitSrc(s.Name).Src)
            .OrderBy(g => { var i = Array.IndexOf(TrafficOrder, g.Key); return i < 0 ? 99 : i; }).ThenBy(g => g.Key, StringComparer.CurrentCulture);
        foreach (var g in groups)
        {
            var list = g.Select(s => s with { Name = SplitSrc(s.Name).Name }).ToList();
            if (TrafficOrder.Contains(g.Key))
            {
                var rates = list.Where(x => x.Unit == "B/s" && Graphable(x)).ToList();
                if (rates.Count > 0)
                {
                    var pickKey = d.Id + "|src:" + g.Key;
                    var options = rates.Select(x => new CardOption(x.Id, x.Name)).ToList();
                    var def = rates.FirstOrDefault(x => x.Name.StartsWith("Download", StringComparison.Ordinal)) ?? rates[0];
                    var pick = picks != null && picks.TryGetValue(pickKey, out var pv) && options.Any(o => o.Key == pv) ? pv : def.Id;
                    var sel = rates.First(x => x.Id == pick);
                    var hist = tracker.Get(sel.Id)?.History.ToArray() ?? [];
                    var hv = hist.Where(v => v != null).Select(v => (double)v!.Value).ToList();
                    string lo = "", hi = "";
                    if (hv.Count > 0) { var a = Fmt(hv.Min(), "B/s"); var b = Fmt(hv.Max(), "B/s"); if (a != b) { lo = "mín " + a; hi = "máx " + b; } else lo = "estável " + a; }
                    var gmax = Math.Max(rates.Max(x => (double)(x.Value ?? 0)), 1e-9);
                    double fr = sel.Value is float vv ? Math.Clamp(vv / (gmax * 1.1), 0, 1) : double.NaN;
                    cards.Add(new CardModel(pickKey, pick, options, g.Key, sel.Name, Formatter.Format(sel.Value, "B/s"), sel.Value, "B/s", false, fr, cards.Count % 2 == 1, hist, null, true, lo, hi));
                }
            }
            var sub = Panel(d with { Id = d.Id + "/" + g.Key }, list, tracker);
            var tiles = sub.Sections.SelectMany(s => s.Tiles).ToList();
            if (tiles.Count > 0) sections.Add(new SectionModel("src:" + g.Key, g.Key, tiles.Count.ToString(CultureInfo.InvariantCulture), false, [], tiles));
        }
        return new DevicePanel(d, Meta(d, visible.Count), cards, sections, visible.Count);
    }

    public static DevicePanel Panel(Device d, IReadOnlyList<SensorReading> visible, SensorTracker tracker, IReadOnlyDictionary<string, string>? picks = null)
    {
        if (d.Id == "family:Network") return NetworkPanel(d, visible, tracker, picks);
        var cards = new List<CardModel>();
        var sections = new List<SectionModel>();
        var groups = visible.GroupBy(s => s.KindRaw).OrderBy(g => Units.TypeInfo(g.Key).Order).ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var list = g.ToList();
            var label = Units.TypeInfo(g.Key).Label;
            var live = list.Where(Graphable).ToList();
            var gmax = Math.Max(live.Select(x => (double)(x.Value ?? 0)).DefaultIfEmpty(0).Max(), 1e-9);
            double Frac(SensorReading x)
            {
                if (x.Value is not float v) return double.NaN; // UI mantém a barra anterior; texto mostra o traço
                var (lo, hi) = Units.IsFixedRange(x.Unit) ? (0d, 100d) : (0d, gmax * 1.1);
                return Math.Clamp((v - lo) / (hi - lo), 0, 1);
            }
            if (live.Count > 0)
            {
                var p = Primary.Pick(live, s => s.Name, s => s.Value)!;
                var same = live.Where(x => x.Unit == p.Unit).ToList();
                var pickKey = d.Id + "|" + g.Key;
                var options = new List<CardOption>();
                if (same.Count >= 2) { options.Add(new(PickAvg, $"Média ({same.Count})")); options.Add(new(PickMax, $"Máximo ({same.Count})")); }
                options.AddRange(same.Select(x => new CardOption(x.Id, x.Name)));
                var pick = picks != null && picks.TryGetValue(pickKey, out var pv) && options.Any(o => o.Key == pv) ? pv : p.Id;
                var fixedR = Units.IsFixedRange(p.Unit);
                float?[] hist; double? value; string name;
                if (pick is PickAvg or PickMax)
                {
                    bool mx = pick == PickMax;
                    hist = Aggregate(same.Select(x => tracker.Get(x.Id)?.History.ToArray() ?? []).ToList(), mx);
                    var vals = same.Where(x => x.Value != null).Select(x => (double)x.Value!.Value).ToList();
                    value = vals.Count == 0 ? null : mx ? vals.Max() : vals.Average();
                    name = options.First(o => o.Key == pick).Label;
                }
                else
                {
                    var sel = same.First(x => x.Id == pick);
                    hist = tracker.Get(sel.Id)?.History.ToArray() ?? [];
                    value = sel.Value; name = sel.Name;
                }
                var hv = hist.Where(v => v != null).Select(v => (double)v!.Value).ToList();
                bool varies = hv.Count > 0 && hv.Min() != hv.Max();
                if (fixedR || varies || pick != p.Id)
                {
                    string lo = "", hi = "";
                    if (hv.Count > 0)
                    {
                        var a2 = Fmt(hv.Min(), p.Unit); var b2 = Fmt(hv.Max(), p.Unit);
                        if (a2 != b2) { lo = "mín " + a2; hi = "máx " + b2; } else lo = "estável " + a2;
                    }
                    double fr = value is double vv ? Math.Clamp(fixedR ? vv / 100 : vv / (gmax * 1.1), 0, 1) : double.NaN;
                    cards.Add(new CardModel(pickKey, pick, options, label, name, Formatter.Format(value, p.Unit), value, p.Unit, fixedR, fr, cards.Count % 2 == 1, hist,
                        fixedR ? (0, 100) : null, p.Unit == "B/s", lo, hi));
                }
            }
            bool isGrid = list.Count >= 9 && list.All(Graphable) && list.All(x => x.Unit == list[0].Unit);
            if (isGrid)
            {
                var u = list[0].Unit;
                var vs = list.Where(x => x.Value != null).Select(x => (double)x.Value!.Value).ToList();
                var meta = vs.Count == 0 ? list.Count.ToString(CultureInfo.InvariantCulture)
                    : $"{list.Count} · mín {Fmt(vs.Min(), u)} · méd {Fmt(vs.Average(), u)} · máx {Fmt(vs.Max(), u)}";
                var cells = list.Select(x => { var fr = Frac(x); return new CellModel(fr, fr >= Emphasis, Formatter.Short(x.Value, u), x.Name + " · " + Fmt(x.Value, u), x.Value, u); }).ToList();
                sections.Add(new SectionModel(g.Key, label, meta, true, cells, []));
                continue;
            }
            var tiles = list.Select(x =>
            {
                var gr = Graphable(x);
                var bar = gr && Units.BarUnits.Contains(x.Unit);
                var spark = gr && !bar && x.Min != x.Max && !x.AllZero;
                var range = "";
                if (x.Min is float mn && x.Max is float mx && mn != mx && !x.AllZero)
                {
                    var a = Formatter.Format(mn, x.Unit); var b = Formatter.Format(mx, x.Unit);
                    if (a.ToString() != b.ToString()) range = a.Unit == b.Unit ? $"{a.Value}–{b.Value} {b.Unit}".TrimEnd() : $"{a}–{b}";
                }
                var fr = Frac(x);
                var sp = spark ? tracker.Get(x.Id)?.History.ToArray(61) ?? [] : [];
                return new TileModel(x, x.Name, x.Name + " · " + x.Id, Formatter.Format(x.Value, x.Unit), x.Tag,
                    bar ? TileVisual.Bar : spark ? TileVisual.Spark : TileVisual.Text, fr, fr >= Emphasis, sp, x.Unit == "B/s", range);
            }).ToList();
            sections.Add(new SectionModel(g.Key, label, list.Count.ToString(CultureInfo.InvariantCulture), false, [], tiles));
        }
        return new DevicePanel(d, Meta(d, visible.Count), cards, sections, visible.Count);
    }

    public static string Footer(DetailView v)
    {
        var parts = new List<string> { $"{v.Visible} sensores visíveis" };
        if (v.HiddenZero > 0) parts.Add($"{v.HiddenZero} zerados ocultos");
        if (v.HiddenLimits > 0) parts.Add($"{v.HiddenLimits} limites/SPD ocultos");
        return string.Join(" · ", parts);
    }
}
