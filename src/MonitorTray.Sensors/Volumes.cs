using System.Management;
using MonitorTray.Core.Model;

namespace MonitorTray.Sensors;

/// <summary>
/// Unidades lógicas fixas/removíveis prontas, com o modelo do disco físico (WMI: DiskDrive → Partition → LogicalDisk).
/// Espaço vem de DriveInfo a cada leitura (barato); o mapa letra→modelo é refeito a cada 60 s (WMI é lento).
/// Somente leitura. Nenhum nome de hardware fixo: o modelo vem do sistema.
/// </summary>
public sealed class Volumes
{
    private Dictionary<string, string> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (System.Diagnostics.PerformanceCounter R, System.Diagnostics.PerformanceCounter W)?> counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Leitura/escrita por unidade (PDH LogicalDisk), barato; chamado a cada tick com o popup aberto.</summary>
    public IReadOnlyList<VolumeInfo> WithRates(IReadOnlyList<VolumeInfo> vols)
    {
        var list = new List<VolumeInfo>(vols.Count);
        foreach (var v in vols)
        {
            if (!counters.TryGetValue(v.Letter, out var c))
            {
                try
                {
                    var r = new System.Diagnostics.PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", v.Letter, true);
                    var w = new System.Diagnostics.PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", v.Letter, true);
                    r.NextValue(); w.NextValue(); c = (r, w);
                }
                catch (Exception) { c = null; }
                counters[v.Letter] = c;
            }
            double? rd = null, wr = null;
            if (c is { } k) { try { rd = k.R.NextValue(); wr = k.W.NextValue(); } catch (Exception) { counters.Remove(v.Letter); } }
            list.Add(v with { ReadBps = rd, WriteBps = wr });
        }
        return list;
    }
    private DateTime mappedAt = DateTime.MinValue;

    public IReadOnlyList<VolumeInfo> Read()
    {
        if (DateTime.UtcNow - mappedAt > TimeSpan.FromSeconds(60)) { models = Map(); mappedAt = DateTime.UtcNow; }
        var list = new List<VolumeInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\');
                list.Add(new VolumeInfo(letter, d.VolumeLabel, models.GetValueOrDefault(letter, ""), (ulong)d.TotalSize, (ulong)d.TotalFreeSpace));
            }
            catch (Exception) { }
        }
        return list.OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<string, string> Map()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            foreach (ManagementObject disk in disks.Get())
            {
                using (disk)
                {
                    var model = (disk["Model"] as string ?? "").Trim();
                    foreach (ManagementObject part in disk.GetRelated("Win32_DiskPartition"))
                        using (part)
                            foreach (ManagementObject ld in part.GetRelated("Win32_LogicalDisk"))
                                using (ld) if (ld["DeviceID"] is string id) map[id] = model;
                }
            }
        }
        catch (Exception) { }
        return map;
    }
}
