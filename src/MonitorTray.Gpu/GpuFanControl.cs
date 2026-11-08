// Ported from CmpInf (homelab/hosts/nanim/apps/cmpinf/src, homolog 260a683f). Safety logic kept unchanged.
using System;
using System.Collections.Generic;
using System.Linq;

namespace MonitorTray.Gpu;

public sealed record GpuIdentity(string Uuid, string Name, int FanCount);
public sealed record FanReading(int Index, uint? SpeedPercent, uint? TargetPercent, uint? Policy)
{
    public uint? SpeedRpm { get; init; }
}
public sealed record GpuReading(GpuIdentity Identity, DateTimeOffset CapturedAtUtc, uint? TemperatureC, IReadOnlyList<FanReading> Fans)
{
    public GpuTelemetry Telemetry { get; init; } = GpuTelemetry.Empty;
}
public interface IGpuApi : IDisposable
{
    IReadOnlyList<GpuIdentity> Discover();
    GpuReading Read(string uuid);
    void SetSpeed(string uuid, int fan, uint percent);
    void RestoreAutomatic(string uuid, int fan);
}

public sealed class GpuFanController
{
    private readonly IGpuApi api;
    private readonly GpuIdentity identity;
    private readonly object gate = new();
    private bool holding;
    private uint requested = 100;
    internal bool IsHoldingManual { get { lock (gate) return holding; } }
    internal bool IsHoldingFullSpeed { get { lock (gate) return holding && requested == 100; } }
    internal uint? RequestedPercent { get { lock (gate) return holding ? requested : null; } }
    internal string? SafetyNotice { get; private set; }
    internal GpuFanController(IGpuApi api, GpuIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Uuid) || string.IsNullOrWhiteSpace(identity.Name) || identity.FanCount is < 1 or > 32)
            throw new ArgumentException("Invalid identity/fan count.");
        this.api = api; this.identity = identity;
    }
    private void ValidateIdentity()
    {
        var matches = api.Discover().Where(g => g.Uuid == identity.Uuid).ToArray();
        if (matches.Length != 1 || matches[0] != identity) throw new InvalidOperationException("Identity or fan count changed; write refused.");
    }
    internal GpuReading Read()
    {
        lock (gate)
        {
            ValidateIdentity();
            var r = api.Read(identity.Uuid);
            if (r.Identity != identity || r.Fans.Count != identity.FanCount || !r.Fans.Select(f => f.Index).Order().SequenceEqual(Enumerable.Range(0, identity.FanCount)))
                throw new InvalidOperationException("Reading does not match the expected GPU/fans.");
            if (r.CapturedAtUtc > DateTimeOffset.UtcNow.AddSeconds(1) || DateTimeOffset.UtcNow - r.CapturedAtUtc > TimeSpan.FromSeconds(6))
                throw new InvalidOperationException("NVML reading stale or in the future.");
            return r;
        }
    }
    internal void SetFullSpeed() => SetSpeed(100);
    internal void SetSpeed(uint percent)
    {
        lock (gate)
        {
            if (percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            ValidateIdentity();
            if (percent < 100)
            {
                var block = GpuSafety.ReductionBlock(Read(), percent);
                if (block != null) throw new InvalidOperationException(block);
            }
            holding = true; requested = percent; SafetyNotice = null;
            try { Apply(percent); }
            catch (Exception ex)
            {
                // A partially reduced/unverified set must not be maintained.
                if (percent < 100)
                {
                    requested = 100; SafetyNotice = "Preset not confirmed; protection raised the target to 100%.";
                    try { Apply(100); } catch (Exception failHigh) { throw new AggregateException(SafetyNotice, ex, failHigh); }
                }
                throw;
            }
        }
    }
    internal void MaintainFullSpeed() => Maintain(); // Compatibility with previous callers.
    internal void Maintain()
    {
        lock (gate)
        {
            if (!holding) return;
            if (requested < 100)
            {
                string? block;
                try { block = GpuSafety.ReductionBlock(Read(), requested); }
                catch (Exception ex) { block = "Telemetry failure: " + ex.Message; }
                if (block != null) { requested = 100; SafetyNotice = "PROTECTION 100%: " + block; }
            }
            try { Apply(requested); }
            catch (Exception ex)
            {
                if (requested < 100)
                {
                    requested = 100; SafetyNotice = "PROTECTION 100%: manual target not confirmed.";
                    try { Apply(100); } catch (Exception failHigh) { throw new AggregateException(SafetyNotice, ex, failHigh); }
                }
                throw;
            }
        }
    }
    private void Apply(uint? percent)
    {
        ValidateIdentity();
        holding = true; // Before any potentially mutating command, including auto.
        var errors = new List<Exception>();
        for (int fan = 0; fan < identity.FanCount; fan++)
        {
            try
            {
                ValidateIdentity();
                if (percent is uint value) api.SetSpeed(identity.Uuid, fan, value);
                else api.RestoreAutomatic(identity.Uuid, fan);
            }
            catch (Exception ex) { errors.Add(new InvalidOperationException($"Fan {fan}: {ex.Message}", ex)); }
        }
        try
        {
            var result = Read();
            foreach (var fan in result.Fans)
                if (fan.Policy != (percent.HasValue ? 1u : 0u) || (percent.HasValue && fan.TargetPercent != percent))
                    errors.Add(new InvalidOperationException($"Fan {fan.Index}: policy/target not confirmed."));
        }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("GPU command not confirmed; check each fan.", errors);
    }
    internal void RestoreAutomatic()
    {
        lock (gate)
        {
            ValidateIdentity(); requested = 100; // A failed restore never resumes a lower target.
            try { Apply(null); }
            catch (Exception original)
            {
                SafetyNotice = "Automatic not confirmed; trying 100% protection on all fans.";
                try { Apply(100); }
                catch (Exception failHigh)
                {
                    SafetyNotice = "Automatic and 100% protection not confirmed; check each fan.";
                    throw new AggregateException(SafetyNotice, original, failHigh);
                }
                SafetyNotice = "Automatic failed; 100% policy/target confirmed per fan. Still in control; try automatic again.";
                throw new AggregateException(SafetyNotice, original);
            }
            holding = false; SafetyNotice = null;
        }
    }
    internal void Close() { lock (gate) { if (holding) RestoreAutomatic(); } }
}
