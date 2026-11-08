using MonitorTray.Core.Format;
using MonitorTray.Core.Keyboard;
using MonitorTray.Core.Model;
using MonitorTray.Gpu;
using MonitorTray.Sensors;

namespace MonitorTray.App;

/// <summary>Valores para as telas do teclado: calculados (calc:*) e qualquer sensor do LHM pelo id. Thread-safe (só lê snapshots imutáveis).</summary>
public sealed class KeyboardValues(SensorService service, Func<FanState?> fans)
{
    private IReadOnlyList<RawSensor>? indexedFrom;
    private Dictionary<string, RawSensor> index = new(StringComparer.Ordinal);

    private Dictionary<string, RawSensor> Index()
    {
        var raw = service.LastRaw;
        if (!ReferenceEquals(raw, indexedFrom))
        {
            var d = new Dictionary<string, RawSensor>(StringComparer.Ordinal);
            foreach (var r in raw) d[r.Id] = r;
            index = d; indexedFrom = raw;
        }
        return index;
    }

    public double? Value(string id)
    {
        var s = service.Latest?.Summary;
        var f = fans()?.Reading;
        const double GiB = 1024d * 1024 * 1024, MiB = 1024d * 1024;
        switch (id)
        {
            case "": return null;
            case "calc:cpu-load": return s?.CpuLoad;
            case "calc:cpu-temp": return s?.CpuTemp;
            case "calc:cpu-power": return s?.CpuPower;
            case "calc:cpu-clock-max": return CalculatedSensors.CoreClocksGHz(service.LastRaw).Max;
            case "calc:cpu-clock-avg": return CalculatedSensors.CoreClocksGHz(service.LastRaw).Avg;
            case "calc:gpu-load": return s?.GpuLoad ?? f?.Telemetry.GpuUtilPercent;
            case "calc:gpu-temp": return s?.GpuTemp ?? f?.TemperatureC;
            case "calc:gpu-power": return s?.GpuPower ?? f?.Telemetry.PowerW;
            case "calc:gpu-fan": return f?.Fans.Max(x => (double?)x.SpeedPercent);
            case "calc:mem-used": return s is { MemTotal: > 0 } ? s.MemUsed / GiB : null;
            case "calc:mem-load": return s is { MemTotal: > 0 } ? 100.0 * s.MemUsed / s.MemTotal : null;
            case "calc:disk-read": return s?.DiskReadBps / MiB;
            case "calc:disk-write": return s?.DiskWriteBps / MiB;
            case "calc:net-down": return s?.NetDownBps / (Formatter.NetBits ? 125000.0 : MiB);
            case "calc:net-up": return s?.NetUpBps / (Formatter.NetBits ? 125000.0 : MiB);
        }
        return Index().TryGetValue(id, out var r) ? r.Value : null;
    }

    public string Name(string id)
    {
        var c = CalculatedSensors.All.FirstOrDefault(x => x.Id == id);
        if (c.Id != null) return c.Name.Split('·').Last().Trim() + " ";
        return Index().TryGetValue(id, out var r) ? r.Name + " " : "? ";
    }

    public string Label(string id)
    {
        if (id.Length == 0) return "(vazia)";
        var c = CalculatedSensors.All.FirstOrDefault(x => x.Id == id);
        if (c.Id != null) return c.Name;
        return Index().TryGetValue(id, out var r) ? $"{r.DeviceName} · {r.Name} ({r.KindRaw})" : id + " (não encontrado)";
    }
}
