// Ported from CmpInf (homelab/hosts/nanim/apps/cmpinf/src, homolog 260a683f). Safety logic kept unchanged.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorTray.Gpu;

internal sealed partial class NvmlGpuApi : IGpuApi
{
    private const string Dll = "nvml.dll";
    private bool initialized;
    internal NvmlGpuApi()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This app's NVML requires Windows.");
        Check(Init(), "nvmlInit_v2"); initialized = true;
    }
    public IReadOnlyList<GpuIdentity> Discover()
    {
        Ready(); Check(Count(out uint count), "nvmlDeviceGetCount_v2");
        if (count > 64) throw new InvalidOperationException("Invalid GPU count.");
        var result = new List<GpuIdentity>();
        for (uint index = 0; index < count; index++)
        {
            Check(ByIndex(index, out IntPtr handle), "nvmlDeviceGetHandleByIndex_v2");
            result.Add(Identity(handle));
        }
        if (result.Select(g => g.Uuid).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidOperationException("Duplicate GPU identity.");
        return result;
    }
    private static GpuIdentity Identity(IntPtr handle)
    {
        var uuid = new StringBuilder(96); var name = new StringBuilder(256);
        Check(Uuid(handle, uuid, 96), "nvmlDeviceGetUUID");
        Check(Name(handle, name, 256), "nvmlDeviceGetName");
        Check(FanCount(handle, out uint fans), "nvmlDeviceGetNumFans");
        if (fans > 32) throw new InvalidOperationException("Invalid fan count.");
        if (!uuid.ToString().StartsWith("GPU-", StringComparison.Ordinal) || name.Length == 0)
            throw new InvalidOperationException("Invalid NVML identity.");
        return new GpuIdentity(uuid.ToString(), name.ToString(), (int)fans);
    }
    private IntPtr Handle(string uuid)
    {
        Ready(); Check(ByUuid(uuid, out IntPtr handle), "nvmlDeviceGetHandleByUUID");
        if (Identity(handle).Uuid != uuid) throw new InvalidOperationException("NVML returned a different GPU.");
        return handle;
    }
    public GpuReading Read(string uuid)
    {
        var captured = DateTimeOffset.UtcNow;
        var handle = Handle(uuid); var identity = Identity(handle);
        uint? temp = Optional(() => { int rc = Temperature(handle, 0, out uint v); return (rc, v); });
        if (temp is 0 or > 150) temp = null;
        var fans = new List<FanReading>();
        for (uint i = 0; i < identity.FanCount; i++)
        {
            uint? speed = Optional(() => { int rc = Speed(handle, i, out uint v); return (rc, v); });
            uint? target = Optional(() => { int rc = Target(handle, i, out uint v); return (rc, v); });
            uint? policy = Optional(() => { int rc = Policy(handle, i, out uint v); return (rc, v); });
            fans.Add(new FanReading((int)i, speed <= 100 ? speed : null, target <= 100 ? target : null, policy is 0 or 1 ? policy : null) { SpeedRpm = ReadRpm(handle, i) });
        }
        var telemetry = ReadTelemetry(handle);
        // GeForce: NVML hides GDDR6X junction/hotspot. Read-only NVAPI fallback, single-GPU systems only.
        if (telemetry.MemoryTemperatureC == null || telemetry.HotspotC == null)
        {
            Ready(); Check(Count(out uint gpuCount), "nvmlDeviceGetCount_v2");
            if (gpuCount == 1 && NvApiThermal.TryRead(identity.Name) is { } extra)
                telemetry = telemetry with {
                    MemoryTemperatureC = telemetry.MemoryTemperatureC ?? extra.MemoryC,
                    MemoryTemperatureSource = telemetry.MemoryTemperatureC.HasValue ? telemetry.MemoryTemperatureSource : extra.MemoryC.HasValue ? "NVAPI" : null,
                    HotspotC = telemetry.HotspotC ?? extra.HotspotC };
        }
        return new GpuReading(identity, captured, temp, fans) { Telemetry = telemetry };
    }
    private static uint? Optional(Func<(int Code, uint Value)> read)
    {
        try
        {
            var result = read();
            if (result.Code == 0) return result.Value;
            if (result.Code is 3 or 13) return null; // unsupported / unavailable function
            Check(result.Code, "NVML read"); return null;
        }
        catch (EntryPointNotFoundException) { return null; }
    }
    public void SetSpeed(string uuid, int fan, uint percent)
    {
        if (percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        var handle = Handle(uuid); ValidateFan(handle, fan);
        if (percent < 100)
        {
            Check(FanRange(handle, out uint minimum, out uint maximum), "nvmlDeviceGetMinMaxFanSpeed");
            if (minimum > maximum || maximum > 100 || percent < minimum || percent > maximum) throw new InvalidOperationException("Percentage outside the NVML range.");
        }
        // This call itself switches policy to manual. No separate low/zero target.
        Check(SetSpeed(handle, (uint)fan, percent), $"nvmlDeviceSetFanSpeed_v2 fan {fan}");
    }
    public void RestoreAutomatic(string uuid, int fan)
    {
        var handle = Handle(uuid); ValidateFan(handle, fan);
        Check(SetDefault(handle, (uint)fan), $"nvmlDeviceSetDefaultFanSpeed_v2 fan {fan}");
    }
    private static void ValidateFan(IntPtr handle, int fan)
    {
        Check(FanCount(handle, out uint count), "nvmlDeviceGetNumFans");
        if (fan < 0 || fan >= count) throw new ArgumentOutOfRangeException(nameof(fan));
    }
    private void Ready() { if (!initialized) throw new ObjectDisposedException(nameof(NvmlGpuApi)); }
    public void Dispose()
    {
        if (!initialized) return;
        int rc = Shutdown(); initialized = false; Check(rc, "nvmlShutdown");
    }
    private static void Check(int result, string operation)
    {
        if (result == 0) return;
        string message = Marshal.PtrToStringAnsi(ErrorString(result)) ?? "Unknown error";
        throw new InvalidOperationException($"{operation}: NVML {result} ({message})");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlInit_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Init();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlShutdown", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Shutdown();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlErrorString", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern IntPtr ErrorString(int result);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetCount_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Count(out uint count);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetHandleByIndex_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int ByIndex(uint index,out IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetHandleByUUID", ExactSpelling=true, CharSet=CharSet.Ansi, CallingConvention=CallingConvention.Cdecl)] private static extern int ByUuid(string uuid,out IntPtr handle);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetUUID", ExactSpelling=true, CharSet=CharSet.Ansi, CallingConvention=CallingConvention.Cdecl)] private static extern int Uuid(IntPtr device,StringBuilder uuid,uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetName", ExactSpelling=true, CharSet=CharSet.Ansi, CallingConvention=CallingConvention.Cdecl)] private static extern int Name(IntPtr device,StringBuilder name,uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetNumFans", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int FanCount(IntPtr device,out uint count);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetTemperature", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Temperature(IntPtr device,uint sensor,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetFanSpeed_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Speed(IntPtr device,uint fan,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetTargetFanSpeed", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Target(IntPtr device,uint fan,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceGetFanControlPolicy_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int Policy(IntPtr device,uint fan,out uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceSetFanSpeed_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int SetSpeed(IntPtr device,uint fan,uint value);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(Dll, EntryPoint="nvmlDeviceSetDefaultFanSpeed_v2", ExactSpelling=true, CallingConvention=CallingConvention.Cdecl)] private static extern int SetDefault(IntPtr device,uint fan);
}
