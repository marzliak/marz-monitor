using System.Text.RegularExpressions;

namespace MonitorTray.Core.Normalize;

/// <summary>Sensor principal de um conjunto (docs/06 §4).</summary>
public static class Primary
{
    private static readonly Regex Excluded = new(@"distance|tjmax", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex[] Canon =
    [
        new(@"^física", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"total", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"package", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Desvio consciente do protótipo: lá "hot spot|composite" vem antes de "core$" e a GPU escolheria
        // Hot Spot, contrariando os casos esperados de docs/06 §4 (GPU Temp → "GPU Core"). Vale o doc.
        new(@"composite", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"(^|\s)core$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"hot spot", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"core max", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public static T? Pick<T>(IReadOnlyList<T> list, Func<T, string> name, Func<T, float?> value) where T : class
    {
        if (list.Count == 0) return null;
        var pool = list.Where(x => !Excluded.IsMatch(name(x))).ToList();
        if (pool.Count == 0) pool = list.ToList();
        foreach (var re in Canon)
        {
            var hit = pool.FirstOrDefault(x => re.IsMatch(name(x)));
            if (hit != null) return hit;
        }
        return pool.OrderByDescending(x => value(x) ?? float.MinValue).First();
    }
}
