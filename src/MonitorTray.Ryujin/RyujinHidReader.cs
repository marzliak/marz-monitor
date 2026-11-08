using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MonitorTray.Ryujin;

/// <summary>
/// Leitor HID SOMENTE LEITURA do RYUJIN II (VID 0B05, PID 1988, UsagePage FF72). Convive com o Armoury Crate
/// (share READ|WRITE) mas compartilha o canal: filtra a resposta pelo comando e repete se vier a de outro.
/// Nunca envia comando fora de <see cref="RyujinProtocol.IsReadCommand"/>.
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

    /// <summary>Envia um comando de leitura e devolve a resposta correspondente, ou null (timeout/falha).</summary>
    public byte[]? Query(byte cmd, int timeoutMs = 600, int attempts = 4)
    {
        var req = RyujinProtocol.BuildRequest(cmd); // lança se não for leitura
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
    /// Escreve um quadro do LCD (só <see cref="RyujinProtocol.IsDisplayCommand"/>). true = cooler respondeu OK,
    /// false = falhou, null = escrito sem eco (o canal é compartilhado; a resposta pode ter ido a outro leitor).
    /// </summary>
    public bool? SendDisplay(byte[] frame, int ackTimeoutMs = 250)
    {
        if (frame.Length != RyujinProtocol.FrameLength || frame[0] != RyujinProtocol.ReportId || !RyujinProtocol.IsDisplayCommand(frame[1]))
            throw new ArgumentException("só quadros de texto/layout do LCD", nameof(frame));
        return Send(frame, ackTimeoutMs);
    }

    /// <summary>Escreve bomba/fan embutido (0x1A) ou radiador (0x21). Mesmo retorno de <see cref="SendDisplay"/>.</summary>
    public bool? SendFan(byte[] frame, int ackTimeoutMs = 250)
    {
        if (frame.Length != RyujinProtocol.FrameLength || frame[0] != RyujinProtocol.ReportId || !RyujinProtocol.IsFanCommand(frame[1]))
            throw new ArgumentException("só quadros de fan (0x1A/0x21)", nameof(frame));
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
