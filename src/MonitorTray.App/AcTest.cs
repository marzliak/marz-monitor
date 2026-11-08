using System.IO;

namespace MonitorTray.App;

/// <summary>
/// Bench test (admin): MonitorTray.exe --ac-test &lt;log&gt; &lt;AC fan&gt;. Applies FULL, TURBO and CURVES
/// through the real AsusFanModes class, measures RPM (LHM, read only) and checks that no User curve changed.
/// </summary>
internal static class AcTest
{
    public static int Run(string logPath, string fan)
    {
        using var log = new StreamWriter(logPath) { AutoFlush = true };
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logPath))!, "marz-actest"); Directory.CreateDirectory(dir);
        var lhm = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = true }; lhm.Open();
        int idx = fan switch { "Chassis Fan 1" => 0, "Chassis Fan 2" => 2, "Chassis Fan 3" => 3, "Chassis Fan 4" => 4, _ => 3 };
        float? Rpm()
        {
            foreach (var h in lhm.Hardware) { h.Update(); foreach (var s in h.SubHardware) { s.Update(); if (s.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.SuperIO) return s.Sensors.FirstOrDefault(x => x.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan && x.Index == idx)?.Value; } }
            return null;
        }
        dynamic mgr = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("14083c53-b8e7-48e4-9320-811f3478c4a4"))!)!;
        string Snap()
        {
            var sb = new System.Text.StringBuilder(); dynamic cs = mgr.Controls;
            for (int i = 0; i < (int)cs.Count; i++) { dynamic c = cs.Item(i); for (int j = 1; j <= 4; j++) { dynamic cv = c.Profiles.Item(j).FanCurve; sb.Append(c.Name).Append(j).Append(':'); for (int k = 0; k < (int)cv.Count; k++) sb.Append(cv.Item(k).Temperature).Append(',').Append(cv.Item(k).Speed).Append(' '); sb.Append('|'); } }
            return sb.ToString();
        }
        var before = Snap();
        AsusFanModes.Log = s => { lock (log) log.WriteLine("   · " + s); };
        using var m = new AsusFanModes(dir);
        for (int i = 0; i < 50 && !m.Available && m.Error == null; i++) Thread.Sleep(100);
        log.WriteLine($"available={m.Available} error={m.Error} fans={string.Join(",", m.Fans)}");
        log.WriteLine($"start rpm={Rpm():0}");
        foreach (var mode in new[] { "full", "turbo", "curves", "full" })
        {
            m.Apply(mode, [fan]); Thread.Sleep(9000);
            log.WriteLine($"{mode,-7} rpm={Rpm():0} mode={m.Mode ?? "curves"} error={m.Error} snapshot-pending={File.Exists(Path.Combine(dir, "ac-user-curves.json"))}");
        }
        m.RestoreAndWait(TimeSpan.FromSeconds(10)); Thread.Sleep(6000);
        log.WriteLine($"exit (RestoreAndWait) rpm={Rpm():0} snapshot-pending={File.Exists(Path.Combine(dir, "ac-user-curves.json"))}");
        log.WriteLine("curves same as at start: " + (before == Snap()));
        var tr = Path.Combine(dir, "ac-modes.log"); if (File.Exists(tr)) log.Write(File.ReadAllText(tr));
        lhm.Close();
        return 0;
    }
}
