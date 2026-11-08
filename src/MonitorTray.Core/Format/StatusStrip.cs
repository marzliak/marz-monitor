using System.Globalization;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Format;

/// <summary>Proteção do OFF religou fans da placa (CPU/GPU acima do limite ou CPU sem leitura).</summary>
public sealed record SafetyEvent(DateTimeOffset At, IReadOnlyList<string> Fans, float? CpuC, float? GpuC);

/// <summary>Estado automático que a faixa do topo do popup resume.</summary>
public sealed record StripState
{
    public bool GameOn { get; init; }
    /// <summary>Executável que ligou o modo game (null = ligado na mão).</summary>
    public string? GameBy { get; init; }
    public IReadOnlyCollection<string> GameTargets { get; init; } = [];
    public int GameGpuPercent { get; init; } = 100;
    public int GameCoolerPercent { get; init; } = 100;
    public SafetyEvent? BoardSafety { get; init; }
    /// <summary>Aviso de proteção da GPU (fans promovidas a 100%).</summary>
    public string? GpuProtection { get; init; }
    /// <summary>Água do cooler no limite: bomba e radiador em 100%.</summary>
    public double? CoolerPanicC { get; init; }
}

/// <summary>Um item da faixa: texto curto e o detalhe do tooltip.</summary>
public sealed record StripItem(string Text, string Tip);

/// <summary>
/// Faixa fina do topo do popup: o que o app fez sozinho ou está mantendo ativo (proteções e modo game).
/// Vazia = a faixa some. Eventos passados (religou fans) ficam visíveis por <see cref="EventTtl"/>.
/// </summary>
public static class StatusStrip
{
    public static readonly TimeSpan EventTtl = TimeSpan.FromMinutes(5);

    private static string C(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "°";

    public static IReadOnlyList<StripItem> Items(StripState s, DateTimeOffset now, Func<string, string> fanShort)
    {
        var l = new List<StripItem>();
        if (s.GpuProtection is { Length: > 0 } gp) l.Add(new("GPU 100% · proteção", gp));
        if (s.CoolerPanicC is double w) l.Add(new($"COOLER 100% · água {w.ToString("0.0", CultureInfo.InvariantCulture)}°",
            $"Água do water cooler em {w.ToString("0.0", CultureInfo.InvariantCulture)} °C: bomba e radiador em 100% até baixar de {TempLimits.LiquidC:0} °C."));
        if (s.BoardSafety is { } e && now - e.At <= EventTtl && e.Fans.Count > 0) l.Add(Safety(e, fanShort));
        if (s.GameOn)
        {
            var text = "MODO GAME · " + GameText(s.GameTargets, s.GameGpuPercent, s.GameCoolerPercent);
            l.Add(new(text, s.GameBy != null ? $"Ligado automaticamente: {s.GameBy} está rodando. Desliga quando o jogo fechar." : "Ligado no botão do modo game."));
        }
        return l;
    }

    /// <summary>"CHA1 religado · CPU 86°" (ou "sem leitura da CPU").</summary>
    public static StripItem Safety(SafetyEvent e, Func<string, string> fanShort)
    {
        var names = string.Join(", ", e.Fans.Select(fanShort));
        var why = e.CpuC is not float cpu ? "sem leitura da CPU"
            : cpu > TempLimits.CpuC ? "CPU " + C(cpu)
            : e.GpuC is float gpu && gpu > TempLimits.GpuC ? "GPU " + C(gpu)
            : "CPU " + C(cpu);
        var text = $"{names} {(e.Fans.Count > 1 ? "religados" : "religado")} · {why}";
        var tip = $"{e.At.ToLocalTime():HH:mm:ss} · proteção do OFF: {names} {(e.Fans.Count > 1 ? "voltaram" : "voltou")} ao modo atual"
            + $" (CPU acima de {TempLimits.CpuC:0} °C, GPU acima de {TempLimits.GpuC:0} °C ou CPU sem leitura). Clique para dispensar.";
        return new(text, tip);
    }

    /// <summary>"tudo 100%" quando tudo que o modo game liga vai a 100%; senão as partes ("GPU 85% · placa 100% · cooler 70%").</summary>
    public static string GameText(IReadOnlyCollection<string> targets, int gpuPercent, int coolerPercent)
    {
        bool cooler = targets.Contains("pump") || targets.Contains("radiator") || targets.Contains("embedded");
        var parts = new List<(string Name, int Pct)>();
        if (targets.Contains("gpu")) parts.Add(("GPU", gpuPercent));
        if (targets.Contains("board")) parts.Add(("placa", 100));
        if (cooler) parts.Add(("cooler", coolerPercent));
        if (parts.Count == 0) return "nada selecionado";
        if (parts.All(p => p.Pct >= 100)) return parts.Count == 3 ? "tudo 100%" : string.Join(" e ", parts.Select(p => p.Name)) + " 100%";
        return string.Join(" · ", parts.Select(p => $"{p.Name} {p.Pct}%"));
    }
}
