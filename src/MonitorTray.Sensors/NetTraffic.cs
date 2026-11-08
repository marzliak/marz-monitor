using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Sensors;

/// <summary>
/// Tráfego separado em rede local × internet, somente leitura, via ETW (provedor Microsoft-Windows-Kernel-Network:
/// eventos TCP/UDP de envio e recebimento, IPv4 e IPv6, com tamanho e endereços). Exige administrador.
/// Conta bytes de dados TCP/UDP (sem cabeçalhos/ICMP), então o total pode diferir um pouco do contador da placa.
/// A sessão "MarzMonitor-Net" é parada ao sair e reaproveitada se ficou aberta.
/// </summary>
public sealed unsafe class NetTraffic : IDisposable
{
    private static readonly Guid Provider = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private const string SessionName = "MarzMonitor-Net";
    private long downLocal, upLocal, downNet, upNet;
    private long[] prev = new long[4];
    private readonly Stopwatch sw = new();
    private ulong session, trace = ulong.MaxValue;
    private Thread? thread;
    private EventCallback? keep;
    private IntPtr loggerName;
    public bool Running { get; private set; }
    public string? Error { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void EventCallback(IntPtr record);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
    [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(ulong session, ref Guid provider, uint control, byte level, ulong any, ulong all, uint timeout, IntPtr p);
    [DllImport("advapi32.dll")] private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);

    private static IntPtr Props()
    {
        const int size = 120 + 1024 * 2;
        var p = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)p, size).Clear();
        *(uint*)(p + 0) = size;          // Wnode.BufferSize
        *(uint*)(p + 40) = 1;            // Wnode.ClientContext = QPC
        *(uint*)(p + 44) = 0x00020000;   // WNODE_FLAG_TRACED_GUID
        *(uint*)(p + 48) = 64;           // BufferSize KB
        *(uint*)(p + 52) = 4; *(uint*)(p + 56) = 16;
        *(uint*)(p + 64) = 0x100;        // EVENT_TRACE_REAL_TIME_MODE
        *(uint*)(p + 68) = 1;            // FlushTimer (s)
        *(uint*)(p + 116) = 120;         // LoggerNameOffset
        return p;
    }

    public void Start()
    {
        if (Running) return;
        try
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) { Error = "requer administrador"; return; }
            var p = Props();
            try
            {
                ControlTraceW(0, SessionName, p, 1); // para sessão órfã de uma execução anterior
                Marshal.FreeHGlobal(p); p = Props();
                int rc = StartTraceW(out session, SessionName, p);
                if (rc != 0) { Error = "StartTrace " + rc; return; }
            }
            finally { Marshal.FreeHGlobal(p); }
            var g = Provider;
            int e = EnableTraceEx2(session, ref g, 1, 5, 0, 0, 0, IntPtr.Zero);
            if (e != 0) { Error = "EnableTrace " + e; Stop(); return; }
            keep = OnEvent;
            loggerName = Marshal.StringToHGlobalUni(SessionName);
            var lf = Marshal.AllocHGlobal(448);
            try
            {
                new Span<byte>((void*)lf, 448).Clear();
                *(IntPtr*)(lf + 8) = loggerName;
                *(uint*)(lf + 28) = 0x100 | 0x10000000; // REAL_TIME | EVENT_RECORD
                *(IntPtr*)(lf + 424) = Marshal.GetFunctionPointerForDelegate(keep);
                trace = OpenTraceW(lf);
            }
            finally { Marshal.FreeHGlobal(lf); }
            if (trace == ulong.MaxValue) { Error = "OpenTrace " + Marshal.GetLastWin32Error(); Stop(); return; }
            var h = trace;
            thread = new Thread(() => { try { ProcessTrace([h], 1, IntPtr.Zero, IntPtr.Zero); } catch (Exception) { } }) { IsBackground = true, Name = "NetTraffic" };
            thread.Start();
            sw.Start();
            Running = true;
        }
        catch (Exception ex) { Error = ex.Message; Stop(); }
    }

    private void OnEvent(IntPtr rec)
    {
        try
        {
            ushort id = *(ushort*)(rec + 40);
            bool up, v6;
            switch (id)
            {
                case 10: case 42: up = true; v6 = false; break;
                case 11: case 43: up = false; v6 = false; break;
                case 26: case 58: up = true; v6 = true; break;
                case 27: case 59: up = false; v6 = true; break;
                default: return;
            }
            int len = *(ushort*)(rec + 86);
            byte* ud = *(byte**)(rec + 96);
            int al = v6 ? 16 : 4;
            if (ud == null || len < 8 + 2 * al) return;
            uint size = *(uint*)(ud + 4);
            var scope = NetScope.Of(new ReadOnlySpan<byte>(ud + 8, al), new ReadOnlySpan<byte>(ud + 8 + al, al));
            if (scope == TrafficScope.Ignore) return;
            if (scope == TrafficScope.Local) Interlocked.Add(ref up ? ref upLocal : ref downLocal, size);
            else Interlocked.Add(ref up ? ref upNet : ref downNet, size);
        }
        catch (Exception) { }
    }

    /// <summary>B/s desde a última leitura: (↓local, ↑local, ↓internet, ↑internet).</summary>
    public (double DownLocal, double UpLocal, double DownNet, double UpNet) Rates()
    {
        long[] now = [Interlocked.Read(ref downLocal), Interlocked.Read(ref upLocal), Interlocked.Read(ref downNet), Interlocked.Read(ref upNet)];
        var dt = sw.Elapsed.TotalSeconds; sw.Restart();
        double R(int i) => dt > 0 ? Math.Max(0, now[i] - prev[i]) / dt : 0;
        var r = (R(0), R(1), R(2), R(3));
        prev = now;
        return r;
    }

    public void AppendSensors(List<RawSensor> list)
    {
        if (!Running) return;
        var (dl, ul, dn, un) = Rates();
        void Add(string dev, string name, double down, double up)
        {
            list.Add(new($"{dev}/throughput/0", dev, name, "Network", "Download", "Throughput", "B/s", (float)down));
            list.Add(new($"{dev}/throughput/1", dev, name, "Network", "Upload", "Throughput", "B/s", (float)up));
        }
        Add(MonitorTray.Core.Normalize.NetTrafficIds.Total, "Tráfego total", dl + dn, ul + un);
        Add(MonitorTray.Core.Normalize.NetTrafficIds.Local, "Tráfego rede local", dl, ul);
        Add(MonitorTray.Core.Normalize.NetTrafficIds.Internet, "Tráfego internet", dn, un);
    }

    private void Stop()
    {
        if (session != 0)
        {
            var p = Props();
            try { ControlTraceW(session, null, p, 1); } finally { Marshal.FreeHGlobal(p); }
            session = 0;
        }
        if (trace != ulong.MaxValue) { CloseTrace(trace); trace = ulong.MaxValue; }
        thread?.Join(2000); thread = null;
        if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
        Running = false;
    }

    public void Dispose() => Stop();
}
