using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace MonitorTray.App.Platform;

/// <summary>
/// Startup and elevation. The installer registers two scheduled tasks for the account (interactive session, highest privileges, never SYSTEM):
///  - "MonitorTray-&lt;SID&gt;": on demand, no trigger. Shortcuts and the exe itself use it to open elevated without UAC.
///  - "MonitorTray-Logon-&lt;SID&gt;": at logon, with --tray. The "Start with Windows" toggle enables/disables only this one.
/// Without installation (portable run) autostart falls back to the Run key, which does not elevate.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "MonitorTray";

    public static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
    public static string LaunchTask => "MonitorTray-" + Sid;
    public static string LogonTask => "MonitorTray-Logon-" + Sid;

    public static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception) { return false; }
    }

    private static (int Code, string Out) Schtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    public static bool TaskExists(string name) => Schtasks($"/Query /TN \"{name}\"").Code == 0;

    /// <summary>Executable inside Program Files\MonitorTray (installed), not a build/test copy.</summary>
    public static bool IsInstalledCopy()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MonitorTray") + Path.DirectorySeparatorChar;
        return (Environment.ProcessPath ?? "").StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Installed copy opened without elevation (shortcut): reopens through the elevated task. true = the task took over; this process exits.</summary>
    public static bool TryRelaunchElevated()
    {
        if (IsAdmin() || !IsInstalledCopy()) return false;
        return Schtasks($"/Run /TN \"{LaunchTask}\"").Code == 0;
    }

    public static void Apply(bool enabled)
    {
        if (TaskExists(LogonTask))
        {
            Schtasks($"/Change /TN \"{LogonTask}\" {(enabled ? "/ENABLE" : "/DISABLE")}");
            SetRun(false);
            return;
        }
        SetRun(enabled);
    }

    private static void SetRun(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
        }
        catch (Exception) { }
    }

    public static bool RestartAsAdmin(string args)
    {
        try
        {
            if (IsInstalledCopy() && Schtasks($"/Run /TN \"{LaunchTask}\"").Code == 0) return true;
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Exception) { return false; }
    }
}
