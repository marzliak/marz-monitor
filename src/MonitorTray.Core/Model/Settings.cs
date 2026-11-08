using System.Text.Json;
using System.Text.Json.Nodes;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Model;

public sealed record DetailsWindowState(double? X, double? Y, double? W, double? H, bool Maximized);

/// <summary>Configurações persistidas (spec/settings.schema.json). Valores inválidos → padrão.</summary>
public sealed record Settings
{
    public static readonly string[] Themes = ["claro", "escuro", "sistema"];
    public static readonly string[] Palettes = ["eletrico", "neon", "lima", "brasa", "violeta", "aco"];
    public static readonly int[] RefreshOptions = [500, 1000, 2000];
    public static readonly int[] HistoryOptions = [60, 300, 600, 1800, 3600, 7200]; // segundos; máx. 2 h
    public static readonly string[] Scales = ["independente", "compartilhada"];
    public static readonly string[] NetUnits = ["bytes", "bits"];

    public int Version { get; init; } = 1;
    public string Theme { get; init; } = "escuro";
    public string Palette { get; init; } = "eletrico";
    public int Opacity { get; init; } = 90;
    public int RefreshMs { get; init; } = 1000;
    public int History { get; init; } = 60;
    public string Scale { get; init; } = "independente";
    /// <summary>Rede em bytes/s (MB/s, base 1024) ou bits/s (Mbps, base 1000).</summary>
    public string NetUnit { get; init; } = "bytes";
    /// <summary>Modos da placa (Armoury Crate), na ordem dos botões.</summary>
    public static readonly string[] AcModeIds = ["curvas", "silent", "standard", "turbo", "full"];
    /// <summary>Modos da placa que aparecem no popup (padrão: todos) e ventoinhas que entram neles.</summary>
    public IReadOnlyList<string> AcModes { get; init; } = AcModeIds;
    public IReadOnlyList<string> AcFans { get; init; } = ["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"];
    /// <summary>Nome curto de cada ventoinha da placa (conector do Armoury Crate → nome do usuário).</summary>
    public IReadOnlyDictionary<string, string> AcFanNames { get; init; } = new Dictionary<string, string>
    { ["Chassis Fan 1"] = "Exaustor", ["Chassis Fan 2"] = "Front top", ["Chassis Fan 3"] = "Front down", ["Chassis Fan 4"] = "Rear" };
    /// <summary>Modo game: o que vai a 100% (gpu, board, pump, radiator, embedded).</summary>
    public static readonly string[] GameTargetIds = ["gpu", "board", "pump", "radiator", "embedded"];
    public IReadOnlyList<string> GameTargets { get; init; } = GameTargetIds;
    /// <summary>Ventoinhas da placa no modo game; null = todas as que o Armoury Crate expõe.</summary>
    public IReadOnlyList<string>? GameBoardFans { get; init; }
    public static readonly int[] GamePercents = [70, 85, 100];
    public int GameGpuPercent { get; init; } = 100;
    public int GameCoolerPercent { get; init; } = 100;
    /// <summary>Cards de temperatura do popup, na ordem (ids de TempCards; no máximo 8).</summary>
    public IReadOnlyList<string> PopupTemps { get; init; } = TempCards.DefaultIds;
    /// <summary>Fans da placa (nome do Armoury Crate) que viram card no popup. Padrão: CPU e chassis (sem bombas nem CPU_OPT).</summary>
    public static readonly string[] DefaultBoardCards = ["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"];
    public IReadOnlyList<string> PopupBoardFans { get; init; } = DefaultBoardCards;
    /// <summary>Partes do water cooler que viram card no popup (pump, radiator, embedded).</summary>
    public static readonly string[] CoolerCardIds = ["pump", "radiator", "embedded"];
    public IReadOnlyList<string> PopupCoolerFans { get; init; } = CoolerCardIds;
    /// <summary>Experimental (desligado por padrão): clique no card de um fan da placa desliga/religa (curva de proteção).</summary>
    public bool BoardFanOff { get; init; } = false;
    /// <summary>Ícone da bandeja mostra a maior temperatura entre os cards habilitados (PopupTemps).</summary>
    public bool TrayTemp { get; init; } = false;
    /// <summary>Ícone da bandeja muda de cor com algum card perto do limite (≥ limite − 5 °C).</summary>
    public bool TrayAlert { get; init; } = false;
    /// <summary>Tamanho do número no ícone da bandeja: p (padrão, o menor legível), m ou g (ocupa o quadrado todo).</summary>
    public static readonly string[] TrayFontSizes = ["pp", "p", "m", "g"];
    public string TrayFontSize { get; init; } = "pp";
    /// <summary>Altura máxima dos dígitos no ícone, em fração do lado (16/24/32 px).</summary>
    public static double TrayFontScale(string size) => size switch { "g" => 1.0, "m" => 0.85, "p" => 0.72, _ => 0.58 };
    /// <summary>Temperaturas do ícone da bandeja, na ordem do rodízio. Vazio = a mais quente entre os cards do popup.</summary>
    public IReadOnlyList<string> TrayTemps { get; init; } = [];
    /// <summary>Segundos entre uma temperatura e a próxima no ícone (com mais de uma escolhida).</summary>
    public static readonly int[] TrayRotateSeconds = [2, 3, 5, 10];
    public int TrayRotateSec { get; init; } = 3;
    /// <summary>Executáveis que ligam o modo game sozinhos enquanto rodam ("jogo.exe"; vazio = desligado).</summary>
    public IReadOnlyList<string> GameExes { get; init; } = [];
    public bool Autostart { get; init; } = true;
    public bool Autohide { get; init; } = false;
    /// <summary>Iniciar com o Windows só na bandeja, sem abrir o popup.</summary>
    public bool StartHidden { get; init; } = true;
    /// <summary>Fechar (X) esconde na bandeja; desligado = fechar encerra o app (fans voltam ao automático).</summary>
    public bool CloseToTray { get; init; } = true; // usuário: popup fica aberto ao clicar em outra janela (desvio do kit)
    public DetailsWindowState? DetailsWindow { get; init; }
    /// <summary>Escolha do sensor de cada cartão do Detalhes: chave "deviceId|tipo" → "avg", "max" ou id do sensor.</summary>
    public IReadOnlyDictionary<string, string> CardPicks { get; init; } = new Dictionary<string, string>();

    public bool Equals(Settings? o) => o is not null && Version == o.Version && Theme == o.Theme && Palette == o.Palette && Opacity == o.Opacity
        && RefreshMs == o.RefreshMs && History == o.History && Scale == o.Scale && NetUnit == o.NetUnit && AcModes.SequenceEqual(o.AcModes) && AcFans.SequenceEqual(o.AcFans)
        && GameTargets.SequenceEqual(o.GameTargets) && (GameBoardFans is null) == (o.GameBoardFans is null) && (GameBoardFans ?? []).SequenceEqual(o.GameBoardFans ?? [])
        && GameGpuPercent == o.GameGpuPercent && GameCoolerPercent == o.GameCoolerPercent
        && PopupTemps.SequenceEqual(o.PopupTemps) && PopupBoardFans.SequenceEqual(o.PopupBoardFans) && PopupCoolerFans.SequenceEqual(o.PopupCoolerFans) && BoardFanOff == o.BoardFanOff
        && TrayTemp == o.TrayTemp && TrayAlert == o.TrayAlert && TrayFontSize == o.TrayFontSize && TrayTemps.SequenceEqual(o.TrayTemps) && TrayRotateSec == o.TrayRotateSec && GameExes.SequenceEqual(o.GameExes)
        && Autostart == o.Autostart && Autohide == o.Autohide
        && StartHidden == o.StartHidden && CloseToTray == o.CloseToTray && Equals(DetailsWindow, o.DetailsWindow)
        && CardPicks.Count == o.CardPicks.Count && CardPicks.All(kv => o.CardPicks.TryGetValue(kv.Key, out var v) && v == kv.Value);
    public override int GetHashCode() => HashCode.Combine(Theme, Palette, Opacity, RefreshMs, History, Autohide, CardPicks.Count);

    public static Settings Default { get; } = new();

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
            AcModes = Strings(o["acModes"])?.Where(AcModeIds.Contains).ToList() ?? d.AcModes,
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
            TrayFontSize = Get("trayFontSize", d.TrayFontSize, TrayFontSizes.Contains),
            TrayTemps = Strings(o["trayTemps"]) is { } tt ? PickList.Clean(tt, TempCards.Ids.ToList()) : d.TrayTemps,
            TrayRotateSec = Get("trayRotateSec", d.TrayRotateSec, TrayRotateSeconds.Contains),
            GameExes = Strings(o["gameExes"])?.Select(Fans.AutoGame.Clean).OfType<string>().Distinct().ToList() ?? d.GameExes,
            AcFanNames = o["acFanNames"] is JsonObject fn ? fn.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => { try { var v = kv.Value!.GetValue<string>().Trim(); return v.Length > 16 ? v[..16] : v; } catch (Exception) { return ""; } }) : d.AcFanNames,
            Theme = Get("theme", d.Theme, Themes.Contains),
            Palette = Get("palette", d.Palette, Palettes.Contains),
            Opacity = Get("opacity", d.Opacity, v => v is >= 40 and <= 100 && v % 5 == 0),
            RefreshMs = Get("refreshMs", d.RefreshMs, RefreshOptions.Contains),
            History = Get("history", d.History, HistoryOptions.Contains),
            Scale = Get("scale", d.Scale, Scales.Contains),
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

    /// <summary>Gravação atômica com debounce de 300 ms.</summary>
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
