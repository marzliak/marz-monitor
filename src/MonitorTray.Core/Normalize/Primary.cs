using System.Text.RegularExpressions;

namespace MonitorTray.Core.Normalize;

/// <summary>Primary sensor of a set (docs/06 §4).</summary>
public static class Primary
{
    private static readonly Regex Excluded = new(@"distance|tjmax", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex[] Canon =
    [
        new(@"^physical", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"total", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"package", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // Deliberate deviation from the prototype: there "hot spot|composite" comes before "core$" and the GPU would pick
        // Hot Spot, contradicting the expected cases in docs/06 §4 (GPU Temp → "GPU Core"). The doc wins.
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
