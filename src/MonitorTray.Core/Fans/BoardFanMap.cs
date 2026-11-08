namespace MonitorTray.Core.Fans;

/// <summary>Leitura de um card de fan da placa: rpm só quando medido; % de controle da saída do chip.</summary>
public sealed record BoardFanReading(int? Rpm, int? Percent, bool RpmMeasured, int? ChipIndex);

/// <summary>
/// Conector do Armoury Crate → saída do Super I/O (só leitura de rpm/%). Chassis 1..4 conferidos ao vivo (saídas 0, 2, 3, 4);
/// CPU e bombas foram estimados pelo % de controle da coleta (saídas 1, 5, 6) e o rpm deles nunca foi confirmado:
/// o card mostra só o % e "—" no rpm. O rpm nunca é calculado a partir do %.
/// </summary>
public static class BoardFanMap
{
    public static int? ChipIndex(string acName) => acName switch
    {
        "Chassis Fan 1" => 0, "CPU Fan" => 1, "Chassis Fan 2" => 2, "Chassis Fan 3" => 3, "Chassis Fan 4" => 4, "Water Pump+" => 5, "AIO Pump" => 6, _ => null,
    };

    /// <summary>rpm medido e conferido para este conector (só os chassis).</summary>
    public static bool RpmMeasured(string acName) => acName.StartsWith("Chassis Fan ", StringComparison.Ordinal) && ChipIndex(acName) != null;

    public static BoardFanReading Read(string acName, IReadOnlyDictionary<int, float> rpm, IReadOnlyDictionary<int, float> control)
    {
        var i = ChipIndex(acName);
        bool measured = RpmMeasured(acName);
        int? r = measured && i is int a && rpm.TryGetValue(a, out var v) && v >= 0 ? (int)Math.Round(v) : null;
        int? p = i is int b && control.TryGetValue(b, out var c) && c is >= 0 and <= 100 ? (int)Math.Round(c) : null;
        return new BoardFanReading(r, p, measured, i);
    }

    /// <summary>
    /// Índice da saída no fim do id do sensor: "/lpc/nct6798d/0/fan/2" → 2. Tolera o sufixo "#nome" das coletas
    /// exportadas (modo simulado: "/Motherboard/hw-1/fan/2#Fan #3"). null = sem índice.
    /// </summary>
    public static int? TrailingIndex(string sensorId)
    {
        var id = sensorId;
        int hash = id.IndexOf('#');
        if (hash >= 0) id = id[..hash];
        return int.TryParse(id[(id.LastIndexOf('/') + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : null;
    }
}
