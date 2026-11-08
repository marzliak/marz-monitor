using MonitorTray.Core.Model;

namespace MonitorTray.Core.Normalize;

/// <summary>Campos de CPU/GPU do Resumo a partir das leituras cruas (docs/02 §Mapeamento).</summary>
public static class SummaryPicker
{
    public static bool IsGpuFamily(string family) => family.StartsWith("Gpu", StringComparison.Ordinal);

    private static IEnumerable<RawSensor> FirstDevice(IEnumerable<RawSensor> all, Func<string, bool> family)
    {
        var dev = all.Where(s => family(s.Family)).Select(s => s.DeviceId).Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
        return dev == null ? [] : all.Where(s => s.DeviceId == dev);
    }

    private static float? ByName(IEnumerable<RawSensor> s, string kind, params string[] names)
    {
        foreach (var n in names)
        {
            var hit = s.FirstOrDefault(x => x.KindRaw == kind && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase) && x.Value is float);
            if (hit != null) return hit.Value;
        }
        return null;
    }

    public static (float? Load, float? Temp, float? Power) Cpu(IReadOnlyList<RawSensor> all)
    {
        var cpu = FirstDevice(all, f => f == "Cpu").ToList();
        float? temp = ByName(cpu, "Temperature", "CPU Package", "Core Max");
        temp ??= cpu.Where(x => x.KindRaw == "Temperature" && !Roles.IsMargin(x.Name) && x.Value is float).Select(x => x.Value).Max();
        return (ByName(cpu, "Load", "CPU Total"), temp, Plausible(ByName(cpu, "Power", "CPU Package")));
    }

    public static (float? Load, float? Temp, float? Power) Gpu(IReadOnlyList<RawSensor> all)
    {
        var gpu = FirstDevice(all, IsGpuFamily).ToList();
        return (ByName(gpu, "Load", "GPU Core"), ByName(gpu, "Temperature", "GPU Core", "GPU Hot Spot"), Plausible(ByName(gpu, "Power", "GPU Package", "GPU Power")));
    }

    /// <summary>
    /// Sem admin o LHM devolve 0 W de package (não consegue ler o MSR). Um chip ligado não consome 0 W,
    /// então isso é "sem leitura" ("—"), não um valor. Não inventa nada: só recusa um zero impossível.
    /// </summary>
    internal static float? Plausible(float? watts) => watts is float w && w > 0 ? w : null;
}
