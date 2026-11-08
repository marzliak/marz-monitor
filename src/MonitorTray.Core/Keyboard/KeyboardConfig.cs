using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorTray.Core.Keyboard;

/// <summary>Uma linha do OLED: sensor (id do LHM ou "calc:..."), prefixo, sufixo e casas decimais.</summary>
public sealed record OledLine
{
    public string SensorId { get; init; } = "";
    public string Prefix { get; init; } = "";
    public string Suffix { get; init; } = "";
    public int Decimals { get; init; }
}

/// <summary>Uma tela do teclado: até 2 linhas, tempo na tela e ícone GameSense (0–255).</summary>
public sealed record OledPage
{
    public int DurationMs { get; init; } = 5000;
    public int IconId { get; init; }
    public IReadOnlyList<OledLine> Lines { get; init; } = [new(), new()];
}

/// <summary>Configuração do teclado (%APPDATA%\MonitorTray\keyboard.json).</summary>
public sealed record KeyboardConfig
{
    public const int MaxPages = 20, LineWidth = 20;
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; } = true;
    public bool WheelEnabled { get; init; } = true;
    public bool MuteCompensation { get; init; } = true;
    public IReadOnlyList<OledPage> Pages { get; init; } = DefaultPages;

    public static IReadOnlyList<OledPage> DefaultPages { get; } =
    [
        new() { Lines = [L("calc:cpu-load", "CPU uso ", "%", 0), L("calc:cpu-temp", "CPU temp ", " C", 0)] },
        new() { Lines = [L("calc:cpu-clock-max", "Clock max ", " GHz", 2), L("calc:cpu-power", "CPU pacote ", " W", 0)] },
        new() { Lines = [L("calc:gpu-temp", "GPU temp ", " C", 0), L("calc:gpu-load", "GPU uso ", "%", 0)] },
        new() { Lines = [L("calc:mem-used", "RAM ", " GB", 1), L("calc:mem-load", "RAM ", "%", 0)] },
    ];

    public static OledLine L(string id, string prefix, string suffix, int decimals) => new() { SensorId = id, Prefix = prefix, Suffix = suffix, Decimals = decimals };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Inválido ou corrompido → padrão. Valores fora da faixa são limitados, nunca rejeitam o arquivo inteiro.</summary>
    public static KeyboardConfig Parse(string json)
    {
        KeyboardConfig? c;
        try { c = JsonSerializer.Deserialize<KeyboardConfig>(json, Json); } catch (Exception) { return new(); }
        if (c is null || c.Version != 1) return new();
        return c.Normalized();
    }

    public KeyboardConfig Normalized()
    {
        var pages = (Pages ?? []).Take(MaxPages).Select(p => p with
        {
            DurationMs = Math.Clamp(p.DurationMs, 1000, 60000),
            IconId = Math.Clamp(p.IconId, 0, 255),
            Lines = Enumerable.Range(0, 2).Select(i => (p.Lines ?? []).ElementAtOrDefault(i) is { } l
                ? l with { SensorId = l.SensorId ?? "", Prefix = Cut(l.Prefix), Suffix = Cut(l.Suffix), Decimals = Math.Clamp(l.Decimals, 0, 3) }
                : new OledLine()).ToList(),
        }).ToList();
        if (pages.Count == 0) pages = DefaultPages.ToList();
        return this with { Pages = pages };
    }

    private static string Cut(string? s) => (s ?? "").Length > LineWidth ? s![..LineWidth] : s ?? "";
}

/// <summary>Monta as 2 linhas de 20 colunas (mesma regra do GPU by MarzOps: prefixos e valores alinhados).</summary>
public static class OledComposer
{
    public static string[] Compose(OledPage page, Func<string, double?> value, Func<string, string> fallbackName)
    {
        var lines = page.Lines.Take(2).ToList();
        while (lines.Count < 2) lines.Add(new());
        string Prefix(OledLine l) => string.IsNullOrEmpty(l.Prefix) ? fallbackName(l.SensorId) : l.Prefix;
        string Val(OledLine l) => value(l.SensorId) is double v && double.IsFinite(v) ? v.ToString("F" + l.Decimals, CultureInfo.InvariantCulture) : "-";
        var used = lines.Where(l => l.SensorId.Length > 0).ToList();
        int pw = used.Count == 0 ? 0 : used.Max(l => Prefix(l).Length);
        int vw = used.Count == 0 ? 0 : used.Max(l => Val(l).Length);
        return lines.Select(l =>
        {
            if (l.SensorId.Length == 0) return " ";
            var s = Prefix(l).PadRight(pw) + Val(l).PadLeft(vw) + l.Suffix;
            return s.Length > KeyboardConfig.LineWidth ? s[..KeyboardConfig.LineWidth] : s;
        }).ToArray();
    }

    /// <summary>Página fixada ganha "* " na 1ª linha só se couber sem cortar número/unidade.</summary>
    public static string[] DecoratePinned(string[] lines, bool pinned)
    {
        if (!pinned || lines.Length == 0) return lines;
        var copy = (string[])lines.Clone();
        var first = copy[0].Trim();
        if (first.Length > KeyboardConfig.LineWidth - 2) first = System.Text.RegularExpressions.Regex.Replace(first, " {2,}", " ");
        if (first.Length <= KeyboardConfig.LineWidth - 2) copy[0] = "* " + first;
        return copy;
    }
}

/// <summary>Valores calculados, genéricos (nenhum modelo fixo): disponíveis para qualquer hardware.</summary>
public static class CalculatedSensors
{
    public static readonly IReadOnlyList<(string Id, string Name, string Unit)> All =
    [
        ("calc:cpu-load", "CPU · uso total", "%"),
        ("calc:cpu-temp", "CPU · temperatura (package)", "°C"),
        ("calc:cpu-power", "CPU · potência (package)", "W"),
        ("calc:cpu-clock-max", "CPU · clock máximo dos núcleos", "GHz"),
        ("calc:cpu-clock-avg", "CPU · clock médio dos núcleos", "GHz"),
        ("calc:gpu-load", "GPU · uso", "%"),
        ("calc:gpu-temp", "GPU · temperatura do core", "°C"),
        ("calc:gpu-power", "GPU · potência", "W"),
        ("calc:gpu-fan", "GPU · fans (maior %)", "%"),
        ("calc:mem-used", "Memória · usada", "GB"),
        ("calc:mem-load", "Memória · uso", "%"),
        ("calc:disk-read", "Disco · leitura", "MB/s"),
        ("calc:disk-write", "Disco · escrita", "MB/s"),
        ("calc:net-down", "Rede · download", "MB/s"),
        ("calc:net-up", "Rede · upload", "MB/s"),
    ];

    /// <summary>Clocks de núcleo da 1ª CPU (exclui Bus Speed). Genérico: P/E-core, CCD, "Core #n".</summary>
    public static (double? Max, double? Avg) CoreClocksGHz(IReadOnlyList<Model.RawSensor> raw)
    {
        var dev = raw.Where(r => r.Family == "Cpu").Select(r => r.DeviceId).Order(StringComparer.Ordinal).FirstOrDefault();
        var v = raw.Where(r => r.DeviceId == dev && r.KindRaw == "Clock" && r.Value is float f && f > 0 && !r.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                   .Select(r => (double)r.Value!.Value / 1000).ToList();
        return v.Count == 0 ? (null, null) : (v.Max(), v.Average());
    }

    /// <summary>Converte identificadores sintéticos do GPU by MarzOps.</summary>
    public static string MapLegacy(string id) => id switch
    {
        _ when id.EndsWith("/clock/max-ghz", StringComparison.Ordinal) => "calc:cpu-clock-max",
        "/ram/data/0" => "calc:mem-used",
        "/ram/load/0" => "calc:mem-load",
        _ => id,
    };
}
