using MonitorTray.Core.Normalize;

namespace MonitorTray.Core.Fans;

/// <summary>
/// Fans da placa desligados pelo usuário no popup (clique no card). Só memória: ao sair/reiniciar tudo volta ligado.
/// Regras: CPU/bombas nunca desligam; sem leitura da CPU ou CPU &gt; 85 °C / GPU &gt; 80 °C não desliga e religa todos;
/// FULL e modo game religam todos; os demais modos não mexem nos desligados nem nos em MANUAL (FULL tira do MANUAL). No chip, "desligado" é uma curva de proteção
/// (0% até 80 °C, 100% a partir de 85 °C), então o fan volta sozinho mesmo com o app fechado/travado.
/// </summary>
public enum BoardFanClick { None, TurnOn, TurnOff, Blocked }

public sealed class BoardFanOff
{
    public const float CpuLimitC = TempLimits.CpuC, GpuLimitC = TempLimits.GpuC;
    public const int CurveOffUntilC = 80, CurveFullFromC = 85;

    private readonly HashSet<string> off = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> manual = new(StringComparer.Ordinal);

    /// <summary>Menor % do MANUAL na placa (abaixo disso os fans de gabinete param; para 0 existe o OFF).</summary>
    public const int ManualMinPercent = 20;

    /// <summary>Conector que nunca desliga (cadeado): fan da CPU e bombas.</summary>
    public static bool Locked(string fan) =>
        fan.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || fan.Contains("Pump", StringComparison.OrdinalIgnoreCase);

    /// <summary>Temperatura acima do limite: nada fica desligado.</summary>
    public static bool TooHot(float? cpuC, float? gpuC) => cpuC > CpuLimitC || gpuC > GpuLimitC;

    /// <summary>Por que este fan não pode desligar agora (null = pode).</summary>
    public static string? BlockReason(string fan, float? cpuC, float? gpuC) =>
        Locked(fan) ? "CPU e bombas não desligam"
        : cpuC is null ? "sem leitura da temperatura da CPU"
        : TooHot(cpuC, gpuC) ? $"CPU acima de {CpuLimitC:0} °C ou GPU acima de {GpuLimitC:0} °C"
        : null;

    /// <summary>
    /// Curva de proteção gravada no lugar da curva do fan: <paramref name="points"/> pontos (°C, PWM 0..255),
    /// PWM 0 até 80 °C e 255 a partir de 85 °C. Temperaturas crescentes (os primeiros pontos de 10 em 10 °C abaixo de 80).
    /// </summary>
    public static int[][] ProtectionCurve(int points) => HoldCurve(points, 0);

    /// <summary>
    /// Curva plana no PWM pedido (0..255) até 80 °C e 255 a partir de 85 °C: usada pelo OFF (PWM 0) e pelo MANUAL.
    /// O chip sobe o fan sozinho se esquentar, mesmo com o Marz fechado/travado.
    /// </summary>
    public static int[][] HoldCurve(int points, int pwm)
    {
        if (points < 2) throw new ArgumentOutOfRangeException(nameof(points));
        pwm = Math.Clamp(pwm, 0, 255);
        var c = new int[points][];
        for (int i = 0; i < points - 1; i++) c[i] = [CurveOffUntilC - (points - 2 - i) * 10, pwm];
        c[points - 1] = [CurveFullFromC, 255];
        return c;
    }

    /// <summary>% do MANUAL limitado (mínimo <see cref="ManualMinPercent"/>, máximo 100).</summary>
    public static int ClampManual(int percent) => Math.Clamp(percent, ManualMinPercent, 100);

    /// <summary>% fixo do fan em MANUAL (null = segue a curva/modo).</summary>
    public int? ManualPercent(string fan) { lock (off) return manual.TryGetValue(fan, out var p) ? p : null; }
    public bool AnyManual { get { lock (off) return manual.Count > 0; } }
    public IReadOnlyList<string> Manual { get { lock (off) return manual.Keys.ToList(); } }

    /// <summary>Põe em MANUAL (sai do OFF). Devolve o % aplicado.</summary>
    public int SetManual(string fan, int percent)
    {
        var p = ClampManual(percent);
        lock (off) { off.Remove(fan); manual[fan] = p; }
        return p;
    }

    /// <summary>Volta para a curva. true = estava em MANUAL.</summary>
    public bool ClearManual(string fan) { lock (off) return manual.Remove(fan); }
    public IReadOnlyList<string> ClearAllManual() { lock (off) { var l = manual.Keys.ToList(); manual.Clear(); return l; } }

    public IReadOnlyList<string> Off { get { lock (off) return off.ToList(); } }
    public bool IsOff(string fan) { lock (off) return off.Contains(fan); }
    public bool Any { get { lock (off) return off.Count > 0; } }

    /// <summary>Marca como desligado se as regras deixam. true = mudou.</summary>
    public bool TurnOff(string fan, float? cpuC, float? gpuC)
    {
        if (BlockReason(fan, cpuC, gpuC) != null) return false;
        lock (off) { manual.Remove(fan); return off.Add(fan); }
    }

    public bool TurnOn(string fan) { lock (off) return off.Remove(fan); }

    /// <summary>
    /// O que o clique no card faz. Opção "DESLIGAR FAN DA PLACA" desligada ou CPU/bomba: nada (o card nem parece clicável).
    /// Desligado: religa. Ligado: desliga, ou Blocked quando as regras não deixam agora (BlockReason diz o motivo).
    /// </summary>
    public BoardFanClick ClickAction(string fan, bool optionOn, float? cpuC, float? gpuC) =>
        !optionOn || Locked(fan) ? BoardFanClick.None
        : IsOff(fan) ? BoardFanClick.TurnOn
        : BlockReason(fan, cpuC, gpuC) != null ? BoardFanClick.Blocked
        : BoardFanClick.TurnOff;

    /// <summary>O card do fan reage ao mouse (mãozinha, clique) só com a opção ligada e fora do cadeado.</summary>
    public static bool Clickable(string fan, bool optionOn) => optionOn && !Locked(fan);

    public IReadOnlyList<string> ClearAll() { lock (off) { var l = off.ToList(); off.Clear(); return l; } }

    /// <summary>Checagem a cada leitura: sem leitura da CPU ou acima do limite religa todos. Devolve os religados.</summary>
    public IReadOnlyList<string> Check(float? cpuC, float? gpuC) => cpuC is null || TooHot(cpuC, gpuC) ? ClearAll() : [];

    /// <summary>
    /// Fans que recebem um modo. FULL inclui os desligados (religa todos, mesmo fora da lista) e limpa o estado;
    /// os outros modos pulam os desligados, que seguem desligados.
    /// </summary>
    public IReadOnlyList<string> ModeTargets(string mode, IEnumerable<string> fans)
    {
        lock (off)
        {
            if (mode == "full") { var all = fans.Union(off).Union(manual.Keys).ToList(); off.Clear(); manual.Clear(); return all; }
            return fans.Where(f => !off.Contains(f) && !manual.ContainsKey(f)).ToList();
        }
    }
}
