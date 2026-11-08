using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace MonitorTray.App.Platform;

/// <summary>
/// Início e elevação. O instalador registra duas tarefas agendadas da conta (sessão interativa, privilégio mais alto, nunca SYSTEM):
///  - "MonitorTray-&lt;SID&gt;": sob demanda, sem gatilho. Atalhos e o próprio exe a usam para abrir elevado sem UAC.
///  - "MonitorTray-Logon-&lt;SID&gt;": no logon, com --tray. O toggle "Iniciar com o Windows" habilita/desabilita só esta.
/// Sem instalação (execução portátil) o autostart cai na chave Run, que não eleva.
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

    /// <summary>Executável dentro de Program Files\MonitorTray (instalado), e não uma cópia de build/teste.</summary>
    public static bool IsInstalledCopy()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MonitorTray") + Path.DirectorySeparatorChar;
        return (Environment.ProcessPath ?? "").StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Cópia instalada aberta sem elevação (atalho): reabre pela tarefa elevada. true = a tarefa assumiu; este processo sai.</summary>
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
