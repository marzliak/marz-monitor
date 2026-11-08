using System.Management;
using MonitorTray.Core.Model;

namespace MonitorTray.Sensors;

/// <summary>
/// Ready fixed/removable logical drives, with the physical disk model (WMI: DiskDrive → Partition → LogicalDisk).
/// Space comes from DriveInfo on every read (cheap); the letter→model map is rebuilt every 60 s (WMI is slow).
/// Read-only. No hardcoded hardware names: the model comes from the system.
/// </summary>
public sealed class Volumes
{
    private Dictionary<string, string> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (System.Diagnostics.PerformanceCounter R, System.Diagnostics.PerformanceCounter W)?> counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Read/write per drive (PDH LogicalDisk), cheap; called every tick while the popup is open.</summary>
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
