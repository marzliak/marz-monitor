namespace MonitorTray.Gpu;

/// <summary>Fake GPU for --simulate/--preview: 2 fans, range 30–100%, no hardware.</summary>
public sealed class SimulatedGpuApi : IGpuApi
{
    private readonly GpuIdentity id = new("GPU-simulated-0000", "Simulated GPU", 2);
    private readonly FanReading[] fans = [new(0, 0, 0, 0) { SpeedRpm = 0 }, new(1, 0, 0, 0) { SpeedRpm = 0 }];
    private readonly Random rng = new(7);
    public IReadOnlyList<GpuIdentity> Discover() => [id];
    public GpuReading Read(string uuid)
    {
        for (int i = 0; i < fans.Length; i++)
        {
            var f = fans[i];
            uint speed = f.Policy == 1 ? f.TargetPercent ?? 0 : 0;
            fans[i] = f with { SpeedPercent = speed, SpeedRpm = speed == 0 ? 0 : (uint)(speed * 30 + rng.Next(-20, 20)) };
        }
        return new GpuReading(id, DateTimeOffset.UtcNow, (uint)(40 + rng.Next(0, 3)), fans.ToArray())
        { Telemetry = new GpuTelemetry { MinFanPercent = 30, MaxFanPercent = 100, SlowdownC = 90, ClockEventReasons = 1, PowerW = 42 } };
    }
    public void SetSpeed(string uuid, int fan, uint percent) => fans[fan] = fans[fan] with { Policy = 1, TargetPercent = percent };
    public void RestoreAutomatic(string uuid, int fan) => fans[fan] = fans[fan] with { Policy = 0, TargetPercent = 0 };
    public void Dispose() { }
}
