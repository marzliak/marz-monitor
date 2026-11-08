// Portado de CmpInf (homelab/hosts/nanim/apps/cmpinf/src, homolog 260a683f). Lógica de segurança mantida sem alterações.
using System;
using System.Runtime.InteropServices;

namespace MonitorTray.Gpu;

// Read-only fallback for temperatures that NVML does not expose on GeForce (GDDR6X junction, hotspot).
// Uses the undocumented NvAPI_GPU_GetThermalSensors (0x65FE3AAD), the same call and channel map as
// LibreHardwareMonitor. No writes, no elevation. Any failure returns null and is retried at most
// every 30 s, so a missing/changed driver never affects NVML telemetry or fan control.
internal static class NvApiThermal
{
    private const uint IdInitialize = 0x0150E828, IdEnumPhysicalGpus = 0xE5AC921F, IdGetThermalSensors = 0x65FE3AAD;
    private static readonly uint[] Masks = { 0x3FF, 0x1FF, 0xFF, 0x7F, 0x3F, 0x0F };

    [StructLayout(LayoutKind.Sequential)]
    internal struct ThermalSensors
    {
        public uint Version, Mask;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] Reserved;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public int[] Temps;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn([Out] IntPtr[] handles, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SensorsFn(IntPtr gpu, ref ThermalSensors sensors);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    private static readonly object gate = new();
    private static SensorsFn? sensors;
    private static IntPtr gpu;
    private static uint mask;
    private static DateTime retryAfter = DateTime.MinValue;

    internal readonly record struct Reading(uint? MemoryC, uint? HotspotC);

    /// <summary>Channel indices by generation (hotspot, memory). Pure; unit-tested.</summary>
    internal static (int? Hotspot, int Memory) ChannelMap(string? gpuName)
    {
        string name = (gpuName ?? "").ToUpperInvariant();
        if (name.Contains("RTX 50")) return (null, 2);
        if (name.Contains("RTX 40")) return (1, 7);
        return (1, 9); // RTX 30xx/20xx and older
    }

    /// <summary>Decodes 1/256 °C fixed point; rejects absent or implausible channels. Pure; unit-tested.</summary>
    internal static uint? Decode(int raw, uint activeMask, int channel)
    {
        if (channel < 0 || channel > 31 || ((activeMask >> channel) & 1) == 0 || raw <= 0) return null;
        double celsius = raw / 256.0;
        return celsius is > 0 and <= 150 ? (uint)Math.Round(celsius) : null;
    }

    /// <summary>Only for a single physical NVIDIA GPU (unambiguous mapping to the NVML device).</summary>
    internal static Reading? TryRead(string? gpuName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        lock (gate)
        {
            try
            {
                if (sensors == null && !Initialize()) return null;
                var (hotspot, memory) = ChannelMap(gpuName);
                var data = New(mask);
                if (sensors!(gpu, ref data) != 0) { Reset(); return null; }
                uint? mem = Decode(data.Temps[memory], data.Mask, memory);
                uint? hot = hotspot is int h ? Decode(data.Temps[h], data.Mask, h) : null;
                return mem == null && hot == null ? null : new Reading(mem, hot);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException or SEHException)
            {
                Reset(); return null;
            }
        }
    }

    private static ThermalSensors New(uint requested) => new()
    {
        Version = (uint)Marshal.SizeOf<ThermalSensors>() | (1u << 16), Mask = requested, Reserved = new int[8], Temps = new int[32],
    };

    private static bool Initialize()
    {
        if (DateTime.UtcNow < retryAfter) return false;
        retryAfter = DateTime.UtcNow.AddSeconds(30);
        IntPtr init = QueryInterface(IdInitialize), enumerate = QueryInterface(IdEnumPhysicalGpus), read = QueryInterface(IdGetThermalSensors);
        if (init == IntPtr.Zero || enumerate == IntPtr.Zero || read == IntPtr.Zero) return false;
        if (Marshal.GetDelegateForFunctionPointer<InitializeFn>(init)() != 0) return false;
        var handles = new IntPtr[64];
        if (Marshal.GetDelegateForFunctionPointer<EnumFn>(enumerate)(handles, out uint count) != 0 || count != 1) return false;
        var fn = Marshal.GetDelegateForFunctionPointer<SensorsFn>(read);
        foreach (uint candidate in Masks)
        {
            var probe = New(candidate);
            if (fn(handles[0], ref probe) == 0) { sensors = fn; gpu = handles[0]; mask = candidate; return true; }
        }
        return false;
    }

    private static void Reset() { sensors = null; gpu = IntPtr.Zero; mask = 0; retryAfter = DateTime.UtcNow.AddSeconds(30); }
}
