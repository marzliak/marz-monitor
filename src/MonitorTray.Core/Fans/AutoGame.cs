namespace MonitorTray.Core.Fans;

public enum AutoGameAction { None, TurnOn, TurnOff }

/// <summary>
/// Automatic per-game game mode (pure logic; the app polls processes every ~3 s and calls <see cref="Step"/>).
/// A listed executable running turns game mode on; when all of them close, it turns off — only if automatic turned it on.
/// Turned on by hand is never turned off by automatic. Turned off by hand with the game open: not turned back on until all close.
/// </summary>
public sealed class AutoGame
{
    /// <summary>The current game mode was turned on by automatic.</summary>
    public bool Owned { get; private set; }
    /// <summary>Executable that turned it on (for the popup strip).</summary>
    public string? Game { get; private set; }
    private bool suppressed;

    /// <summary>"C:\Games\CS2.EXE", "cs2.exe" or "cs2" → "cs2" (like Process.ProcessName, lowercased).</summary>
    public static string Normalize(string exe)
    {
        var s = exe.Trim().Trim('"');
        s = s[(Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/')) + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>Text typed in Settings → "name.exe" (lowercase) or null if it is not an executable name.</summary>
    public static string? Clean(string input)
    {
        var n = Normalize(input);
        if (n.Length is 0 or > 59 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.Contains('*') || n.Contains('?')) return null;
        return n + ".exe";
    }

    /// <summary>First listed executable that is running (name as in the list), or null.</summary>
    public static string? Match(IEnumerable<string> running, IReadOnlyList<string> exes)
    {
        if (exes.Count == 0) return null;
        var set = new HashSet<string>(running.Select(Normalize), StringComparer.Ordinal);
        return exes.FirstOrDefault(e => set.Contains(Normalize(e)));
    }

    /// <param name="running">Names of the processes running now.</param>
    /// <param name="exes">List from Settings.</param>
    /// <param name="gameOn">Game mode on right now (by any path).</param>
    public AutoGameAction Step(IEnumerable<string> running, IReadOnlyList<string> exes, bool gameOn)
    {
        var match = Match(running, exes);
        if (match == null)
        {
            suppressed = false;
            if (!Owned) return AutoGameAction.None;
            Owned = false; Game = null;
            return gameOn ? AutoGameAction.TurnOff : AutoGameAction.None;
        }
        if (Owned && !gameOn) { Owned = false; Game = null; suppressed = true; return AutoGameAction.None; } // turned off externally during the game
        if (Owned) { Game = match; return AutoGameAction.None; }
        if (gameOn || suppressed) return AutoGameAction.None; // turned on by hand: not ours
        Owned = true; Game = match;
        return AutoGameAction.TurnOn;
    }

    /// <summary>The user clicked game mode: from here on the state is theirs.</summary>
    public void ManualToggle(bool nowOn, bool gameRunning)
    {
        Owned = false; Game = null;
        suppressed = !nowOn && gameRunning;
    }

    /// <summary>Game mode restored from disk when the app starts (turned on by automatic before it closed).</summary>
    public void Restore(bool owned, string? game) { Owned = owned; Game = owned ? game : null; }
}
