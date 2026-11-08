using System.Text.Json;
using System.Text.Json.Nodes;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Model;

public sealed record DetailsWindowState(double? X, double? Y, double? W, double? H, bool Maximized);

/// <summary>Persisted settings (spec/settings.schema.json). Invalid values → default.</summary>
public sealed record Settings
{
    public static readonly string[] Themes = ["light", "dark", "system"];
    public static readonly string[] Palettes = ["electric", "neon", "lime", "ember", "violet", "steel"];
    public static readonly int[] RefreshOptions = [500, 1000, 2000];
    public static readonly int[] HistoryOptions = [60, 300, 600, 1800, 3600, 7200]; // seconds; max. 2 h
    public static readonly string[] Scales = ["independent", "shared"];
    public static readonly string[] NetUnits = ["bytes", "bits"];

    public int Version { get; init; } = 1;
    public string Theme { get; init; } = "dark";
    public string Palette { get; init; } = "electric";
    public int Opacity { get; init; } = 90;
    public int RefreshMs { get; init; } = 1000;
    public int History { get; init; } = 60;
    public string Scale { get; init; } = "independent";
    /// <summary>Network in bytes/s (MB/s, base 1024) or bits/s (Mbps, base 1000).</summary>
    public string NetUnit { get; init; } = "bytes";
    /// <summary>Board modes (Armoury Crate), in button order.</summary>
    public static readonly string[] AcModeIds = ["curves", "silent", "standard", "turbo", "full"];
    /// <summary>Board modes shown in the popup (default: all) and the fans they apply to.</summary>
    public IReadOnlyList<string> AcModes { get; init; } = AcModeIds;
    public IReadOnlyList<string> AcFans { get; init; } = ["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"];
    /// <summary>Names the user gave to the board fans (Armoury Crate connector → name). Missing = the header name (CHA_FAN1...).</summary>
    public IReadOnlyDictionary<string, string> AcFanNames { get; init; } = new Dictionary<string, string>();
    /// <summary>Names the user gave to the cooler parts (pump, radiator, embedded). Missing = PUMP/RADIATOR/EMB.</summary>
    public IReadOnlyDictionary<string, string> CoolerNames { get; init; } = new Dictionary<string, string>();
    /// <summary>Board fan name shown everywhere: the user's name or the header printed on the board.</summary>
    public string BoardFanName(string acName) => Fans.FanNames.Name(AcFanNames, acName, Fans.FanNames.BoardHeader(acName));
    /// <summary>Tooltip form: "Exhaust · CHA_FAN1" when renamed.</summary>
    public string BoardFanTip(string acName) => Fans.FanNames.Tip(AcFanNames, acName, Fans.FanNames.BoardHeader(acName));
    public string CoolerName(string part) => Fans.FanNames.Name(CoolerNames, part, Fans.FanNames.CoolerHeader(part));
    public string CoolerTip(string part) => Fans.FanNames.Tip(CoolerNames, part, Fans.FanNames.CoolerHeader(part));
    /// <summary>Game mode: what goes to 100% (gpu, board, pump, radiator, embedded).</summary>
    public static readonly string[] GameTargetIds = ["gpu", "board", "pump", "radiator", "embedded"];
    public IReadOnlyList<string> GameTargets { get; init; } = GameTargetIds;
    /// <summary>Board fans in game mode; null = all the ones Armoury Crate exposes.</summary>
    public IReadOnlyList<string>? GameBoardFans { get; init; }
    public static readonly int[] GamePercents = [70, 85, 100];
    public int GameGpuPercent { get; init; } = 100;
    public int GameCoolerPercent { get; init; } = 100;
    /// <summary>Popup temperature cards, in order (TempCards ids; at most 8).</summary>
    public IReadOnlyList<string> PopupTemps { get; init; } = TempCards.DefaultIds;
    /// <summary>Board fans (Armoury Crate name) shown as popup cards. Default: CPU and chassis (no pumps, no CPU_OPT).</summary>
    public static readonly string[] DefaultBoardCards = ["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"];
    public IReadOnlyList<string> PopupBoardFans { get; init; } = DefaultBoardCards;
    /// <summary>Water cooler parts shown as popup cards (pump, radiator, embedded).</summary>
    public static readonly string[] CoolerCardIds = ["pump", "radiator", "embedded"];
    public IReadOnlyList<string> PopupCoolerFans { get; init; } = CoolerCardIds;
    /// <summary>Experimental (off by default): clicking a board fan card turns it off/on again (protection curve).</summary>
    public bool BoardFanOff { get; init; } = false;
    /// <summary>Tray icon shows the highest temperature among the enabled cards (PopupTemps).</summary>
    public bool TrayTemp { get; init; } = false;
    /// <summary>Tray icon changes color when a card is near its limit (≥ limit − 5 °C).</summary>
    public bool TrayAlert { get; init; } = false;
    /// <summary>Size of the number in the tray icon: xs (default, smallest legible), s, m or l (fills the whole square).</summary>
    public static readonly string[] TrayFontSizes = ["xs", "s", "m", "l"];
    public string TrayFontSize { get; init; } = "xs";
    /// <summary>Maximum digit height in the icon, as a fraction of the side (16/24/32 px).</summary>
    public static double TrayFontScale(string size) => size switch { "l" => 1.0, "m" => 0.85, "s" => 0.72, _ => 0.58 };
    /// <summary>Tray icon temperatures, in rotation order. Empty = the hottest among the popup cards.</summary>
    public IReadOnlyList<string> TrayTemps { get; init; } = [];
    /// <summary>Seconds between one temperature and the next in the icon (when more than one is chosen).</summary>
    public static readonly int[] TrayRotateSeconds = [2, 3, 5, 10];
    public int TrayRotateSec { get; init; } = 3;
    /// <summary>Executables that turn game mode on by themselves while running ("game.exe"; empty = off).</summary>
    public IReadOnlyList<string> GameExes { get; init; } = [];
    public bool Autostart { get; init; } = true;
    public bool Autohide { get; init; } = false;
    /// <summary>Start with Windows in the tray only, without opening the popup.</summary>
    public bool StartHidden { get; init; } = true;
    /// <summary>Close (X) hides to the tray; off = close quits the app (fans go back to automatic).</summary>
    public bool CloseToTray { get; init; } = true; // user: popup stays open when clicking another window (deviation from the kit)
    public DetailsWindowState? DetailsWindow { get; init; }
    /// <summary>Sensor choice for each Details card: key "deviceId|kind" → "avg", "max" or sensor id.</summary>
    public IReadOnlyDictionary<string, string> CardPicks { get; init; } = new Dictionary<string, string>();

    public bool Equals(Settings? o) => o is not null && Version == o.Version && Theme == o.Theme && Palette == o.Palette && Opacity == o.Opacity
        && RefreshMs == o.RefreshMs && History == o.History && Scale == o.Scale && NetUnit == o.NetUnit && AcModes.SequenceEqual(o.AcModes) && AcFans.SequenceEqual(o.AcFans)
        && GameTargets.SequenceEqual(o.GameTargets) && (GameBoardFans is null) == (o.GameBoardFans is null) && (GameBoardFans ?? []).SequenceEqual(o.GameBoardFans ?? [])
        && GameGpuPercent == o.GameGpuPercent && GameCoolerPercent == o.GameCoolerPercent
        && PopupTemps.SequenceEqual(o.PopupTemps) && PopupBoardFans.SequenceEqual(o.PopupBoardFans) && PopupCoolerFans.SequenceEqual(o.PopupCoolerFans) && BoardFanOff == o.BoardFanOff
        && TrayTemp == o.TrayTemp && TrayAlert == o.TrayAlert && TrayFontSize == o.TrayFontSize && TrayTemps.SequenceEqual(o.TrayTemps) && TrayRotateSec == o.TrayRotateSec && GameExes.SequenceEqual(o.GameExes)
        && Autostart == o.Autostart && Autohide == o.Autohide
        && StartHidden == o.StartHidden && CloseToTray == o.CloseToTray && Equals(DetailsWindow, o.DetailsWindow)
        && SameMap(CardPicks, o.CardPicks) && SameMap(AcFanNames, o.AcFanNames) && SameMap(CoolerNames, o.CoolerNames);
    private static bool SameMap(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
    public override int GetHashCode() => HashCode.Combine(Theme, Palette, Opacity, RefreshMs, History, Autohide, CardPicks.Count);

    public static Settings Default { get; } = new();

    /// <summary>Ids written by versions up to 0.1.33 (Portuguese) → current id. Read only; ToJson writes the new ids.</summary>
    private static readonly Dictionary<string, string> LegacyIds = new(StringComparer.Ordinal)
    {
        ["claro"] = "light", ["escuro"] = "dark", ["sistema"] = "system",
        ["eletrico"] = "electric", ["lima"] = "lime", ["brasa"] = "ember", ["violeta"] = "violet", ["aco"] = "steel",
        ["independente"] = "independent", ["compartilhada"] = "shared",
    };
    private static readonly Dictionary<string, string> LegacyTrayFontSizes = new(StringComparer.Ordinal) { ["pp"] = "xs", ["p"] = "s", ["g"] = "l" };
    /// <summary>
    /// Nicknames that used to be built-in defaults (up to 0.1.33, plus their English forms), per connector. Saved exactly like
    /// this they are dropped so the header name shows; any other value is the user's own name and is kept.
    /// </summary>
    private static readonly Dictionary<string, string[]> LegacyDefaultFanNames = new(StringComparer.Ordinal)
    {
        ["Chassis Fan 1"] = ["Exaustor", "Exhaust"], ["Chassis Fan 2"] = ["Front top"],
        ["Chassis Fan 3"] = ["Front down", "Front bottom"], ["Chassis Fan 4"] = ["Rear"],
    };

    /// <summary>Board mode id: "curvas" (up to 0.1.33) → "curves".</summary>
    public static string? MigrateAcMode(string? id) => id == "curvas" ? "curves" : id;
    /// <summary>True when <paramref name="name"/> is the old built-in default for that connector (not a name the user chose).</summary>
    public static bool IsLegacyDefaultFanName(string acName, string name) =>
        LegacyDefaultFanNames.TryGetValue(acName, out var l) && l.Contains(name, StringComparer.Ordinal);

    /// <summary>Name dictionary from JSON: names trimmed and cut to 16, empty ones and the keys not accepted dropped.</summary>
    private static Dictionary<string, string> Names(JsonNode? n, Func<string, string, bool> keep)
    {
        var d = new Dictionary<string, string>();
        if (n is not JsonObject o) return d;
        foreach (var kv in o)
        {
            try { var v = Fans.FanNames.Clean(kv.Value?.GetValue<string>()); if (v.Length > 0 && kv.Key.Length is > 0 and < 64 && keep(kv.Key, v)) d[kv.Key] = v; }
            catch (Exception) { }
        }
        return d;
    }

    public static Settings Parse(string json)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; } catch (JsonException) { return Default; }
        if (o == null) return Default;
        try { if (o["version"]?.GetValue<int>() != 1) return Default; } catch (Exception) { return Default; }
        var d = Default;
        T Get<T>(string key, T fallback, Func<T, bool> valid)
        {
            try { var n = o[key]; if (n == null) return fallback; var v = n.GetValue<T>(); return valid(v) ? v : fallback; }
            catch (Exception) { return fallback; }
        }
        string Id(string key, string fallback, string[] valid, Dictionary<string, string> legacy)
            => Get(key, fallback, v => valid.Contains(v) || (legacy.TryGetValue(v, out var n) && valid.Contains(n))) is var v && legacy.TryGetValue(v, out var m) ? m : v;
        static IReadOnlyList<string>? Strings(JsonNode? n)
        {
            if (n is not JsonArray a) return null;
            try { return a.Select(x => x!.GetValue<string>()).Where(x => x.Length is > 0 and < 64).Distinct().Take(16).ToList(); } catch (Exception) { return null; }
        }
        DetailsWindowState? win = null;
        if (o["detailsWindow"] is JsonObject w)
        {
            double? Num(string k, double min) { try { var v = w[k]?.GetValue<double>(); return v is double x && double.IsFinite(x) && x >= min ? x : null; } catch (Exception) { return null; } }
            bool max = false; try { max = w["maximized"]?.GetValue<bool>() ?? false; } catch (Exception) { }
            win = new DetailsWindowState(Num("x", double.MinValue), Num("y", double.MinValue), Num("w", 720), Num("h", 440), max);
        }
        var picks = new Dictionary<string, string>();
        if (o["cardPicks"] is JsonObject cp)
            foreach (var kv in cp)
                try { if (kv.Value?.GetValue<string>() is string v && v.Length is > 0 and < 300 && kv.Key.Length < 300) picks[kv.Key] = v; } catch (Exception) { }
        return new Settings
        {
            CardPicks = picks,
            AcModes = Strings(o["acModes"])?.Select(m => MigrateAcMode(m)!).Where(AcModeIds.Contains).Distinct().ToList() ?? d.AcModes,
            AcFans = Strings(o["acFans"]) ?? d.AcFans,
            GameTargets = Strings(o["gameTargets"])?.Where(GameTargetIds.Contains).ToList() ?? d.GameTargets,
            GameBoardFans = Strings(o["gameBoardFans"]),
            GameGpuPercent = Get("gameGpuPercent", d.GameGpuPercent, GamePercents.Contains),
            GameCoolerPercent = Get("gameCoolerPercent", d.GameCoolerPercent, GamePercents.Contains),
            PopupTemps = Strings(o["popupTemps"]) is { } pt ? PickList.Clean(pt, TempCards.Ids.ToList(), TempCards.MaxCards) : d.PopupTemps,
            PopupBoardFans = Strings(o["popupBoardFans"]) ?? d.PopupBoardFans,
            PopupCoolerFans = Strings(o["popupCoolerFans"]) is { } pc ? PickList.Clean(pc, CoolerCardIds) : d.PopupCoolerFans,
            BoardFanOff = Get("boardFanOff", d.BoardFanOff, _ => true),
            TrayTemp = Get("trayTemp", d.TrayTemp, _ => true),
            TrayAlert = Get("trayAlert", d.TrayAlert, _ => true),
            TrayFontSize = Id("trayFontSize", d.TrayFontSize, TrayFontSizes, LegacyTrayFontSizes),
            TrayTemps = Strings(o["trayTemps"]) is { } tt ? PickList.Clean(tt, TempCards.Ids.ToList()) : d.TrayTemps,
            TrayRotateSec = Get("trayRotateSec", d.TrayRotateSec, TrayRotateSeconds.Contains),
            GameExes = Strings(o["gameExes"])?.Select(Fans.AutoGame.Clean).OfType<string>().Distinct().ToList() ?? d.GameExes,
            AcFanNames = Names(o["acFanNames"], (k, v) => !IsLegacyDefaultFanName(k, v)),
            CoolerNames = Names(o["coolerNames"], (k, _) => Fans.FanNames.CoolerParts.Contains(k)),
            Theme = Id("theme", d.Theme, Themes, LegacyIds),
            Palette = Id("palette", d.Palette, Palettes, LegacyIds),
            Opacity = Get("opacity", d.Opacity, v => v is >= 40 and <= 100 && v % 5 == 0),
            RefreshMs = Get("refreshMs", d.RefreshMs, RefreshOptions.Contains),
            History = Get("history", d.History, HistoryOptions.Contains),
            Scale = Id("scale", d.Scale, Scales, LegacyIds),
            NetUnit = Get("netUnit", d.NetUnit, NetUnits.Contains),
            Autostart = Get("autostart", d.Autostart, _ => true),
            Autohide = Get("autohide", d.Autohide, _ => true),
            StartHidden = Get("startHidden", d.StartHidden, _ => true),
            CloseToTray = Get("closeToTray", d.CloseToTray, _ => true),
            DetailsWindow = win,
        };
    }

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["version"] = 1, ["theme"] = Theme, ["palette"] = Palette, ["opacity"] = Opacity, ["refreshMs"] = RefreshMs,
            ["history"] = History, ["scale"] = Scale, ["netUnit"] = NetUnit, ["autostart"] = Autostart, ["autohide"] = Autohide,
            ["startHidden"] = StartHidden, ["closeToTray"] = CloseToTray,
            ["acModes"] = new JsonArray(AcModes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["acFans"] = new JsonArray(AcFans.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["gameTargets"] = new JsonArray(GameTargets.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["gameGpuPercent"] = GameGpuPercent, ["gameCoolerPercent"] = GameCoolerPercent,
            ["popupTemps"] = new JsonArray(PopupTemps.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["popupBoardFans"] = new JsonArray(PopupBoardFans.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["popupCoolerFans"] = new JsonArray(PopupCoolerFans.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["boardFanOff"] = BoardFanOff,
            ["trayTemp"] = TrayTemp, ["trayAlert"] = TrayAlert, ["trayFontSize"] = TrayFontSize,
            ["trayTemps"] = new JsonArray(TrayTemps.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()), ["trayRotateSec"] = TrayRotateSec,
            ["gameExes"] = new JsonArray(GameExes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
            ["acFanNames"] = new JsonObject(AcFanNames.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
            ["coolerNames"] = new JsonObject(CoolerNames.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonValue.Create(kv.Value)))),
        };
        if (GameBoardFans != null) o["gameBoardFans"] = new JsonArray(GameBoardFans.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        if (CardPicks.Count > 0)
        {
            var cp = new JsonObject();
            foreach (var kv in CardPicks.OrderBy(k => k.Key, StringComparer.Ordinal)) cp[kv.Key] = kv.Value;
            o["cardPicks"] = cp;
        }
        if (DetailsWindow is { } w)
        {
            var wo = new JsonObject { ["maximized"] = w.Maximized };
            if (w.X is double x) wo["x"] = x;
            if (w.Y is double y) wo["y"] = y;
            if (w.W is double ww) wo["w"] = ww;
            if (w.H is double h) wo["h"] = h;
            o["detailsWindow"] = wo;
        }
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed class SettingsStore
{
    private readonly string path;
    private readonly object gate = new();
    private Timer? debounce;
    private Settings pending = Settings.Default;
    public SettingsStore(string path) { this.path = path; }
    public string Path => path;

    public Settings Load()
    {
        try { return File.Exists(path) ? Settings.Parse(File.ReadAllText(path)) : Settings.Default; }
        catch (Exception) { return Settings.Default; }
    }

    /// <summary>Atomic write with a 300 ms debounce.</summary>
    public void Save(Settings s)
    {
        lock (gate)
        {
            pending = s;
            debounce ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            debounce.Change(300, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        Settings s;
        lock (gate) { s = pending; debounce?.Change(Timeout.Infinite, Timeout.Infinite); }
        try { AtomicFile.WriteAllText(path, s.ToJson()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
