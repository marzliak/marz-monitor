using System.Text.RegularExpressions;
using MonitorTray.Core.History;
using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

/// <summary>Turns raw readings into devices: category, name, consolidation, role, quality (docs/05 §2–7).</summary>
public static class NetTrafficIds
{
    public const string Total = "/net-traffic/total", Local = "/net-traffic/local", Internet = "/net-traffic/internet";
}

public static partial class DeviceBuilder
{
    public static readonly IReadOnlyList<string> Consolidate = ["Memory", "Network"];
    private static readonly string[] CategoryOrder = ["CPU", "GPU", "Memory", "Motherboard", "Storage", "Network", "PSU"];

    [GeneratedRegex(@"\(#(\d+)\)")] private static partial Regex SlotRe();

    public static string Category(string family) => family switch
    {
        "Cpu" => "CPU",
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => "GPU",
        "Memory" => "Memory",
        "Motherboard" or "SuperIO" or "EmbeddedController" => "Motherboard",
        "Storage" => "Storage",
        "Network" => "Network",
        "Psu" => "PSU",
        "Cooler" or "Controller" => "Controller",
        "Battery" => "Battery",
        "PowerMonitor" => "Power",
        _ => family,
    };

    private static string FamilyGroup(string family) => family switch
    {
        "SuperIO" or "EmbeddedController" => "Motherboard",
        _ => family,
    };

    private static string FallbackStem(string family, string category) => family switch
    {
        "Network" => "Network adapter",
        "Storage" => "Disk",
        "Memory" => "Memory",
        _ => category,
    };

    public static int CategoryRank(string category)
    {
        var i = Array.IndexOf(CategoryOrder, category);
        return i < 0 ? 99 : i;
    }

    private sealed class Acc
    {
        public required string Id, Family, RawName;
        public bool Merged;
        public readonly List<SensorReading> Sensors = [];
        public readonly HashSet<string> Sources = new(StringComparer.Ordinal);
        public List<MemoryModule> Modules = [];
        public string Name = "", Cat = "";
    }

    public static IReadOnlyList<Device> Build(IReadOnlyList<RawSensor> raw, SensorTracker tracker, Func<string, bool> isNew)
    {
        // Memory module slots: hardware with "(#N)"; numbered by ascending N.
        var slotByDevice = new Dictionary<string, (int Index, int Slot)>(StringComparer.Ordinal);
        var modules = raw.Where(r => r.Family == "Memory").Select(r => (r.DeviceId, r.DeviceName)).Distinct()
            .Select(d => (d.DeviceId, M: SlotRe().Match(d.DeviceName))).Where(d => d.M.Success)
            .Select(d => (d.DeviceId, Slot: int.Parse(d.M.Groups[1].Value))).OrderBy(d => d.Slot).ThenBy(d => d.DeviceId, StringComparer.Ordinal).ToList();
        for (int i = 0; i < modules.Count; i++) slotByDevice[modules[i].DeviceId] = (i + 1, modules[i].Slot);

        // Consolidated network: each adapter/traffic type becomes a "source" (sensor prefix and panel section).
        var netNames = new Dictionary<string, string>(StringComparer.Ordinal);
        int generic = 0;
        foreach (var nd in raw.Where(r => r.Family == "Network").Select(r => (r.DeviceId, r.DeviceName)).Distinct().OrderBy(x => x.DeviceId, StringComparer.Ordinal))
        {
            string n = nd.DeviceId switch
            {
                NetTrafficIds.Total => "Total", NetTrafficIds.Local => "Local network", NetTrafficIds.Internet => "Internet",
                _ => nd.DeviceName.Replace(nd.DeviceId, "", StringComparison.Ordinal).Trim(),
            };
            if (n.Length == 0 || n == "Network") n = "Network adapter " + (++generic);
            netNames[nd.DeviceId] = n;
        }

        var map = new Dictionary<string, Acc>(StringComparer.Ordinal);
        foreach (var r in raw)
        {
            var group = FamilyGroup(r.Family);
            bool merge = Consolidate.Contains(group);
            var key = merge ? "family:" + group : r.DeviceId;
            if (!map.TryGetValue(key, out var acc))
            {
                acc = new Acc { Id = key, Family = merge ? group : r.Family, RawName = merge ? Category(group) : r.DeviceName, Merged = merge };
                map[key] = acc;
            }
            acc.Sources.Add(r.DeviceId);
            var name = merge ? (netNames.TryGetValue(r.DeviceId, out var nn) ? nn : SourceName(r.DeviceId, r.DeviceName, slotByDevice)) + " · " + r.Name : r.Name;
            var e = tracker.Get(r.Id);
            var quality = e?.Quality ?? (r.Value is null ? SensorQuality.Unavailable : SensorQuality.Ok);
            var role = Roles.Classify(group, r.KindRaw, r.Name);
            var allZero = e is { SeenValue: true, AllZero: true };
            acc.Sensors.Add(new SensorReading(r.Id, r.DeviceId, name, Units.Kind(r.KindRaw), r.KindRaw, r.Unit,
                quality == SensorQuality.Unavailable ? null : r.Value, e?.Min, e?.Max, role, quality, allZero, Roles.Tag(role, quality, r.Name)));
        }
        foreach (var acc in map.Values.Where(a => a.Merged && a.Family == "Memory"))
            acc.Modules = acc.Sources.Where(slotByDevice.ContainsKey).Select(s => new MemoryModule(slotByDevice[s].Index, slotByDevice[s].Slot)).OrderBy(m => m.Index).ToList();

        // Names: strip the embedded ID; empty/same as the family → "Disk N" etc., numbered in ID order (consolidated ones keep the category name).
        var all = map.Values.OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        var famIdx = new Dictionary<string, int>();
        foreach (var a in all)
        {
            a.Cat = Category(FamilyGroup(a.Family));
            var n = a.RawName.Replace(a.Id, "", StringComparison.Ordinal).Trim();
            if (!a.Merged && (n.Length == 0 || n == a.Family || n == FamilyGroup(a.Family)))
            {
                famIdx[a.Family] = famIdx.GetValueOrDefault(a.Family) + 1;
                n = FallbackStem(a.Family, a.Cat) + " " + famIdx[a.Family];
            }
            a.Name = n;
        }
        foreach (var dup in all.GroupBy(a => a.Name).Where(g => g.Count() > 1).ToList())
            foreach (var a in dup) a.Name += " · " + a.Id.Split('/').Last();

        return all.OrderBy(a => CategoryRank(a.Cat)).ThenBy(a => a.Cat, StringComparer.CurrentCulture).ThenBy(a => a.Name, StringComparer.CurrentCulture)
            .Select(a => new Device(a.Id, a.Family, a.RawName, a.Name, a.Cat, isNew(a.Id), a.Sensors, a.Sources, a.Modules))
            .ToList();
    }

    private static string SourceName(string deviceId, string deviceName, Dictionary<string, (int Index, int Slot)> slots)
    {
        if (slots.TryGetValue(deviceId, out var s)) return "Module " + s.Index;
        var n = Regex.Replace(deviceName, @"\s*memory\s*", "", RegexOptions.IgnoreCase).Trim();
        return n switch { "Total" => "Physical", "Virtual" => "Virtual", "" => deviceName, _ => n };
    }
}
