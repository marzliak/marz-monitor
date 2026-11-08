using MonitorTray.Gpu;
using Xunit;

namespace MonitorTray.Core.Tests.Gpu;

public sealed class GpuFanServiceTests
{
    private sealed class Api : IGpuApi
    {
        public GpuIdentity Identity = new("GPU-svc", "NVIDIA fixture", 2);
        public FanReading[] Fans = [new(0, 30, 30, 0), new(1, 30, 30, 0)];
        public readonly List<string> Writes = [];
        public GpuTelemetry Telemetry = new() { MinFanPercent = 30, MaxFanPercent = 100, SlowdownC = 95, ClockEventReasons = 0 };
        public uint? Temp = 42;
        public IReadOnlyList<GpuIdentity> Discover() => [Identity];
        public GpuReading Read(string uuid) => new(Identity, DateTimeOffset.UtcNow, Temp, Fans.ToArray()) { Telemetry = Telemetry };
        public void SetSpeed(string uuid, int fan, uint percent) { Writes.Add($"{fan}={percent}"); Fans[fan] = Fans[fan] with { Policy = 1, TargetPercent = percent }; }
        public void RestoreAutomatic(string uuid, int fan) { Writes.Add($"{fan}=auto"); Fans[fan] = Fans[fan] with { Policy = 0 }; }
        public void Dispose() { }
    }

    private static FanState WaitFor(GpuFanService s, Func<FanState, bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until) { if (cond(s.State)) return s.State; Thread.Sleep(20); }
        return s.State;
    }

    [Fact]
    public void StartupIsReadOnlyAndManualStateIsNotAssumed()
    {
        var api = new Api(); api.Fans[0] = api.Fans[0] with { Policy = 1 };
        using var s = new GpuFanService(() => api, takeMutex: false);
        s.Start();
        var st = WaitFor(s, x => x.Reading != null);
        Assert.Empty(api.Writes);
        Assert.False(st.Holding);
        Assert.Equal(FanMode.Mixed, st.Mode);
        Assert.Contains("not taken over", st.SafetyNotice);
    }

    [Fact]
    public async Task CommandVerifiesAndExitRestoresAutomatic()
    {
        var api = new Api();
        var s = new GpuFanService(() => api, takeMutex: false);
        s.Start();
        WaitFor(s, x => x.Reading != null);
        await s.CommandAsync(50);
        Assert.True(s.State.Holding); Assert.Equal(50u, s.State.RequestedPercent); Assert.Equal(FanMode.Manual, s.State.Mode);
        Assert.True(s.TryClose(TimeSpan.FromSeconds(5)));
        Assert.All(api.Fans, f => Assert.Equal(0u, f.Policy));
        Assert.Equal(["0=50", "1=50", "0=auto", "1=auto"], api.Writes);
        s.Dispose();
    }

    [Fact]
    public void UnsafeReductionIsBlockedButFullIsAllowed()
    {
        var api = new Api { Temp = 85 };
        using var s = new GpuFanService(() => api, takeMutex: false);
        s.Start();
        var st = WaitFor(s, x => x.Reading != null);
        Assert.NotNull(st.BlockFor(50, DateTimeOffset.UtcNow));
        Assert.Null(st.BlockFor(100, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task WhenAnotherProcessHoldsTheMutexEverythingIsReadOnly()
    {
        // Simulates CmpInf holding the mutex on another thread (mutexes are per thread).
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var name = GpuFanService.MutexName;
        // If the installed app is running it already owns the mutex: the "another process holds it" scenario stays valid; only release if this thread got ownership.
        var t = new Thread(() =>
        {
            using var m = new Mutex(false, name);
            bool owned; try { owned = m.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            held.Set(); release.Wait();
            if (owned) m.ReleaseMutex();
        }) { IsBackground = true };
        t.Start(); held.Wait();
        try
        {
            var api = new Api();
            using var s = new GpuFanService(() => api);
            s.Start();
            var st = WaitFor(s, x => x.Reading != null);
            Assert.False(st.CanControl);
            Assert.NotNull(st.BlockFor(100, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.CommandAsync(100));
            Assert.Empty(api.Writes);
        }
        finally { release.Set(); t.Join(); }
    }
}
