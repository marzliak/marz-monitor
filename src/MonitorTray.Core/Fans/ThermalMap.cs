namespace MonitorTray.Core.Fans;

/// <summary>Dispositivo do controle térmico: cada um tem o próprio modo e os próprios atuadores.</summary>
public enum ThermalDevice { Gpu, Board, Cooler }

/// <summary>Item da faixa de fans do topo: um fan físico (rpm só se medido), apontando para o atuador (curva) que o move.</summary>
public sealed record StripItem(string Name, string? Nick, string? ActuatorId, int? Rpm, int? Percent, bool Off, bool Locked);

/// <summary>
/// Modelo "fonte → curva → atuador" do controle térmico: ids dos atuadores, fonte de temperatura de cada um,
/// eixo do gráfico, itens da faixa e quando a curva da placa pode ser editada.
/// </summary>
public static class ThermalMap
{
    public const string GpuId = "gpu", PumpId = "ry:pump", RadiatorId = "ry:rad", EmbeddedId = "ry:emb";
    public const string CpuSource = "calc:cpu-temp", GpuSource = "calc:gpu-temp";
    /// <summary>Pontos da curva do cooler (a curva roda no PC; ryujin-fans.json guarda até 8).</summary>
    public const int CoolerMaxPoints = 8;

    public static string BoardId(string acName) => "mb:" + acName;
    public static string? BoardFan(string actuatorId) => actuatorId.StartsWith("mb:", StringComparison.Ordinal) ? actuatorId[3..] : null;

    public static ThermalDevice? DeviceOf(string actuatorId) =>
        actuatorId == GpuId ? ThermalDevice.Gpu
        : actuatorId.StartsWith("mb:", StringComparison.Ordinal) ? ThermalDevice.Board
        : actuatorId is PumpId or RadiatorId or EmbeddedId ? ThermalDevice.Cooler : null;

    /// <summary>Nome do conector como impresso na placa: "Chassis Fan 1" → "CHA_FAN1".</summary>
    public static string BoardHeader(string acName) => acName switch
    {
        "CPU Fan" => "CPU_FAN", "CPU Optional Fan" => "CPU_OPT", "Water Pump+" => "W_PUMP+", "AIO Pump" => "AIO_PUMP",
        _ when acName.StartsWith("Chassis Fan ", StringComparison.Ordinal) => "CHA_FAN" + acName["Chassis Fan ".Length..],
        _ => acName.ToUpperInvariant().Replace(' ', '_'),
    };

    /// <summary>
    /// Fonte de temperatura de cada conector da placa como o serviço da ASUS grava (SetMultiFanSource, see asus-rog-protocol-notes):
    /// o Chassis Fan 1 segue a GPU, os demais a CPU. Só leitura: a fonte é escolhida no Armoury Crate.
    /// </summary>
    public static string BoardSource(string acName) => acName == "Chassis Fan 1" ? GpuSource : CpuSource;

    /// <summary>Fonte (id do sensor) da curva do atuador: GPU = core; placa = fonte do conector; cooler = a escolhida no Marz.</summary>
    public static string SourceOf(string actuatorId, string coolerSourceId) => DeviceOf(actuatorId) switch
    {
        ThermalDevice.Gpu => GpuSource,
        ThermalDevice.Board => BoardSource(BoardFan(actuatorId)!),
        _ => coolerSourceId,
    };

    /// <summary>
    /// Eixo X do gráfico: líquido varia pouco (20–60 °C de 5 em 5); as demais fontes 20–100 °C de 10 em 10.
    /// Cresce para caber os pontos da curva (um preset com ponto a 75 °C não é cortado no eixo do líquido).
    /// </summary>
    public static (double Min, double Max, double Step) Axis(bool liquid, IEnumerable<double> temps)
    {
        double step = liquid ? 5 : 10, min = 20, max = liquid ? 60 : 100;
        foreach (var t in temps)
        {
            if (!double.IsFinite(t)) continue;
            if (t < min) min = Math.Max(0, Math.Floor(t / step) * step);
            if (t > max) max = Math.Min(liquid ? 110 : 120, Math.Ceiling(t / step) * step);
        }
        return (min, max, step);
    }

    /// <summary>Limites de edição: cooler 2..8 pontos (bomba ≥ <paramref name="pumpMin"/>%); placa = pontos do chip, CPU/bombas nunca 0%.</summary>
    public static CurveLimits Limits(string actuatorId, (double Min, double Max, double Step) axis, int pumpMin, int boardPoints = 4)
    {
        var dev = DeviceOf(actuatorId);
        if (dev == ThermalDevice.Board)
        {
            var fan = BoardFan(actuatorId)!;
            return new CurveLimits(axis.Min, axis.Max, BoardFanOff.Locked(fan) ? BoardLockedMinPercent : 0, boardPoints, boardPoints);
        }
        return new CurveLimits(axis.Min, axis.Max, actuatorId == PumpId ? pumpMin : 0, 2, CoolerMaxPoints);
    }

    /// <summary>CPU e bombas da placa: a curva editada nunca desce abaixo disto (o cadeado do OFF vale também para a curva).</summary>
    public const int BoardLockedMinPercent = 20;

    /// <summary>
    /// Por que a curva deste fan da placa não pode ser editada agora (null = pode). A escrita é a mesma do FULL/OFF
    /// (curva User via Armoury Crate), então só vale em CURVAS, com o fan ligado e sem curva guardada pendente.
    /// </summary>
    public static string? BoardEditBlock(bool available, string? mode, string? pending, bool off, bool savedPending, bool fits) =>
        !available ? "Armoury Crate indisponível"
        : pending != null ? "aplicando um modo"
        : off ? "fan desligado: religue para editar"
        : mode != null ? "edição só no modo CURVAS (a curva mostrada é a do modo ativo)"
        : savedPending ? "a curva guardada antes do FULL/OFF ainda não voltou"
        : !fits ? "curva fora do padrão de 4 pontos (fixa ou do Fan Xpert): edite no Armoury Crate"
        : null;

    /// <summary>Fans da placa na faixa: todos os conectores, na ordem do Armoury Crate; rpm só dos conferidos.</summary>
    public static IReadOnlyList<StripItem> BoardStrip(IEnumerable<string> acFans, Func<string, string?> nick,
        IReadOnlyDictionary<int, float> rpm, IReadOnlyDictionary<int, float> control, Func<string, bool> isOff) =>
        acFans.Select(f =>
        {
            var r = BoardFanMap.Read(f, rpm, control);
            return new StripItem(BoardHeader(f), nick(f), BoardId(f), r.Rpm, r.Percent, isOff(f), BoardFanOff.Locked(f));
        }).ToList();

    /// <summary>Fans do cooler na faixa: bomba, um item por fan do radiador e o fan embutido.</summary>
    public static IReadOnlyList<StripItem> CoolerStrip(int? pumpRpm, int? pumpPct, IReadOnlyList<int> radRpm, int? radPct, int? embRpm, int? embPct)
    {
        var l = new List<StripItem> { new("BOMBA", null, PumpId, pumpRpm, pumpPct, false, false) };
        if (radRpm.Count == 0) l.Add(new("RADIADOR", null, RadiatorId, null, radPct, false, false));
        for (int i = 0; i < radRpm.Count; i++) l.Add(new($"RAD {i + 1}", null, RadiatorId, radRpm[i], radPct, false, false));
        l.Add(new("EMBUTIDO", null, EmbeddedId, embRpm, embPct, false, false));
        return l;
    }

    /// <summary>Fans da GPU na faixa (todas seguem o mesmo modo do driver/Marz).</summary>
    public static IReadOnlyList<StripItem> GpuStrip(IEnumerable<(uint? Percent, uint? Rpm)> fans) =>
        fans.Select((f, i) => new StripItem($"FAN {i + 1}", null, GpuId, f.Rpm is uint r ? (int)r : null, f.Percent is uint p ? (int)p : null, false, false)).ToList();
}
