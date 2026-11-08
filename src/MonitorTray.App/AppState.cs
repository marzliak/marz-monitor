using System.IO;
using MonitorTray.Core.Model;

namespace MonitorTray.App;

/// <summary>Configurações vivas + persistência. Mudanças são aplicadas na hora e salvas (debounce 300 ms).</summary>
public sealed class AppState
{
    private readonly SettingsStore store;
    public Settings Settings { get; private set; }
    public event Action<Settings, Settings>? Changed;
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorTray");

    public AppState(string? dir = null)
    {
        store = new SettingsStore(Path.Combine(dir ?? DataDir, "settings.json"));
        Settings = store.Load();
    }

    public void Update(Func<Settings, Settings> f)
    {
        var old = Settings;
        var next = f(old);
        if (next == old) return;
        Settings = next;
        store.Save(next);
        Changed?.Invoke(old, next);
    }

    public void Flush() => store.Flush();
}
