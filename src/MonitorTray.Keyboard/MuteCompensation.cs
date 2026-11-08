// Portado do GPU by MarzOps (CmpInf, homolog 260a683f). Lógica mantida; só namespace/log/visibilidade mudaram.
// Mute compensation on the default render endpoint only; NOT HID suppression.
// No volume-level changes, no microphone access, no keystroke injection.
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using MonitorTray.Core.Keyboard;

namespace MonitorTray.Oled;

[ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class R33DeviceEnumeratorClass { }
[ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface R33DeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IntPtr collection);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow,int role,out R33Device endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out R33Device endpoint);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
}
[ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface R33Device
{
    [PreserveSig] int Activate(ref Guid id,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)]out object instance);
    [PreserveSig] int OpenPropertyStore(uint mode,out IntPtr store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)]out string id);
    [PreserveSig] int GetState(out uint state);
}
[ComImport,Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface R33EndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(R33VolumeCallback callback);
    [PreserveSig] int UnregisterControlChangeNotify(R33VolumeCallback callback);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float value,ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float value,ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float value);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float value);
    [PreserveSig] int SetChannelVolumeLevel(uint channel,float value,ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel,float value,ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel,out float value);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel,out float value);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)]bool mute,ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)]out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step,out uint count);
    [PreserveSig] int VolumeStepUp(ref Guid context);
    [PreserveSig] int VolumeStepDown(ref Guid context);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float min,out float max,out float increment);
}
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown),ComVisible(true)]
public interface R33VolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

[StructLayout(LayoutKind.Sequential, Pack=4)]
internal struct R33AudioVolumeNotificationData
{
    internal Guid EventContext;
    internal int Muted;
    internal float MasterVolume;
    internal uint NChannels;
    internal static int ChannelsOffset => 28;
}

internal sealed class R33VolumeNotification
{
    internal Guid EventContext { get; }
    internal bool Muted { get; }
    internal float MasterVolume { get; }
    internal float[] Channels { get; }
    internal R33VolumeNotification(Guid eventContext,bool muted,float masterVolume,float[] channels)
    { EventContext=eventContext;Muted=muted;MasterVolume=masterVolume;Channels=channels; }
}

[ClassInterface(ClassInterfaceType.None),ComVisible(true)]
public sealed class R33VolumeSink : R33VolumeCallback
{
    private readonly Action<R33VolumeNotification,long> notify;
    internal R33VolumeSink(Action<R33VolumeNotification,long> notify){this.notify=notify;}
    internal static bool TryParse(IntPtr data,out R33VolumeNotification? notification)
    {
        notification=null;
        if(data==IntPtr.Zero)return false;
        try
        {
            var header=Marshal.PtrToStructure<R33AudioVolumeNotificationData>(data);
            if(header.NChannels==0 || header.NChannels>1024)return false;
            int count=checked((int)header.NChannels);
            int bytes=checked(count*sizeof(float));
            if(bytes>4096)return false;
            var channels=new float[count];
            Marshal.Copy(IntPtr.Add(data,R33AudioVolumeNotificationData.ChannelsOffset),channels,0,count);
            notification=new R33VolumeNotification(header.EventContext,header.Muted!=0,header.MasterVolume,channels);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
    public int OnNotify(IntPtr data)
    {
        // Never block or call SetMute / Unregister from the audio callback.
        try { if(TryParse(data,out var notification)) notify(notification!,Environment.TickCount64); } catch { }
        return 0;
    }
}
internal sealed class R33Audio : IDisposable
{
    private sealed class Notice {internal Guid Context;internal bool Muted;internal long At;internal int Epoch;}
    private readonly ConcurrentQueue<Notice> pending=new ConcurrentQueue<Notice>();
    private readonly MuteCorrelation correlation=new MuteCorrelation();
    private readonly Guid ownContext=Guid.NewGuid();
    private R33DeviceEnumerator? enumerator;
    private R33Device? device;
    private R33EndpointVolume? endpoint;
    private R33VolumeSink? callback;
    private string endpointId="";
    private int epoch;
    private long refreshed=-10000;
    private int disposed;
    private readonly AudioCallGate callGate;
    internal bool Ready {get{return endpoint!=null;}}
    internal int Corrections {get;private set;}
    internal R33Audio(){callGate=new AudioCallGate(()=>Environment.TickCount64,30000);Refresh(true);}
    private static void Check(int hr){Marshal.ThrowExceptionForHR(hr);}
    private void Refresh(bool force)
    {
        long now=Environment.TickCount64;if(!force&&now-refreshed<1000)return;refreshed=now;
        R33Device? next=null;
        try
        {
            enumerator??=(R33DeviceEnumerator)new R33DeviceEnumeratorClass();
            Check(enumerator.GetDefaultAudioEndpoint(0,0,out next)); // eRender / eConsole only.
            Check(next.GetId(out string id));
            if(endpoint!=null&&id==endpointId)return;
            CloseEndpoint();device=next;next=null;endpointId=id;
            Guid iid=typeof(R33EndpointVolume).GUID;
            Check(device.Activate(ref iid,23,IntPtr.Zero,out object volume));endpoint=(R33EndpointVolume)volume;
            Check(endpoint.GetMute(out bool muted));
            if (callGate.Success("get-mute") == AudioCallResult.Recovered) Log.Info("Audio GetMute recuperado.");
            correlation.Reset(muted);
            int currentEpoch=epoch;
            callback=new R33VolumeSink((notification,t)=>
            {
                if(Volatile.Read(ref disposed)==0&&pending.Count<256)pending.Enqueue(new Notice {Context=notification.EventContext,Muted=notification.Muted,At=t,Epoch=currentEpoch});
            });
            Check(endpoint.RegisterControlChangeNotify(callback));
        }
        catch(Exception ex){CloseEndpoint();if(callGate.Failure("get-mute", ex)==AudioCallResult.FirstFailure)Log.Warn("Compensacao de mudo indisponivel: "+ex.Message);}
        finally {if(next!=null)Marshal.ReleaseComObject(next);}
    }
    internal void Pump()
    {
        if(Volatile.Read(ref disposed)!=0)return;Refresh(false);
        while(pending.TryDequeue(out Notice? n))
        {
            if(n.Epoch!=epoch)continue;
            // Notifications queued while suspended cannot authorize an audio change.
            if(Environment.TickCount64-n.At>250){correlation.Reset(n.Muted);continue;}
            Apply(correlation.Notification(n.At,n.Muted,n.Context==ownContext));
        }
    }
    internal void ApexPress()
    {
        if(Volatile.Read(ref disposed)!=0)return;
        Refresh(true);Pump();
        if(endpoint!=null)Apply(correlation.Click(Environment.TickCount64));
    }
    internal void ForeignPress(){Pump();correlation.Cancel(Environment.TickCount64);}
    private void Apply(MuteFix? fix)
    {
        if(fix==null||endpoint==null)return;
        try
        {
            Check(endpoint.GetMute(out bool actual));
            if (callGate.Success("get-mute") == AudioCallResult.Recovered) Log.Info("Audio GetMute recuperado.");
            if(actual!=fix.Expected){correlation.Reset(actual);return;}
            Guid ctx=ownContext;
            Check(endpoint.SetMute(fix.Desired,ref ctx));
            if (callGate.Success("set-mute") == AudioCallResult.Recovered) Log.Info("Audio SetMute recuperado.");
            correlation.Corrected(fix.Desired);Corrections++;
            Log.Info("Rodinha: estado anterior de mudo restaurado (compensacao, nao bloqueio HID).");
        }
        catch(Exception ex){correlation.Cancel(Environment.TickCount64);if(callGate.Failure("set-mute", ex)==AudioCallResult.FirstFailure)Log.Warn("Nao foi possivel compensar mudo: "+ex.Message);}
    }
    private void CloseEndpoint()
    {
        epoch++;
        var currentEndpoint=endpoint;
        var currentDevice=device;
        var currentCallback=callback;
        endpoint=null;device=null;callback=null;endpointId="";
        try
        {
            if(currentEndpoint!=null&&currentCallback!=null)
            {
                try { currentEndpoint.UnregisterControlChangeNotify(currentCallback); } catch { }
            }
        }
        finally
        {
            try { if(currentEndpoint!=null)Marshal.ReleaseComObject(currentEndpoint); } catch { }
            try { if(currentDevice!=null)Marshal.ReleaseComObject(currentDevice); } catch { }
            while(pending.TryDequeue(out _)) { }
            correlation.Cancel(Environment.TickCount64);
        }
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        try { CloseEndpoint(); }
        finally
        {
            var currentEnumerator=Interlocked.Exchange(ref enumerator,null);
            try { if(currentEnumerator!=null)Marshal.ReleaseComObject(currentEnumerator); } catch { }
        }
    }
}
