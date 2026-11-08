using System.IO;
using System.Text.Json;
using MonitorTray.Core.Detail;
using MonitorTray.Core.History;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Sensors;

namespace MonitorTray.App;

/// <summary>--read-test &lt;json&gt;: one real read-only collection (LHM + PDH), 3 ticks, no UI. Does not write to hardware.</summary>
public static class ReadTest
{
    public static int Run(string output)
    {
        if (File.Exists(output)) { Console.Error.WriteLine("Output already exists."); return 2; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var lhm = new LhmSource();
        lhm.Open();
        var openMs = sw.ElapsedMilliseconds;
        using var sum = new SystemSummarySource();
        sum.Open();
        var tracker = new SensorTracker();
        IReadOnlyList<RawSensor> raw = [];
        var tickMs = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            Thread.Sleep(1000);
            var t0 = sw.ElapsedMilliseconds;
            raw = lhm.Read();
            tickMs.Add(sw.ElapsedMilliseconds - t0);
            tracker.BeginTick();
            foreach (var r in raw) tracker.Push(r.Id, r.Value);
        }
        var s = sum.Sample();
        var devices = DeviceBuilder.Build(raw, tracker, _ => false);
        var (cl, ct, cp) = SummaryPicker.Cpu(raw);
        var (gl, gt, gp) = SummaryPicker.Gpu(raw);
        // Fans: real service, no command at all. With CmpInf open, it must stay read only.
        using var fanSvc = new MonitorTray.Gpu.GpuFanService(statusPath: null);
        fanSvc.Start();
        var fanUntil = DateTime.UtcNow.AddSeconds(5);
        while (fanSvc.State.Reading == null && (fanSvc.State.Error == null || fanSvc.State.Error == MonitorTray.Gpu.FanState.Starting.Error) && DateTime.UtcNow < fanUntil) Thread.Sleep(50);
        var fs = fanSvc.State;
        var fanReport = new
        {
            fs.CanControl, fs.ReadOnlyReason, fs.Error, fs.SafetyNotice, Mode = fs.Mode.ToString(), fs.Holding,
            Gpu = fs.Reading?.Identity.Name, FanCount = fs.Reading?.Identity.FanCount, CoreC = fs.Reading?.TemperatureC,
            Range = fs.Reading?.Telemetry is { } t ? $"{t.MinFanPercent}-{t.MaxFanPercent}" : null,
            Fans = fs.Reading?.Fans.Select(f => $"fan{f.Index} speed={f.SpeedPercent}% target={f.TargetPercent}% policy={f.Policy} rpm={f.SpeedRpm}"),
            Block30 = fs.BlockFor(30, DateTimeOffset.UtcNow), Block100 = fs.BlockFor(100, DateTimeOffset.UtcNow),
        };
        var traffic = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(1000);
            var rr = lhm.Read().Where(r => r.DeviceId == "/net-traffic").Select(r => $"{r.Name}={(r.Value ?? 0) / 1024:0} KB/s");
            traffic.Add(string.Join(" | ", rr));
        }
        var report = new
        {
            Traffic = traffic,
            Fans = fanReport,
            IsAdmin = Platform.Autostart.IsAdmin(),
            LhmOpenMs = openMs, LhmTickMs = tickMs, Entries = raw.Count, lhm.Notice,
            Summary = new { CpuLoadPdh = s.CpuLoad, CpuLoadLhm = cl, CpuTemp = ct, CpuPower = cp, GpuLoad = gl, GpuTemp = gt, GpuPower = gp, s.MemUsed, s.MemTotal, s.DiskRead, s.DiskWrite, s.NetDown, s.NetUp },
            Devices = devices.Select(d => new
            {
                d.Category, d.DisplayName, d.Id, Sensors = d.Sensors.Count,
                WithValue = d.Sensors.Count(x => x.Value != null),
                Hot = DetailModel.HotSensor(d) is { } h ? $"{h.Name} = {h.Value}" : null,
                Fans = d.Sensors.Where(x => x.KindRaw is "Fan" or "Control").Select(x => $"{x.KindRaw} {x.Name} = {x.Value}"),
                Clocks = d.Sensors.Where(x => x.KindRaw == "Clock").Select(x => $"{x.Name} = {x.Value}"),
            }),
        };
        File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
