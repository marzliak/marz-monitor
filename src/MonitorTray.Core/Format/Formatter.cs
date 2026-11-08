using System.Globalization;

namespace MonitorTray.Core.Format;

public readonly record struct Formatted(string Value, string Unit)
{
    public override string ToString() => Unit.Length == 0 ? Value : Value + " " + Unit;
}

/// <summary>Formatação de unidades (docs/05 §9). Separador decimal sempre ponto.</summary>
public static class Formatter
{
    public const string Dash = "\u2014";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Janela de histórico: 60 → "1 min", 3600 → "1 h".</summary>
    public static string Window(int seconds) => seconds >= 3600 && seconds % 3600 == 0 ? seconds / 3600 + " h" : seconds >= 60 ? seconds / 60 + " min" : seconds + " s";

    /// <summary>Preferência global: taxas de rede/B/s em bits por segundo (Kbps/Mbps/Gbps, base 1000).</summary>
    public static bool NetBits { get; set; }

    /// <summary>bytes/s → Kbps (inteiro) / Mbps (1 casa) / Gbps (2 casas), base 1000 como operadoras e speedtests.</summary>
    public static Formatted Bits(double bytesPerSecond)
    {
        var b = Math.Max(0, bytesPerSecond) * 8;
        return b >= 1e9 ? new(D(b / 1e9, 2), "Gbps") : b >= 1e6 ? new(D(b / 1e6, 1), "Mbps") : new(D(b / 1e3, 0), "Kbps");
    }

    private static string D(double v, int n) => Math.Round(v, n, MidpointRounding.AwayFromZero).ToString("F" + n, Inv);

    public static Formatted Format(double? value, string unit)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v)) return new(Dash, "");
        return unit switch
        {
            "degC" => new(D(v, 1), "°C"),
            "%" => new(D(v, Math.Abs(v) < 10 ? 1 : 0), "%"),
            "B/s" when NetBits => Bits(v),
            "B/s" => v >= 1048576 ? new(D(v / 1048576, 1), "MB/s") : v >= 1024 ? new(D(v / 1024, 0), "KB/s") : new(D(v, 0), "B/s"),
            "MiB" => v >= 1024 ? new(D(v / 1024, 1), "GB") : new(D(v, 0), "MB"),
            "GiB" => v >= 1024 ? new(D(v / 1024, 2), "TB") : new(D(v, 1), "GB"),
            "MHz" => v >= 1000 ? new(D(v / 1000, 2), "GHz") : new(D(v, 0), "MHz"),
            "V" => new(D(v, 3), "V"),
            "A" => new(D(v, 2), "A"),
            "W" => new(D(v, 1), "W"),
            "RPM" => new(D(v, 0), "RPM"),
            "L/h" => new(D(v, 1), "L/h"),
            _ => new(Math.Abs(v) >= 100 ? D(v, 0) : Trim(v), unit ?? ""),
        };
    }

    private static string Trim(double v)
    {
        var s = Math.Round(v, 2, MidpointRounding.AwayFromZero).ToString("0.##", Inv);
        return s == "-0" ? "0" : s;
    }

    /// <summary>Valor curto das células da grade (docs/06 §6).</summary>
    public static string Short(double? value, string unit)
    {
        if (value is not double v || double.IsNaN(v)) return Dash;
        return unit switch
        {
            "MHz" => D(v / 1000, 1),
            "V" => D(v, 2),
            _ => D(Math.Round(v, MidpointRounding.AwayFromZero), 0),
        };
    }

    /// <summary>Taxa de rede do Resumo: KB/s inteiro abaixo de 1024 KB/s, senão MB/s com 1 casa (base 1024).</summary>
    public static Formatted Net(double bytesPerSecond)
    {
        if (NetBits) return Bits(bytesPerSecond);
        var kb = Math.Max(0, bytesPerSecond) / 1024;
        return kb >= 1024 ? new(D(kb / 1024, 1), "MB/s") : new(D(Math.Round(kb, MidpointRounding.AwayFromZero), 0), "KB/s");
    }

    /// <summary>Disco no Resumo: sempre MB/s com 1 casa (base 1024).</summary>
    public static string DiskMBps(double bytesPerSecond) => D(Math.Max(0, bytesPerSecond) / 1048576, 1);

    /// <summary>"418/931 GB" ou "1.20/1.82 TB" (base 1024, como o Explorer). A unidade segue o total.</summary>
    public static string UsedTotal(ulong used, ulong total)
    {
        const double GiB = 1024d * 1024 * 1024, TiB = GiB * 1024;
        return total >= TiB
            ? $"{D(used / TiB, 2)}/{D(total / TiB, 2)} TB"
            : $"{D(used / GiB, 0)}/{D(total / GiB, 0)} GB";
    }

    /// <summary>Tira fabricante/marca do nome do driver: "NVIDIA GeForce RTX 5090" → "RTX 5090". Sem modelos fixos.</summary>
    public static string ShortGpuName(string name)
    {
        var n = System.Text.RegularExpressions.Regex.Replace(name.Trim(), @"^(NVIDIA|AMD|Intel\(R\)|Intel)\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        n = System.Text.RegularExpressions.Regex.Replace(n, @"^(GeForce|Radeon\(TM\)|Radeon|Arc\(TM\)|Arc)\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return n.Length == 0 ? name : n;
    }

    public static string RefreshLabel(int ms) => (ms / 1000.0).ToString("0.#", Inv) + "s";

    /// <summary>Fans da GPU no popup: "52% 1240" por fan (rpm só quando &gt; 0), fans separadas por dois espaços; sem % = "—".</summary>
    public static string GpuFans(IEnumerable<(uint? Percent, uint? Rpm)> fans) =>
        string.Join("  ", fans.Select(f => (f.Percent is uint p ? p.ToString(Inv) + "%" : Dash) + (f.Rpm is uint r and > 0 ? " " + r.ToString(Inv) : "")));
}
