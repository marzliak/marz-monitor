// Portado de CmpInf (homelab/hosts/nanim/apps/cmpinf/src, homolog 260a683f). Lógica de segurança mantida sem alterações.
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
    public string ClockEvents => ClockEventReasons is not ulong bits ? "N/D — driver nao expos" : bits == 0 ? "Nenhum motivo reportado"
        : string.Join("; ", EventLabels(bits));
    private static IEnumerable<string> EventLabels(ulong bits)
    {
        var labels = new (ulong Mask, string Text)[] {
            (1,"GPU ociosa"),(2,"Clock de aplicacao"),(4,"Limite de potencia"),(8,"Slowdown hardware"),
            (16,"Sync boost"),(32,"Limite termico SW"),(64,"Limite termico HW"),(128,"Power brake"),(256,"Clock de display") };
        foreach (var (mask, text) in labels) if ((bits & mask) != 0) yield return text;
        if ((bits & ~0x1ffUL) != 0) yield return $"Outros: 0x{bits & ~0x1ffUL:X}";
    }
}

public static class GpuSafety
{
    public static string? ReductionBlock(GpuReading reading, uint percent)
    {
        if (!reading.Telemetry.Allows(percent)) return "Percentual fora da faixa informada pelo driver (ou faixa indisponivel).";
        if (reading.Fans.Any(f => f.Policy is not (0 or 1) || f.TargetPercent is null)) return "Politica/alvo de algum fan indisponivel.";
        if (reading.TemperatureC is null or 0 or > 150) return "Temperatura do core indisponivel ou invalida.";
        if (reading.Telemetry.ClockEventReasons is null) return "Motivos de limitacao do driver indisponiveis; reducao bloqueada.";
        uint limit = reading.Telemetry.SlowdownC is > 3 and <= 150 ? Math.Min(80u, reading.Telemetry.SlowdownC.Value - 3) : 80;
        if (reading.TemperatureC >= limit) return $"Core atingiu o limite conservador do app ({limit} C).";
        if (reading.Telemetry.MemoryTemperatureC >= 95) return "VRAM atingiu 95 C (limite conservador do app).";
        if (reading.Telemetry.ThermalEvent) return "Driver reporta limitacao termica.";
        if (percent < 30 && reading.TemperatureC >= 50) return "Abaixo de 30% exige core abaixo de 50 C.";
        return null;
    }
}
