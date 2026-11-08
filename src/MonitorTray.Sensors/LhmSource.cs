using LibreHardwareMonitor.Hardware;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Sensors;

/// <summary>LibreHardwareMonitorLib: varredura recursiva, IDs estáveis, hot-plug.</summary>
public sealed class LhmSource : IHardwareSource
{
    private Computer? computer;
    private readonly object gate = new();
    private DateTime lastRescan = DateTime.UtcNow;
    private HashSet<string> realNics = new(StringComparer.Ordinal);
    private DateTime nicsAt = DateTime.MinValue;

    /// <summary>A varredura completa periódica (Reset do LHM, ~1 s) só roda com o Detalhes aberto.</summary>
    public bool RescanAllowed { get; set; }
    public bool IsSimulated => false;
    public string? Notice { get; private set; }
    public event Action? HardwareChanged;
    private readonly NetTraffic traffic = new();

    public void Open()
    {
        lock (gate)
        {
            computer = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true,
                IsControllerEnabled = true, IsNetworkEnabled = true, IsStorageEnabled = true, IsPsuEnabled = true, IsBatteryEnabled = true,
            };
            computer.HardwareAdded += _ => HardwareChanged?.Invoke();
            computer.HardwareRemoved += _ => HardwareChanged?.Invoke();
            try { computer.Open(); }
            catch (Exception ex) { Notice = "Falha ao abrir sensores: " + ex.Message; }
            traffic.Start();
        }
    }

    public IReadOnlyList<RawSensor> Read()
    {
        lock (gate)
        {
            if (computer == null) return [];
            // Varredura completa periódica cobre dispositivos USB que o LHM não notifica.
            if (RescanAllowed && DateTime.UtcNow - lastRescan > TimeSpan.FromSeconds(30))
            {
                lastRescan = DateTime.UtcNow;
                try
                {
                    computer.Reset();
                    // Logo após o Reset os sensores voltam sem valor por uma leitura; descarta essa leitura
                    // para não publicar um tick de "—"/zeros que faria as caixinhas caírem e voltarem.
                    var warm = new List<RawSensor>(400);
                    foreach (var h in computer.Hardware) Walk(h, warm);
                }
                catch (Exception) { }
            }
            if (DateTime.UtcNow - nicsAt > TimeSpan.FromSeconds(30)) { realNics = NetworkFilter.RealNames(); nicsAt = DateTime.UtcNow; }
            var list = new List<RawSensor>(400);
            try
            {
                foreach (var h in computer.Hardware)
                {
                    // Interfaces de filtro/WAN Miniport viram "dispositivos" no LHM; só os adaptadores reais entram.
                    if (h.HardwareType == HardwareType.Network && !realNics.Contains(h.Name)) continue;
                    Walk(h, list);
                }
            }
            catch (InvalidOperationException) { }
            traffic.AppendSensors(list);
            return list;
        }
    }

    private static void Walk(IHardware h, List<RawSensor> list)
    {
        try { h.Update(); } catch (Exception) { }
        var devId = h.Identifier.ToString();
        var family = h.HardwareType.ToString();
        try
        {
            foreach (var s in h.Sensors)
            {
                var kind = s.SensorType.ToString();
                float? v = s.Value;
                list.Add(new RawSensor(s.Identifier.ToString(), devId, h.Name, family, s.Name, kind, Units.ForLhmType(kind), v));
            }
        }
        catch (InvalidOperationException) { }
        try { foreach (var c in h.SubHardware) Walk(c, list); } catch (InvalidOperationException) { }
    }

    public void Reset() { lock (gate) { try { computer?.Reset(); } catch (Exception) { } } }

    public void Dispose() { lock (gate) { traffic.Dispose(); try { computer?.Close(); } catch (Exception) { } computer = null; } }
}
