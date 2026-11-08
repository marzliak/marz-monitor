using LibreHardwareMonitor.Hardware;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Sensors;

/// <summary>LibreHardwareMonitorLib: recursive scan, stable IDs, hot-plug.</summary>
public sealed class LhmSource : IHardwareSource
{
    private Computer? computer;
    private readonly object gate = new();
    private DateTime lastRescan = DateTime.UtcNow;
    private HashSet<string> realNics = new(StringComparer.Ordinal);
    private DateTime nicsAt = DateTime.MinValue;

    /// <summary>The periodic full scan (LHM Reset, ~1 s) only runs while Details is open.</summary>
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
            catch (Exception ex) { Notice = "Failed to open sensors: " + ex.Message; }
            traffic.Start();
        }
    }

    public IReadOnlyList<RawSensor> Read()
    {
        lock (gate)
        {
            if (computer == null) return [];
            // The periodic full scan catches USB devices that LHM does not report.
            if (RescanAllowed && DateTime.UtcNow - lastRescan > TimeSpan.FromSeconds(30))
            {
                lastRescan = DateTime.UtcNow;
                try
                {
                    computer.Reset();
                    // Right after Reset the sensors come back without a value for one read; discard that read
                    // so we don't publish a tick of "—"/zeros that would make the tiles drop and come back.
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
                    // Filter/WAN Miniport interfaces show up as "devices" in LHM; only real adapters get in.
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
