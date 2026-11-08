using System.Drawing;
using Microsoft.Win32;

namespace MonitorTray.App.Tray;

/// <summary>Ícone fixo da bandeja (pulso): branco na barra escura, quase preto na clara. Troca junto com o tema do Windows.</summary>
public static class TrayIcons
{
    private static readonly Dictionary<(bool Light, int Px), Icon> cache = new();

    public static bool TaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch (Exception) { return false; }
    }

    private static Icon Load(string name, int px)
    {
        var s = System.Windows.Application.GetResourceStream(new Uri($"pack://application:,,,/MonitorTray;component/Assets/{name}.ico"))!.Stream;
        using (s) return new Icon(s, px, px);
    }

    public static Icon Current() => For(TaskbarIsLight());

    /// <summary>Ícone fixo no tamanho pedido (16 = o da bandeja; 24/32 para recolorir em DPI alto sem borrar).</summary>
    public static Icon For(bool lightTaskbar, int px = 16)
    {
        lock (cache)
        {
            if (!cache.TryGetValue((lightTaskbar, px), out var i)) cache[(lightTaskbar, px)] = i = Load(lightTaskbar ? "tray_light" : "tray_dark", px);
            return i;
        }
    }
}
