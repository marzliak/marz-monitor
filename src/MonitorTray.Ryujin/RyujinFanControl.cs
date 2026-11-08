using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorTray.Ryujin;

/// <summary>Modo de um fan do cooler: segue a curva do preset (Smart) ou % fixo (Fixed).</summary>
public enum FanMode { Smart, Fixed }

public sealed record CoolerFanSetting
{
    public FanMode Mode { get; init; } = FanMode.Smart;
    public RyujinCurves.Preset Preset { get; init; } = RyujinCurves.Preset.Standard;
    /// <summary>% do modo Fixed (0..100). Para a bomba nunca abaixo de <see cref="CoolerSafety.PumpMinPercent"/>.</summary>
    public int FixedPercent { get; init; } = 60;
    /// <summary>Curva própria (°C → %); vazia = curva do preset.</summary>
    public IReadOnlyList<CurveNode> Custom { get; init; } = [];
}

/// <summary>Configuração do controle do cooler (%APPDATA%\MonitorTray\ryujin-fans.json). Desligado por padrão.</summary>
public sealed record CoolerConfig
{
    public int Version { get; init; } = 1;
    /// <summary>Marz é dono das velocidades. Desligado = só leitura (deixe o Armoury Crate controlar).</summary>
    public bool Enabled { get; init; }
    /// <summary>Sensor de temperatura da curva (id do LHM ou "calc:cpu-temp"). Padrão: CPU, como o Armoury Crate.</summary>
    public string SourceId { get; init; } = "calc:cpu-temp";
    public CoolerFanSetting Pump { get; init; } = new() { Preset = RyujinCurves.Preset.Standard };
    public CoolerFanSetting Radiator { get; init; } = new() { Preset = RyujinCurves.Preset.Standard };
    public CoolerFanSetting Embedded { get; init; } = new() { Mode = FanMode.Fixed, FixedPercent = 60, Preset = RyujinCurves.Preset.Standard };
    /// <summary>Tabela % → rpm do radiador (Auto Tuning). Vazia = tabela padrão do Armoury Crate.</summary>
    public IReadOnlyList<RpmPoint> RadiatorTable { get; init; } = [];
    public DateTimeOffset? RadiatorTunedAt { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CoolerConfig Parse(string json)
    {
        CoolerConfig? c;
        try { c = JsonSerializer.Deserialize<CoolerConfig>(json, Json); } catch (Exception) { return new(); }
        if (c is null || c.Version != 1) return new();
        return c.Normalized();
    }

    public CoolerConfig Normalized()
    {
        static CoolerFanSetting N(CoolerFanSetting? s, int min) => (s ?? new()) with
        {
            FixedPercent = Math.Clamp((s ?? new()).FixedPercent, min, 100),
            Custom = ((s ?? new()).Custom ?? []).Take(8).Select(n => new CurveNode(Math.Clamp(n.TempC, 0, 110), Math.Clamp(n.Percent, 0, 100))).OrderBy(n => n.TempC).ToList(),
        };
        return this with
        {
            SourceId = string.IsNullOrWhiteSpace(SourceId) ? "calc:cpu-temp" : SourceId,
            Pump = N(Pump, CoolerSafety.PumpMinPercent), Radiator = N(Radiator, 0), Embedded = N(Embedded, 0),
            RadiatorTable = (RadiatorTable ?? []).Where(p => p.Percent is >= 0 and <= 100 && p.Rpm is >= 0 and < 10000).OrderBy(p => p.Percent).ToList(),
        };
    }
}

/// <summary>
/// Média móvel exponencial (no tempo) da temperatura da fonte da curva. A CPU Package oscila 10–20 °C em picos curtos;
/// a curva segue a média dos últimos segundos, como o tempo de subida/descida do Fan Xpert. Sem leitura = zera.
/// </summary>
public sealed class TempSmoother
{
    /// <summary>Constante de tempo (s): um degrau chega a 63% em ~5 s.</summary>
    public const double DefaultTauSeconds = 5;
    private readonly double tau;
    private double? value;
    public TempSmoother(double tauSeconds = DefaultTauSeconds) { tau = tauSeconds; }
    public double? Value => value;
    public void Reset() => value = null;

    /// <param name="dtSeconds">Tempo desde a amostra anterior; 0 não muda o valor (chamadas repetidas no mesmo instante).</param>
    public double? Next(double? sample, double dtSeconds)
    {
        if (sample is not double x || double.IsNaN(x)) { value = null; return null; }
        if (value is not double v || tau <= 0) { value = x; return x; }
        double dt = Math.Clamp(dtSeconds, 0, 60);
        value = v + dt / (tau + dt) * (x - v);
        return value;
    }
}

/// <summary>Limites de segurança (independentes do que o usuário escolher).</summary>
public static class CoolerSafety
{
    /// <summary>Bomba nunca abaixo disto (Armoury Crate: 20 % no início da curva; aqui mais conservador).</summary>
    public const int PumpMinPercent = 30;
    /// <summary>Água a partir disto: bomba e radiador em 100 %.</summary>
    public const double WaterPanicC = 50;
    /// <summary>Fonte da curva sem leitura: usa este % (bomba e radiador).</summary>
    public const int NoSourcePercent = 70;
    /// <summary>Valor deixado no cooler ao sair do Marz (ele congela no último valor): Standard a ~60 °C.</summary>
    public const int ExitPumpPercent = 60, ExitRadiatorPercent = 60, ExitEmbeddedPercent = 60;
}

public sealed record CoolerTargets(int PumpPercent, int RadiatorPercent, int EmbeddedPercent, string? Reason);

/// <summary>Calcula o % de cada fan como o Armoury Crate (curva no host), com as travas de segurança.</summary>
public static class CoolerLogic
{
    public static IReadOnlyList<CurveNode> CurveOf(CoolerFanSetting s, RyujinCurves.Fan fan) =>
        s.Custom.Count >= 2 ? s.Custom : RyujinCurves.CurveFor(fan, s.Preset);

    public static int PercentFor(CoolerFanSetting s, RyujinCurves.Fan fan, double? sourceC)
    {
        if (s.Mode == FanMode.Fixed) return s.FixedPercent;
        if (s.Preset == RyujinCurves.Preset.FullSpeed && s.Custom.Count < 2) return 100;
        if (sourceC is not double t) return CoolerSafety.NoSourcePercent;
        return (int)Math.Round(RyujinCurves.Interpolate(CurveOf(s, fan), t));
    }

    public static CoolerTargets Compute(CoolerConfig c, double? sourceC, double? waterC)
    {
        int pump = PercentFor(c.Pump, RyujinCurves.Fan.Pump, sourceC);
        int rad = PercentFor(c.Radiator, RyujinCurves.Fan.Radiator, sourceC);
        int emb = PercentFor(c.Embedded, RyujinCurves.Fan.Embedded, sourceC);
        string? reason = sourceC is null && (c.Pump.Mode == FanMode.Smart || c.Radiator.Mode == FanMode.Smart) ? "sem leitura da temperatura da curva" : null;
        if (waterC is double w && w >= CoolerSafety.WaterPanicC) { pump = 100; rad = 100; reason = $"água {w:0.0} °C"; }
        pump = Math.Clamp(Math.Max(pump, CoolerSafety.PumpMinPercent), 0, 100);
        return new CoolerTargets(pump, Math.Clamp(rad, 0, 100), Math.Clamp(emb, 0, 100), reason);
    }

    /// <summary>Suaviza a subida/descida como o spinUp/spinDown do AC: sobe rápido, desce devagar (por tick de ~1 s).</summary>
    public static int Smooth(int current, int target, int upStep = 10, int downStep = 3) =>
        target > current ? Math.Min(target, current + upStep) : Math.Max(target, current - downStep);
}

/// <summary>
/// Botões do popup CURVAS/SILENT/STD/TURBO/FULL: um preset único em bomba e radiador, ou a config própria (CURVAS).
/// Valem para bomba, radiador e fan embutido; a fonte da curva e a tabela do radiador não mudam.
/// </summary>
public static class CoolerModes
{
    /// <summary>Preset único em bomba e radiador; null = config própria (curva, % fixo ou presets diferentes).</summary>
    public static RyujinCurves.Preset? UniformPreset(CoolerConfig c) =>
        c.Pump.Mode == FanMode.Smart && c.Radiator.Mode == FanMode.Smart && c.Pump.Preset == c.Radiator.Preset
        && c.Pump.Custom.Count == 0 && c.Radiator.Custom.Count == 0 ? c.Pump.Preset : null;

    /// <summary>Bomba, radiador e fan embutido no preset (Enabled não muda).</summary>
    public static CoolerConfig WithPreset(CoolerConfig c, RyujinCurves.Preset p) => c with
    {
        Pump = c.Pump with { Mode = FanMode.Smart, Preset = p, Custom = [] },
        Radiator = c.Radiator with { Mode = FanMode.Smart, Preset = p, Custom = [] },
        Embedded = c.Embedded with { Mode = FanMode.Smart, Preset = p, Custom = [] },
    };

    /// <summary>Fan embutido com config própria (curva sua ou % fixo), não um preset.</summary>
    public static bool OwnEmbedded(CoolerConfig c) => c.Embedded.Mode == FanMode.Fixed || c.Embedded.Custom.Count >= 2;

    /// <summary>
    /// O que guardar antes de aplicar um preset: a config própria atual. Num preset (bomba/radiador), mantém a já guardada,
    /// mas guarda o fan embutido se ele tiver config própria (o preset agora também mexe nele). null = nada a guardar.
    /// </summary>
    public static CoolerConfig? ToKeep(CoolerConfig c, CoolerConfig? kept = null) =>
        UniformPreset(c) == null ? c : OwnEmbedded(c) ? (kept ?? c) with { Embedded = c.Embedded } : null;

    /// <summary>
    /// CURVAS: já em config própria, só liga o controle; senão bomba e radiador voltam à config própria guardada antes do preset;
    /// sem uma guardada, curvas padrão (os pontos do STD como curva própria, editável no controle térmico).
    /// </summary>
    public static CoolerConfig Curves(CoolerConfig current, CoolerConfig? saved)
    {
        if (UniformPreset(current) == null) return current with { Enabled = true };
        var emb = saved != null && OwnEmbedded(saved) ? saved.Embedded : DefaultCurve(current.Embedded, RyujinCurves.Fan.Embedded);
        if (saved != null && UniformPreset(saved) == null) return current with { Enabled = true, Pump = saved.Pump, Radiator = saved.Radiator, Embedded = emb };
        return current with { Enabled = true, Pump = DefaultCurve(current.Pump, RyujinCurves.Fan.Pump), Radiator = DefaultCurve(current.Radiator, RyujinCurves.Fan.Radiator), Embedded = emb };
    }

    /// <summary>
    /// Saída do modo game por qualquer caminho: cada parte que ainda está como o game deixou (FULL sem curva ou % fixo do game)
    /// volta ao que era antes; partes que o usuário mudou durante o game ficam. Enabled segue o atual.
    /// </summary>
    public static CoolerConfig UndoGame(CoolerConfig current, CoolerConfig before, int gamePercent)
    {
        bool G(CoolerFanSetting s) => gamePercent >= 100
            ? s.Mode == FanMode.Smart && s.Preset == RyujinCurves.Preset.FullSpeed && s.Custom.Count < 2
            : s.Mode == FanMode.Fixed && s.FixedPercent == gamePercent;
        return current with
        {
            Pump = G(current.Pump) ? before.Pump : current.Pump,
            Radiator = G(current.Radiator) ? before.Radiator : current.Radiator,
            Embedded = G(current.Embedded) ? before.Embedded : current.Embedded,
        };
    }

    /// <summary>Fora do modo game o fan embutido nunca fica no preset FULL (resto de um game que não foi desfeito): volta ao STD.</summary>
    public static CoolerConfig? UnstickEmbedded(CoolerConfig c) =>
        c.Embedded.Mode == FanMode.Smart && c.Embedded.Preset == RyujinCurves.Preset.FullSpeed
            ? c with { Embedded = c.Embedded with { Preset = RyujinCurves.Preset.Standard } } : null;

    private static CoolerFanSetting DefaultCurve(CoolerFanSetting s, RyujinCurves.Fan fan) =>
        s with { Mode = FanMode.Smart, Preset = RyujinCurves.Preset.Standard, Custom = RyujinCurves.CurveFor(fan, RyujinCurves.Preset.Standard).ToList() };
}
