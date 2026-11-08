namespace MonitorTray.Core.Fans;

public enum AutoGameAction { None, TurnOn, TurnOff }

/// <summary>
/// Modo game automático por jogo (lógica pura; o app consulta os processos a cada ~3 s e chama <see cref="Step"/>).
/// Um executável da lista rodando liga o modo game; quando todos fecham, desliga — só se foi o automático que ligou.
/// Ligado na mão nunca é desligado pelo automático. Desligado na mão com o jogo aberto: não religa até todos fecharem.
/// </summary>
public sealed class AutoGame
{
    /// <summary>O modo game atual foi ligado pelo automático.</summary>
    public bool Owned { get; private set; }
    /// <summary>Executável que ligou (para a faixa do popup).</summary>
    public string? Game { get; private set; }
    private bool suppressed;

    /// <summary>"C:\Games\CS2.EXE", "cs2.exe" ou "cs2" → "cs2" (como Process.ProcessName, sem caixa).</summary>
    public static string Normalize(string exe)
    {
        var s = exe.Trim().Trim('"');
        s = s[(Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/')) + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>Texto digitado nas Configurações → "nome.exe" (minúsculo) ou null se não for um nome de executável.</summary>
    public static string? Clean(string input)
    {
        var n = Normalize(input);
        if (n.Length is 0 or > 59 || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.Contains('*') || n.Contains('?')) return null;
        return n + ".exe";
    }

    /// <summary>Primeiro executável da lista que está rodando (nome como na lista), ou null.</summary>
    public static string? Match(IEnumerable<string> running, IReadOnlyList<string> exes)
    {
        if (exes.Count == 0) return null;
        var set = new HashSet<string>(running.Select(Normalize), StringComparer.Ordinal);
        return exes.FirstOrDefault(e => set.Contains(Normalize(e)));
    }

    /// <param name="running">Nomes dos processos rodando agora.</param>
    /// <param name="exes">Lista das Configurações.</param>
    /// <param name="gameOn">Modo game ligado agora (por qualquer caminho).</param>
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
        if (Owned && !gameOn) { Owned = false; Game = null; suppressed = true; return AutoGameAction.None; } // desligado por fora durante o jogo
        if (Owned) { Game = match; return AutoGameAction.None; }
        if (gameOn || suppressed) return AutoGameAction.None; // ligado na mão: não é nosso
        Owned = true; Game = match;
        return AutoGameAction.TurnOn;
    }

    /// <summary>O usuário clicou no modo game: daqui em diante o estado é dele.</summary>
    public void ManualToggle(bool nowOn, bool gameRunning)
    {
        Owned = false; Game = null;
        suppressed = !nowOn && gameRunning;
    }

    /// <summary>Modo game restaurado do disco ao abrir o app (ligado pelo automático antes de fechar).</summary>
    public void Restore(bool owned, string? game) { Owned = owned; Game = owned ? game : null; }
}
