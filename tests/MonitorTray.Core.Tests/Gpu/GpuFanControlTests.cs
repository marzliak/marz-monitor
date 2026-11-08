namespace MonitorTray.Core.Tests.Gpu;
using System;
using System.Collections.Generic;
using System.Linq;
using MonitorTray.Gpu;
using Xunit;

public sealed class GpuFanControlTests
{
    private sealed class Fake : IGpuApi
    {
        internal GpuIdentity Identity = new("GPU-test", "NVIDIA fixture", 2);
        internal readonly List<string> Writes = new();
        internal FanReading[] Fans = { new(0, 35, 35, 0), new(1, 36, 36, 0) };
        internal int FailFan = -1;
        internal bool IgnoreWrites, FailRead;
        public IReadOnlyList<GpuIdentity> Discover() => new[] { Identity };
        public GpuReading Read(string uuid) => FailRead ? throw new InvalidOperationException("Read failed") : new(Identity, DateTimeOffset.UtcNow, 40, Fans.ToArray());
        public void SetSpeed(string uuid, int fan, uint percent)
        {
            Writes.Add($"max:{fan}");
            if (fan == FailFan) throw new InvalidOperationException("Write failed");
            if (!IgnoreWrites) Fans[fan] = Fans[fan] with { Policy = 1, TargetPercent = percent };
        }
        public void RestoreAutomatic(string uuid, int fan)
        {
            Writes.Add($"auto:{fan}");
            if (fan == FailFan) throw new InvalidOperationException("Restore failed");
            if (!IgnoreWrites) Fans[fan] = Fans[fan] with { Policy = 0 };
        }
        public void Dispose() { }
    }
    [Fact] public void ReadAndCloseNeverWriteAtStartup()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity);
        c.Read(); c.Close(); Assert.Empty(api.Writes); Assert.False(c.IsHoldingFullSpeed);
    }
    [Fact] public void MaxAndAutoAreVerifiedForEveryFan()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity);
        c.SetFullSpeed(); Assert.True(c.IsHoldingFullSpeed);
        Assert.All(api.Fans, f => { Assert.Equal(1u, f.Policy); Assert.Equal(100u, f.TargetPercent); });
        c.RestoreAutomatic(); Assert.False(c.IsHoldingFullSpeed);
        Assert.Equal(new[] { "max:0", "max:1", "auto:0", "auto:1" }, api.Writes);
    }
    [Fact] public void ChangedIdentityPreventsWrites()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity);
        api.Identity = api.Identity with { Name = "Other GPU" };
        Assert.Throws<InvalidOperationException>(c.SetFullSpeed); Assert.Empty(api.Writes);
    }
    [Fact] public void ChangedFanCountPreventsWrites()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity);
        api.Identity = api.Identity with { FanCount = 3 };
        Assert.Throws<InvalidOperationException>(c.SetFullSpeed); Assert.Empty(api.Writes);
    }
    [Fact] public void InvalidFanLayoutIsRejected()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity);
        api.Fans[1] = api.Fans[1] with { Index = 0 };
        Assert.Throws<InvalidOperationException>(() => c.Read());
    }
    [Fact] public void PartialFailurePreservesSuccessfulFullSpeedAndOwnership()
    {
        var api = new Fake { FailFan = 0 }; var c = new GpuFanController(api, api.Identity);
        Assert.Throws<AggregateException>(c.SetFullSpeed); Assert.True(c.IsHoldingFullSpeed);
        Assert.Equal(100u, api.Fans[1].TargetPercent); Assert.DoesNotContain(api.Writes, w => w.StartsWith("auto"));
        api.FailFan = -1; c.Close(); Assert.False(c.IsHoldingFullSpeed);
    }
    [Fact] public void FailedRestoreTriesEveryFanAndKeepsOwnership()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity); c.SetFullSpeed();
        api.FailFan = 0; Assert.Throws<AggregateException>(c.Close);
        Assert.Contains("auto:1", api.Writes); Assert.True(c.IsHoldingFullSpeed);
    }
    [Fact] public void DriverDroppingManualIsReasserted()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity); c.SetFullSpeed();
        api.Fans[0] = new FanReading(0, 0, 0, 0); c.MaintainFullSpeed();
        Assert.Equal(100u, api.Fans[0].TargetPercent); Assert.Equal(1u, api.Fans[0].Policy);
    }
    [Fact] public void UnownedMaintenanceIsReadOnly()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity); c.MaintainFullSpeed(); Assert.Empty(api.Writes);
    }
    [Fact] public void SuccessfulReturnWithWrongTargetIsNotSuccess()
    {
        var api = new Fake { IgnoreWrites = true }; var c = new GpuFanController(api, api.Identity);
        Assert.Throws<AggregateException>(c.SetFullSpeed); Assert.True(c.IsHoldingFullSpeed);
    }
    [Fact] public void UnknownPolicyNeverConfirmsAutomatic()
    {
        var api = new Fake { IgnoreWrites = true }; api.Fans[0] = api.Fans[0] with { Policy = null };
        var c = new GpuFanController(api, api.Identity); Assert.Throws<AggregateException>(c.RestoreAutomatic);
    }
    [Fact] public void ReadFailureStillAttemptsAllFullTargetsDuringHold()
    {
        var api = new Fake(); var c = new GpuFanController(api, api.Identity); c.SetFullSpeed();
        api.Writes.Clear(); api.FailRead = true; Assert.Throws<AggregateException>(c.MaintainFullSpeed);
        Assert.Equal(new[] { "max:0", "max:1" }, api.Writes); Assert.True(c.IsHoldingFullSpeed);
    }
    [Fact] public void NativeBackendRefusesNonWindowsWithoutLoadingDll()
    {
        if (!OperatingSystem.IsWindows()) Assert.Throws<PlatformNotSupportedException>(() => new NvmlGpuApi());
    }
}
