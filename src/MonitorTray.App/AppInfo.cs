using System.Reflection;

namespace MonitorTray.App;

/// <summary>Identidade do build, lida do assembly (Directory.Build.props é a fonte única da versão).</summary>
public static class AppInfo
{
    private static readonly Assembly Asm = typeof(AppInfo).Assembly;
    public const string Name = "Marz Monitor";
    public const string Publisher = "MarzOps";
    public static string Version => (Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];
    public static string Commit => (Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "").Split('+') is { Length: > 1 } p ? p[1][..Math.Min(7, p[1].Length)] : "local";
    public static string BuildDate => Meta("BuildDateUtc") ?? "";
    public static string Repository => Meta("RepositoryUrl") ?? "";
    public static string Copyright => Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "© MarzOps";
    public static string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public static string Os => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    private static string? Meta(string key) => Asm.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
}
