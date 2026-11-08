// Ported from GPU by MarzOps (CmpInf, homolog 260a683f). Logic kept; only namespace/log/visibility changed.
// Consumer-Control only. No keyboard hook, key logging, input synthesis or HID driver.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MonitorTray.Core.Keyboard;

namespace MonitorTray.Oled;

internal sealed class PreparsedRetryState
{
    private long nextAttempt;
    private int delay = 100;
    private bool ready;
    internal bool ShouldTry(long now) => !ready && now >= nextAttempt;
    internal void Failed(long now)
    {
        ready = false;
        nextAttempt = now + delay;
        delay = Math.Min(delay * 2, 5000);
    }
    internal void Succeeded() { ready = true; delay = 100; }
    internal void Reset(long now = 0) { ready = false; nextAttempt = now; delay = 100; }
}

internal sealed class R33InputCleanup
{
    private readonly Func<bool> unregister;
    private readonly Action cleanup;
    internal bool IsRegistered { get; private set; } = true;
    internal R33InputCleanup(Func<bool> unregister, Action cleanup) { this.unregister = unregister; this.cleanup = cleanup; }
    internal static bool TryDisposeWithRetries(R33InputCleanup cleanup, int maxAttempts)
    {
        if(maxAttempts<1)throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        for(int attempt=0;attempt<maxAttempts;attempt++) if(cleanup.TryDispose()) return true;
        return !cleanup.IsRegistered;
    }
    internal bool TryDispose()
    {
        if (!IsRegistered) { cleanup(); return true; }
        if (!unregister()) { cleanup(); return false; }
        IsRegistered = false;
        cleanup();
        return true;
    }
}

internal sealed class R33Input : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Registration { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public IntPtr Device, WParam; }
    [DllImport("user32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] Registration[] items,uint count,uint size);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
    [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern uint GetDeviceName(IntPtr device,uint command,[Out] StringBuilder value,ref uint length);
    [DllImport("user32.dll",EntryPoint="GetRawInputDeviceInfoW",SetLastError=true)]
    private static extern uint GetDeviceData(IntPtr device,uint command,IntPtr data,ref uint length);
    [DllImport("hid.dll")] private static extern uint HidP_MaxUsageListLength(int type,ushort page,IntPtr preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int type,ushort page,ushort collection,[Out] ushort[] usages,ref uint count,IntPtr preparsed,IntPtr report,uint length);
    private sealed class Device : IDisposable
    {
        internal bool Candidate, Tried; internal IntPtr Data; internal string Path = "";
        internal readonly PreparsedRetryState Retry = new PreparsedRetryState();
        internal readonly Dictionary<byte,bool> Held = new Dictionary<byte,bool>();
        public void Dispose() { if(Data!=IntPtr.Zero) Marshal.FreeHGlobal(Data); Data=IntPtr.Zero; }
    }
    private readonly Dictionary<IntPtr,Device> devices = new Dictionary<IntPtr,Device>();
    private readonly Action<bool,bool> action;
    private readonly Action reset;
    private IntPtr chosen; private int disposed;
    private R33InputCleanup? unregisterCleanup;
    private readonly HidSelection selection = new HidSelection();
    internal R33Input(IntPtr window,Action<bool,bool> action,Action reset)
    {
        this.action=action; this.reset=reset;
        var entry=new Registration {Page=0x0C,Usage=1,Flags=0x100|0x2000,Target=window};
        if(!RegisterRawInputDevices(new[]{entry},1,(uint)Marshal.SizeOf<Registration>()))
            throw new InvalidOperationException("RAW Consumer Control: "+Marshal.GetLastWin32Error());
        unregisterCleanup = new R33InputCleanup(
            () => RegisterRawInputDevices(new[]{new Registration {Page=0x0C,Usage=1,Flags=1,Target=IntPtr.Zero}},1,(uint)Marshal.SizeOf<Registration>()),
            ReleaseResources);
    }
    private void ReleaseResources()
    {
        foreach(var d in devices.Values)d.Dispose();
        devices.Clear();
        chosen=IntPtr.Zero;
        reset();
    }
    private Device GetDevice(IntPtr handle)
    {
        if(devices.TryGetValue(handle,out var found)) return found;
        if(devices.Count>=64) throw new InvalidOperationException("Too many HID endpoints; restart the monitor.");
        uint n=0; string path="";
        if(handle!=IntPtr.Zero && GetDeviceData(handle,0x20000007,IntPtr.Zero,ref n)!=uint.MaxValue && n>0 && n<16384)
        {
            var b=new StringBuilder((int)n+1); uint capacity=(uint)b.Capacity;
            if(GetDeviceName(handle,0x20000007,b,ref capacity)!=uint.MaxValue) path=b.ToString();
        }
        var device=new Device {Candidate=DeviceFilter.Matches(path), Path=path};
        devices.Add(handle,device);
        // Full path is used only in this method. No serial/path is retained or written to a log.
        if(device.Candidate)
        {
            selection.Update(devices.Values.Where(d => d.Candidate).Select(d => d.Path));
            var selectedPath = selection.Chosen;
            var next = devices.FirstOrDefault(pair => pair.Value.Candidate && pair.Value.Path == selectedPath).Key;
            if (next != chosen) { chosen = next; reset(); }
            if (chosen != IntPtr.Zero) Log.Info("Wheel: HID VID_1038 PID_1640 MI_03 COL01 recognized.");
            else { reset(); Log.Warn("Two HIDs with the wheel signature: capture suspended, not picking one by guesswork."); }
        }
        return device;
    }
    internal void DeviceChanged(IntPtr handle)
    {
        // Removal/reconnection resets press state so a missing release cannot leave it stuck.
        if(devices.TryGetValue(handle,out var d)) {d.Dispose();devices.Remove(handle);}
        if(handle==chosen) {chosen=IntPtr.Zero;reset();}
        selection.Update(devices.Values.Where(d => d.Candidate).Select(d => d.Path));
        var selectedPath = selection.Chosen;
        chosen = devices.FirstOrDefault(pair => pair.Value.Candidate && pair.Value.Path == selectedPath).Key;
        reset();
    }
    internal void Read(IntPtr input)
    {
        uint hs=(uint)Marshal.SizeOf<Header>(),size=0;
        if(GetRawInputData(input,0x10000003,IntPtr.Zero,ref size,hs)==uint.MaxValue||size<hs||size>1048576)return;
        IntPtr buffer=Marshal.AllocHGlobal((int)size);
        try
        {
            uint read=GetRawInputData(input,0x10000003,buffer,ref size,hs);
            if(read==uint.MaxValue||read<hs+8)return;
            var h=Marshal.PtrToStructure<Header>(buffer);
            if(h.Type!=2)return; // In particular, never use the duplicate RAW-keyboard endpoint.
            IntPtr body=IntPtr.Add(buffer,(int)hs);
            uint bytes=unchecked((uint)Marshal.ReadInt32(body)), reports=unchecked((uint)Marshal.ReadInt32(body,4));
            if(bytes==0||bytes>8192||reports==0||reports>64||(ulong)bytes*reports>read-hs-8)return;
            Device device=GetDevice(h.Device);
            if(device.Data==IntPtr.Zero && device.Retry.ShouldTry(Environment.TickCount64))
            {
                uint n=0;
                if(GetDeviceData(h.Device,0x20000005,IntPtr.Zero,ref n)!=uint.MaxValue&&n>0&&n<=1048576)
                {
                    IntPtr data=Marshal.AllocHGlobal((int)n);
                    if(GetDeviceData(h.Device,0x20000005,data,ref n)!=uint.MaxValue)
                    {
                        device.Data=data;
                        device.Tried=true;
                        device.Retry.Succeeded();
                    }
                    else
                    {
                        Marshal.FreeHGlobal(data);
                        device.Retry.Failed(Environment.TickCount64);
                    }
                }
                else device.Retry.Failed(Environment.TickCount64);
            }
            if(device.Data==IntPtr.Zero)return;
            uint max=HidP_MaxUsageListLength(0,0x0C,device.Data);
            if(max==0||max>4096)return;
            for(uint i=0;i<reports;i++)
            {
                IntPtr report=IntPtr.Add(body,checked(8+(int)(i*bytes)));
                var usages=new ushort[max];uint count=max;
                if(HidP_GetUsages(0,0x0C,0,usages,ref count,device.Data,report,bytes)!=0x00110000)continue;
                byte id=Marshal.ReadByte(report);bool now=usages.Take((int)Math.Min(count,max)).Contains((ushort)0xE2);
                bool beforeAny=device.Held.Values.Any(v=>v);
                device.Held[id]=now;
                bool afterAny=device.Held.Values.Any(v=>v);
                if(beforeAny!=afterAny)
                {
                    bool target=device.Candidate&&h.Device==chosen&&selection.Chosen != null;
                    action(target,afterAny);
                }
            }
        }
        finally {Marshal.FreeHGlobal(buffer);}
    }
    public void Dispose()
    {
        if(Volatile.Read(ref disposed)!=0)return;
        if(unregisterCleanup is null || unregisterCleanup.TryDispose())
            Interlocked.Exchange(ref disposed,1);
    }
    internal bool TryDisposeWithRetries(int maxAttempts)
    {
        if(unregisterCleanup is null){Interlocked.Exchange(ref disposed,1);return true;}
        bool complete=R33InputCleanup.TryDisposeWithRetries(unregisterCleanup,maxAttempts);
        if(complete)Interlocked.Exchange(ref disposed,1);
        return complete;
    }
}


internal sealed class HidSelection
{
    public string? Chosen { get; private set; }
    public bool CaptureSuspended { get; private set; }

    public void Update(IEnumerable<string> candidates)
    {
        var handles = candidates.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        CaptureSuspended = handles.Length >= 2;
        Chosen = handles.Length == 1 ? handles[0] : null;
    }
}
