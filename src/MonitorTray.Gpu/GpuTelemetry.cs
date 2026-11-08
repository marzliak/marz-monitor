// Ported from CmpInf (homelab/hosts/nanim/apps/cmpinf/src, homolog 260a683f). Safety logic kept unchanged.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MonitorTray.Gpu;

public sealed record GpuTelemetry
{
    public static readonly GpuTelemetry Empty = new();
    public string? Driver { get; init; }
    public uint? MemoryTemperatureC { get; init; }
    public uint? HotspotC { get; init; }
    /// <summary>"NVML" or "NVAPI" when MemoryTemperatureC is present.</summary>
    public string? MemoryTemperatureSource { get; init; }
    public uint? SlowdownC { get; init; }
    public uint? ShutdownC { get; init; }
    public uint? MinFanPercent { get; init; }
    public uint? MaxFanPercent { get; init; }
    public uint? GpuUtilPercent { get; init; }
    public uint? MemoryUtilPercent { get; init; }
    public ulong? MemoryUsedBytes { get; init; }
    public ulong? MemoryTotalBytes { get; init; }
    public double? PowerW { get; init; }
    public double? PowerLimitW { get; init; }
    public uint? CoreClockMhz { get; init; }
    public uint? MemoryClockMhz { get; init; }
    public ulong? ClockEventReasons { get; init; }
    public bool HasFanRange => MinFanPercent is <= 100 && MaxFanPercent is <= 100 && MinFanPercent <= MaxFanPercent;
    public bool Allows(uint percent) => HasFanRange && percent >= MinFanPercent && percent <= MaxFanPercent;
    public bool ThermalEvent => (ClockEventReasons.GetValueOrDefault() & 0x68UL) != 0;
    public string ClockEvents => ClockEventReasons is not ulong bits ? "N/A — not exposed by driver" : bits == 0 ? "No reason reported"
        : string.Join("; ", EventLabels(bits));
    private static IEnumerable<string> EventLabels(ulong bits)
    {
        var labels = new (ulong Mask, string Text)[] {
            (1,"GPU idle"),(2,"Application clock"),(4,"Power limit"),(8,"Hardware slowdown"),
            (16,"Sync boost"),(32,"SW thermal limit"),(64,"HW thermal limit"),(128,"Power brake"),(256,"Display clock") };
        foreach (var (mask, text) in labels) if ((bits & mask) != 0) yield return text;
        if ((bits & ~0x1ffUL) != 0) yield return $"Other: 0x{bits & ~0x1ffUL:X}";
    }
}

public static class GpuSafety
{
    public static string? ReductionBlock(GpuReading reading, uint percent)
    {
        if (!reading.Telemetry.Allows(percent)) return "Percentage outside the range reported by the driver (or range unavailable).";
        if (reading.Fans.Any(f => f.Policy is not (0 or 1) || f.TargetPercent is null)) return "Policy/target of some fan unavailable.";
        if (reading.TemperatureC is null or 0 or > 150) return "Core temperature unavailable or invalid.";
        if (reading.Telemetry.ClockEventReasons is null) return "Driver throttle reasons unavailable; reduction blocked.";
        uint limit = reading.Telemetry.SlowdownC is > 3 and <= 150 ? Math.Min(80u, reading.Telemetry.SlowdownC.Value - 3) : 80;
        if (reading.TemperatureC >= limit) return $"Core reached the app's conservative limit ({limit} C).";
        if (reading.Telemetry.MemoryTemperatureC >= 95) return "VRAM reached 95 C (app's conservative limit).";
        if (reading.Telemetry.ThermalEvent) return "Driver reports thermal throttling.";
        if (percent < 30 && reading.TemperatureC >= 50) return "Below 30% requires core below 50 C.";
        return null;
    }
}
