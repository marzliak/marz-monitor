// Portado de CmpInf (homelab/hosts/nanim/apps/cmpinf/src, homolog 260a683f). Lógica de segurança mantida sem alterações.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorTray.Gpu;

internal sealed partial class NvmlGpuApi
{
    // Windows x64 NVML ABI (nvml.h): unsigned long is 32 bit on Windows.
    [StructLayout(LayoutKind.Sequential)] internal struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] internal struct FanSpeedInfo { public uint Version, Fan, Speed; }
    [StructLayout(LayoutKind.Explicit, Size = 8)] internal struct FieldUnion
    {
        [FieldOffset(0)] public double Double;
        [FieldOffset(0)] public uint UInt;
        [FieldOffset(0)] public ulong ULongLong;
        [FieldOffset(0)] public long LongLong;
        [FieldOffset(0)] public int Int;
        [FieldOffset(0)] public ushort UShort;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct FieldValue
    {
        public uint FieldId, ScopeId;
        public long Timestamp, LatencyUsec;
        public uint ValueType;
        public int Return;
        public FieldUnion Value;
    }
    internal static uint? DecodeMemoryTemperature(FieldValue value, DateTimeOffset now)
    {
        if (value.Return != 0 || value.FieldId != 82 || value.Timestamp <= 0) return null;
        long ageMicros = now.ToUnixTimeMilliseconds() * 1000 - value.Timestamp;
        if (ageMicros < -1000000 || ageMicros > 6000000) return null;
        double? number = value.ValueType switch {
            0 => value.Value.Double, 1 or 2 => value.Value.UInt, 3 => value.Value.ULongLong,
            4 => value.Value.LongLong, 5 => value.Value.Int, 6 => value.Value.UShort, _ => null };
        return number is > 0 and <= 150 && number == Math.Truncate(number.Value) ? (uint)number.Value : null;
    }
    internal static bool IsOptionalMetricError(int code) => code is 3 or 4 or 6 or 13 or 21 or 25 or 26 or 27;
    private static T? OptionalMetric<T>(Func<(int Code, T Value)> getter) where T : struct
    {
        try
        {
            var value = getter();
            if (value.Code == 0) return value.Value;
            // Optional telemetry may be absent/restricted. Device loss is not
            // optional: invalidate the sample instead of a false healthy frame.
            if (IsOptionalMetricError(value.Code)) return null;
            Check(value.Code, "Telemetria NVML"); return null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }
    private static uint? ReadRpm(IntPtr handle, uint fan)
    {
        var result = OptionalMetric(() => {
            var info = new FanSpeedInfo { Version = (uint)Marshal.SizeOf<FanSpeedInfo>() | (1u << 24), Fan = fan };
            int rc = FanRpm(handle, ref info); return (rc, info); });
        return result is FanSpeedInfo r && r.Fan == fan && r.Speed <= 30000 ? r.Speed : null;
    }
    private static GpuTelemetry ReadTelemetry(IntPtr handle)
    {
        string? driver = null;
        try { var b = new StringBuilder(96); if (Driver(b, 96) == 0) driver = b.ToString(); }
        catch (EntryPointNotFoundException) { }
        var range = OptionalMetric(() => { int rc = FanRange(handle, out uint min, out uint max); return (rc, (Min: min, Max: max)); });
        var utilization = OptionalMetric(() => { int rc = GetUtilization(handle, out Utilization value); return (rc, value); });
        var memory = OptionalMetric(() => { int rc = GetMemory(handle, out MemoryInfo value); return (rc, value); });
        uint? slowdown = OptionalMetric(() => { int rc = Threshold(handle, 1, out uint v); return (rc, v); });
        uint? shutdown = OptionalMetric(() => { int rc = Threshold(handle, 0, out uint v); return (rc, v); });
        uint? power = OptionalMetric(() => { int rc = Power(handle, out uint v); return (rc, v); });
        uint? powerLimit = OptionalMetric(() => { int rc = PowerLimit(handle, out uint v); return (rc, v); });
        uint? coreClock = OptionalMetric(() => { int rc = Clock(handle, 0, out uint v); return (rc, v); });
        uint? memClock = OptionalMetric(() => { int rc = Clock(handle, 2, out uint v); return (rc, v); });
        ulong? events = OptionalMetric(() => { int rc = ClockEvents(handle, out ulong v); return (rc, v); });
        uint? memoryTemp = null;
        try
        {
            var fields = new[] { new FieldValue { FieldId = 82 } };
            int rc = Fields(handle, 1, fields);
            if (rc == 0) memoryTemp = DecodeMemoryTemperature(fields[0], DateTimeOffset.UtcNow);
            else if (!IsOptionalMetricError(rc)) Check(rc, "nvmlDeviceGetFieldValues");
        }
        catch (EntryPointNotFoundException) { }
        bool memoryValid = memory is MemoryInfo m && m.Total > 0 && m.Used <= m.Total && m.Free <= m.Total;
        bool rangeValid = range is { } fr && fr.Min <= fr.Max && fr.Max <= 100;
        return new GpuTelemetry {
            Driver = driver, MemoryTemperatureC = memoryTemp, MemoryTemperatureSource = memoryTemp.HasValue ? "NVML" : null,
            SlowdownC = slowdown is > 0 and <= 150 ? slowdown : null, ShutdownC = shutdown is > 0 and <= 150 ? shutdown : null,
            MinFanPercent = rangeValid ? range!.Value.Min : null, MaxFanPercent = rangeValid ? range!.Value.Max : null,
            GpuUtilPercent = utilization?.Gpu <= 100 ? utilization?.Gpu : null,
            MemoryUtilPercent = utilization?.Memory <= 100 ? utilization?.Memory : null,
            MemoryUsedBytes = memoryValid ? memory!.Value.Used : null, MemoryTotalBytes = memoryValid ? memory!.Value.Total : null,
            PowerW = power <= 2000000 ? power / 1000.0 : null,
            PowerLimitW = powerLimit is > 0 and <= 2000000 ? powerLimit / 1000.0 : null,
            CoreClockMhz = coreClock <= 20000 ? coreClock : null, MemoryClockMhz = memClock <= 50000 ? memClock : null,
            ClockEventReasons = events };
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlSystemGetDriverVersion", ExactSpelling=true, CharSet=CharSet.Ansi, CallingConvention=CallingConvention.Cdecl)] private static extern int Driver(StringBuilder version,uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetMinMaxFanSpeed", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int FanRange(IntPtr device,out uint minimum,out uint maximum);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetUtilizationRates", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int GetUtilization(IntPtr device,out Utilization value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetMemoryInfo", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int GetMemory(IntPtr device,out MemoryInfo value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetEnforcedPowerLimit", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int PowerLimit(IntPtr device,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetPowerUsage", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Power(IntPtr device,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetTemperatureThreshold", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Threshold(IntPtr device,uint which,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetClockInfo", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Clock(IntPtr device,uint which,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetCurrentClocksThrottleReasons", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int ClockEvents(IntPtr device,out ulong bits);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetFanSpeedRPM", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int FanRpm(IntPtr device,ref FanSpeedInfo value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetFieldValues", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Fields(IntPtr device,int count,[In,Out] FieldValue[] values);
}
