using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MonitorTray.App.Platform;

/// <summary>
/// Named event "Local\MonitorTray.Quit.&lt;SID&gt;" (same pattern as CmpInf): the (non-elevated) installer
/// signals it and the app runs the normal quit, which returns the fans to automatic. SDDL: owner = user,
/// SYSTEM/Admins full access, the user itself only SYNCHRONIZE|EVENT_MODIFY_STATE; medium integrity label.
/// </summary>
public static class QuitSignal
{
    public static string Name => @"Local\MonitorTray.Quit." + (WindowsIdentity.GetCurrent().User?.Value ?? "x");

    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int rev, out IntPtr sd, out uint size);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initial, string name);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private sealed class Handle : EventWaitHandle
    {
        public Handle(IntPtr h) : base(false, EventResetMode.AutoReset) { SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(h, true); }
    }

    public static EventWaitHandle? Create()
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            var sddl = "O:" + sid + "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00100002;;;" + sid + ")S:(ML;;NW;;;ME)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out sd, out _)) return null;
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            var h = CreateEventW(ref sa, false, false, Name);
            var err = Marshal.GetLastWin32Error();
            if (h == IntPtr.Zero) return null;
            var handle = new Handle(h);
            if (err == 183) { handle.Dispose(); return null; } // already exists with another owner
            return handle;
        }
        catch (Exception) { return null; }
        finally { if (sd != IntPtr.Zero) LocalFree(sd); }
    }

    public static void Watch(EventWaitHandle h, Action onQuit)
    {
        var t = new Thread(() =>
        {
            try { while (true) { h.WaitOne(); onQuit(); } } catch (Exception) { }
        }) { IsBackground = true, Name = "QuitSignal" };
        t.Start();
    }
}
