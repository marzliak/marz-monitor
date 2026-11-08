using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using MonitorTray.Core.Model;

namespace MonitorTray.Sensors;

/// <summary>Source of full hardware readings (LHM or simulated).</summary>
public interface IHardwareSource : IDisposable
{
    void Open();
    IReadOnlyList<RawSensor> Read();
    bool IsSimulated { get; }
    string? Notice { get; }
}

/// <summary>Cheap Summary readings: PDH (CPU, disk), GlobalMemoryStatusEx, network via NetworkInterface.</summary>
public interface ISummarySource : IDisposable
{
    void Open();
    (float? CpuLoad, ulong MemUsed, ulong MemTotal, double DiskRead, double DiskWrite, double NetDown, double NetUp) Sample();
}

public sealed class SystemSummarySource : ISummarySource
{
    private PerformanceCounter? cpu, diskRead, diskWrite;
    private readonly NetworkRate net = new();

    public void Open()
    {
        cpu = TryCounter("Processor Information", "% Processor Utility", "_Total") ?? TryCounter("Processor", "% Processor Time", "_Total");
        diskRead = TryCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        diskWrite = TryCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        net.Sample();
    }

    private static PerformanceCounter? TryCounter(string cat, string name, string inst)
    {
        try { var c = new PerformanceCounter(cat, name, inst, readOnly: true); c.NextValue(); return c; }
        catch (Exception) { return null; }
    }

    private static float? Next(PerformanceCounter? c)
    {
        if (c == null) return null;
        try { var v = c.NextValue(); return float.IsFinite(v) ? v : null; } catch (Exception) { return null; }
    }

    public (float? CpuLoad, ulong MemUsed, ulong MemTotal, double DiskRead, double DiskWrite, double NetDown, double NetUp) Sample()
    {
        var load = Next(cpu);
        if (load is float l) load = Math.Clamp(l, 0, 100);
        var (used, total) = MemoryProvider.Sample();
        var (down, up) = net.Sample();
        return (load, used, total, Next(diskRead) ?? 0, Next(diskWrite) ?? 0, down, up);
    }

    /// <summary>Reopens the PDH counters (they become invalid after hibernation).</summary>
    public void Reset() { Dispose(); Open(); }

    public void Dispose()
    {
        cpu?.Dispose(); diskRead?.Dispose(); diskWrite?.Dispose();
        cpu = diskRead = diskWrite = null;
    }
}

public static class MemoryProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static (ulong Used, ulong Total) Sample()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys) : (0, 0);
    }
}

/// <summary>Σ bytes of Up adapters (not loopback/tunnel) ÷ real Δt (docs/06 §9).</summary>
public sealed class NetworkRate
{
    private readonly Stopwatch sw = new();
    private Dictionary<string, (long Rx, long Tx)> prev = new();
    /// <summary>Adapter list cached for 10 s: enumerating adapters costs ~6 ms; reading their counters is ~free.</summary>
    private List<System.Net.NetworkInformation.NetworkInterface> nics = [];
    private readonly Stopwatch nicsAge = new();
    public static readonly TimeSpan NicRefresh = TimeSpan.FromSeconds(10);

    public (double Down, double Up) Sample()
    {
        if (!nicsAge.IsRunning || nicsAge.Elapsed >= NicRefresh) { nics = NetworkFilter.Real(upOnly: true).ToList(); nicsAge.Restart(); }
        var now = new Dictionary<string, (long, long)>();
        foreach (var ni in nics)
        {
            try { var s = ni.GetIPv4Statistics(); now[ni.Id] = (s.BytesReceived, s.BytesSent); } catch (Exception) { }
        }
        var dt = sw.Elapsed.TotalSeconds;
        sw.Restart();
        double down = 0, up = 0;
        if (dt > 0 && prev.Count > 0)
        {
            foreach (var (id, (rx, tx)) in now)
            {
                if (!prev.TryGetValue(id, out var p)) continue; // new adapter: resync
                var dr = rx - p.Rx; var dtx = tx - p.Tx;
                if (dr > 0) down += dr / dt;
                if (dtx > 0) up += dtx / dt;
            }
        }
        prev = now;
        return (down, up);
    }
}
