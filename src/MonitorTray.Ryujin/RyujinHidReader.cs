using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitorTray.Ryujin;

/// <summary>
/// READ-ONLY HID reader for the RYUJIN II (VID 0B05, PID 1988, UsagePage FF72). Coexists with Armoury Crate
/// (share READ|WRITE) but shares the channel: filters the response by command and retries if another one arrives.
/// Never sends a command outside <see cref="RyujinProtocol.IsReadCommand"/>.
/// </summary>
public sealed class RyujinHidReader : IDisposable
{
    const ushort Vid = 0x0B05, Pid = 0x1988;
    SafeFileHandle? _h;
    readonly object _gate = new();

    public bool IsOpen => _h is { IsInvalid: false, IsClosed: false };

    public bool TryOpen()
    {
        lock (_gate)
        {
            if (IsOpen) return true;
            var path = FindPath();
            if (path is null) return false;
            var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); return false; }
            _h = h; return true;
        }
    }

    /// <summary>Sends a read command and returns the matching response, or null (timeout/failure).</summary>
    public byte[]? Query(byte cmd, int timeoutMs = 600, int attempts = 4)
    {
        var req = RyujinProtocol.BuildRequest(cmd); // throws if it is not a read
        lock (_gate)
        {
            if (!IsOpen) return null;
            for (int a = 0; a < attempts; a++)
            {
                if (!WriteFile(_h!, req, req.Length, out _, IntPtr.Zero)) return null;
                var deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    var buf = ReadWithTimeout(Math.Max(50, (int)(deadline - Environment.TickCount64)));
                    if (buf is null) break;
                    if (RyujinProtocol.IsResponseTo(buf, cmd)) return buf;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Writes an LCD frame (only <see cref="RyujinProtocol.IsDisplayCommand"/>). true = cooler replied OK,
    /// false = failed, null = written without echo (the channel is shared; the response may have gone to another reader).
    /// </summary>
    public bool? SendDisplay(byte[] frame, int ackTimeoutMs = 250)
    {
        if (frame.Length != RyujinProtocol.FrameLength || frame[0] != RyujinProtocol.ReportId || !RyujinProtocol.IsDisplayCommand(frame[1]))
            throw new ArgumentException("LCD text/layout frames only", nameof(frame));
        return Send(frame, ackTimeoutMs);
    }

    /// <summary>Writes pump/embedded fan (0x1A) or radiator (0x21). Same return as <see cref="SendDisplay"/>.</summary>
    public bool? SendFan(byte[] frame, int ackTimeoutMs = 250)
    {
        if (frame.Length != RyujinProtocol.FrameLength || frame[0] != RyujinProtocol.ReportId || !RyujinProtocol.IsFanCommand(frame[1]))
            throw new ArgumentException("fan frames only (0x1A/0x21)", nameof(frame));
        return Send(frame, ackTimeoutMs);
    }

    bool? Send(byte[] frame, int ackTimeoutMs)
    {
        lock (_gate)
        {
            if (!IsOpen) return false;
            if (!WriteFile(_h!, frame, frame.Length, out _, IntPtr.Zero)) return false;
            var deadline = Environment.TickCount64 + ackTimeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var buf = ReadWithTimeout(Math.Max(50, (int)(deadline - Environment.TickCount64)));
                if (buf is null) break;
                if (buf[0] == RyujinProtocol.ReportId && buf[1] == frame[1]) return buf[2] == 0;
            }
            return null;
        }
    }

    byte[]? ReadWithTimeout(int ms)
    {
        byte[]? result = null;
        var t = new Thread(() =>
        {
            var b = new byte[RyujinProtocol.FrameLength];
            if (ReadFile(_h!, b, b.Length, out var n, IntPtr.Zero) && n > 0) result = b;
        }) { IsBackground = true };
        t.Start();
        if (!t.Join(ms)) { CancelIoEx(_h!, IntPtr.Zero); t.Join(200); return null; }
        return result;
    }

    public string? ReadFirmware() => Query(RyujinProtocol.CmdFirmware) is { } f ? RyujinProtocol.ParseFirmware(f) : null;
    public RyujinPumpSensors? ReadPump() => Query(RyujinProtocol.CmdPumpSensors) is { } f ? RyujinProtocol.ParsePumpSensors(f) : null;
    public RyujinFanReading? ReadFans() =>
        Query(RyujinProtocol.CmdFanRpm) is { } r && Query(RyujinProtocol.CmdFanDuty) is { } d ? RyujinProtocol.ParseFans(r, d) : null;

    public void Dispose() { lock (_gate) { _h?.Dispose(); _h = null; } }

    static string? FindPath()
    {
        HidD_GetHidGuid(out var g);
        var dev = SetupDiGetClassDevs(ref g, null, IntPtr.Zero, 0x12);
        try
        {
            for (uint i = 0; ; i++)
            {
                var d = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(dev, IntPtr.Zero, ref g, i, ref d)) return null;
                SetupDiGetDeviceInterfaceDetail(dev, ref d, IntPtr.Zero, 0, out var need, IntPtr.Zero);
                var p = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(p, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(dev, ref d, p, need, out _, IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(p + 4);
                    if (path is not null && path.Contains($"vid_{Vid:x4}&pid_{Pid:x4}", StringComparison.OrdinalIgnoreCase)
                        && path.Contains("mi_01", StringComparison.OrdinalIgnoreCase)) return path;
                }
                finally { Marshal.FreeHGlobal(p); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(dev); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SpDeviceInterfaceData { public int cbSize; public Guid g; public int f; public IntPtr r; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string n, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tpl);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] b, int n, out int written, IntPtr ov);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] b, int n, out int read, IntPtr ov);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("hid")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, string? e, IntPtr w, uint f);
    [DllImport("setupapi", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr i, ref Guid g, uint idx, ref SpDeviceInterfaceData d);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SpDeviceInterfaceData d, IntPtr det, uint size, out uint need, IntPtr di);
    [DllImport("setupapi")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
}
