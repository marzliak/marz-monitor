using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MonitorTray.App.Controls;

/// <summary>Ícones Lucide (stroke 1.5, viewBox 24) — mesmos paths do protótipo.</summary>
public static class Icons
{
    public const string Settings = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M15 12a3 3 0 1 1-6 0a3 3 0 1 1 6 0z";
    public const string X = "M18 6 6 18M6 6l12 12";
    public const string ChevronLeft = "M15 18 9 12 15 6";
    public const string ArrowDown = "M12 5v14M19 12l-7 7-7-7";
    public const string ArrowUp = "M12 19V5M5 12l7-7 7 7";
    public const string ExternalLink = "M15 3h6v6M10 14 21 3M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6";
    public const string Search = "M18 11a7 7 0 1 1-14 0a7 7 0 1 1 14 0z M20 20 16.5 16.5";
    public const string Maximize = "M8 3H5a2 2 0 0 0-2 2v3M21 8V5a2 2 0 0 0-2-2h-3M3 16v3a2 2 0 0 0 2 2h3M16 21h3a2 2 0 0 0 2-2v-3";
    public const string Minimize = "M8 3v3a2 2 0 0 1-2 2H3M21 8h-3a2 2 0 0 1-2-2V3M3 16h3a2 2 0 0 1 2 2v3M16 21v-3a2 2 0 0 1 2-2h3";
    public const string Check = "M20 6 9 17l-5-5";
    public const string Thermometer = "M14 4v10.54a4 4 0 1 1-4 0V4a2 2 0 0 1 4 0Z";
    public const string ChevronRight = "M9 18 15 12 9 6";
    public const string ChevronUp = "M18 15 12 9 6 15";
    public const string ChevronDown = "M6 9 12 15 18 9";
    public const string Lock = "M6 11h12a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z M8 11V7a4 4 0 0 1 8 0v4";
    public const string Pencil = "M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z M15 5 19 9";
    public const string Pin = "M12 17v5 M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V7a1 1 0 0 1 1-1 2 2 0 0 0 0-4H8a2 2 0 0 0 0 4 1 1 0 0 1 1 1z";
    public const string Gamepad = "M6 11h4 M8 9v4 M15 12h.01 M18 10h.01 M17.32 5H6.68a4 4 0 0 0-3.978 3.59c-.006.052-.01.101-.017.152C2.604 9.416 2 14.456 2 16a3 3 0 0 0 3 3c1 0 1.5-.5 2-1l1.414-1.414A2 2 0 0 1 9.828 16h4.344a2 2 0 0 1 1.414.586L17 18c.5.5 1 1 2 1a3 3 0 0 0 3-3c0-1.545-.604-6.584-.685-7.258-.007-.05-.011-.1-.017-.151A4 4 0 0 0 17.32 5z";
    public const string Fan = "M10.827 16.379a6.082 6.082 0 0 1-8.618-7.002l5.412 1.45a6.082 6.082 0 0 1 7.002-8.618l-1.45 5.412a6.082 6.082 0 0 1 8.618 7.002l-5.412-1.45a6.082 6.082 0 0 1-7.002 8.618l1.45-5.412Z M12 12v.01";

    public const string Info = "M22 12a10 10 0 1 1-20 0a10 10 0 1 1 20 0z M12 16v-4 M12 8h.01";
    public const string Power = "M12 2v10 M18.4 6.6a9 9 0 1 1-12.77.04";
    public const string Shield = "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z";

    private static readonly Dictionary<string, Geometry> Cache = new();

    public static Geometry Geo(string data)
    {
        if (!Cache.TryGetValue(data, out var g)) { g = Geometry.Parse(data); g.Freeze(); Cache[data] = g; }
        return g;
    }

    /// <summary>Ícone cujo traço vem de um recurso do tema (atualiza com tema/paleta).</summary>
    public static (FrameworkElement Root, Path Path) Make(string data, double size, string brushKey)
    {
        var path = new Path
        {
            Data = Geo(data), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.None,
        };
        path.SetResourceReference(Shape.StrokeProperty, brushKey);
        var root = new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
        return (root, path);
    }
}
