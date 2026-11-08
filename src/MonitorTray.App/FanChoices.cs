using System.IO;
using System.Text.Json;

namespace MonitorTray.App;

/// <summary>
/// Últimas escolhas de fan do usuário (state\fan-choices.json), reaplicadas ao abrir o app e ao sair do modo game:
/// modo da placa, fans da placa em OFF/MANUAL e preset da GPU. Ao sair o app continua devolvendo tudo ao seguro
/// (GPU automática, placa nas curvas User); só a escolha fica guardada. O modo game nunca é gravado aqui.
/// </summary>
public static class FanChoices
{
    public sealed record Data
    {
        /// <summary>Modo da placa (curvas/silent/standard/turbo/full); null = nunca escolhido.</summary>
        public string? BoardMode { get; init; }
        /// <summary>Preset da GPU em %; null = automático.</summary>
        public uint? Gpu { get; init; }
        public List<string> Off { get; init; } = [];
        public Dictionary<string, int> Manual { get; init; } = new();
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Path { get; set; } = System.IO.Path.Combine(AppState.DataDir, "state", "fan-choices.json");
    /// <summary>Desligado na saída do app e na prévia: nada é gravado.</summary>
    public static bool Enabled { get; set; } = true;

    public static Data Load()
    {
        lock (Gate)
        {
            try { return File.Exists(Path) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(Path), Json) ?? new() : new(); }
            catch (Exception) { return new(); }
        }
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
