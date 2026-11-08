using System.IO;
using System.Text.Json;

namespace MonitorTray.App;

/// <summary>
/// The user's latest fan choices (state\fan-choices.json), reapplied when the app opens and when game mode ends:
/// board mode, board fans in OFF/MANUAL and GPU preset. On exit the app still returns everything to safe
/// (GPU automatic, board on the User curves); only the choice is stored. Game mode is never written here.
/// </summary>
public static class FanChoices
{
    public sealed record Data
    {
        /// <summary>Board mode (curves/silent/standard/turbo/full); null = never chosen.</summary>
        public string? BoardMode { get; init; }
        /// <summary>GPU preset in %; null = automatic.</summary>
        public uint? Gpu { get; init; }
        public List<string> Off { get; init; } = [];
        public Dictionary<string, int> Manual { get; init; } = new();
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Path { get; set; } = System.IO.Path.Combine(AppState.DataDir, "state", "fan-choices.json");
    /// <summary>Off on app exit and in the preview: nothing is written.</summary>
    public static bool Enabled { get; set; } = true;

    public static Data Load()
    {
        lock (Gate)
        {
            try { return File.Exists(Path) ? Parse(File.ReadAllText(Path)) : new(); }
            catch (Exception) { return new(); }
        }
    }

    /// <summary>Reads the file; the old board mode id "curvas" (up to 0.1.33) becomes "curves".</summary>
    public static Data Parse(string json)
    {
        try { return JsonSerializer.Deserialize<Data>(json, Json) is { } d ? d with { BoardMode = MonitorTray.Core.Model.Settings.MigrateAcMode(d.BoardMode) } : new(); }
        catch (Exception) { return new(); }
    }

    public static void Update(Func<Data, Data> f)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            try { MonitorTray.Core.AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(f(Load()), Json)); } catch (Exception) { }
        }
    }
}
