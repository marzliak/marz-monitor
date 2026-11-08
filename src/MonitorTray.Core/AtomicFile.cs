using System.Text;

namespace MonitorTray.Core;

public static class AtomicFile
{
    /// <summary>Writes to a temporary file in the same directory and swaps it in (File.Replace / Move).</summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        try
        {
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
