namespace MonitorTray.Oled;

/// <summary>Log curto do teclado em %APPDATA%\MonitorTray\logs\keyboard.log (limite 512 KB, depois recomeça).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string PathName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorTray", "logs", "keyboard.log");
    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
                var fi = new FileInfo(PathName);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {level} {msg}\r\n");
            }
        }
        catch (Exception) { }
    }
    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Debug(string m) { }
}
