using System.Text.Json;

namespace MonitorTray.Core.Normalize;

/// <summary>known-devices.json: IDs already seen, for the NEW tag (docs/05 §1).</summary>
public sealed class KnownDevices
{
    private readonly string path;
    private readonly HashSet<string> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> seenThisSession = new(StringComparer.Ordinal);
    private readonly bool firstRun;

    public KnownDevices(string path)
    {
        this.path = path;
        try
        {
            if (File.Exists(path))
            {
                var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [];
                foreach (var id in ids) if (!string.IsNullOrWhiteSpace(id)) known.Add(id);
            }
            else firstRun = true;
        }
        catch (Exception) { firstRun = known.Count == 0; }
    }

    public bool IsNew(string id)
    {
        lock (known)
        {
            seenThisSession.Add(id);
            return !firstRun && !known.Contains(id);
        }
    }

    /// <summary>Writes all seen IDs (called when Details closes and on exit).</summary>
    public void Save()
    {
        string[] ids;
        lock (known)
        {
            if (seenThisSession.IsSubsetOf(known) && !firstRun) return;
            known.UnionWith(seenThisSession);
            ids = known.Order(StringComparer.Ordinal).ToArray();
        }
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(ids, new JsonSerializerOptions { WriteIndented = true }));
    }
}
