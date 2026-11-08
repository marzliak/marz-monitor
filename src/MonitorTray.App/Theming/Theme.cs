using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using MonitorTray.Core.Model;

namespace MonitorTray.App.Theming;

public sealed record PaletteInfo(string Id, string Name, Color A, Color B, Color[] Dark, Color[] Light);

/// <summary>Theme, palette and opacity as dynamic resources (docs/03, spec/tokens.json).</summary>
public static class Theme
{
    public const string Bg = "Bg", BgAlpha = "BgAlpha", Text = "Text", Muted = "Muted", Line = "Line", Track = "Track",
        Fill = "Fill", DStroke = "DStroke", UStroke = "UStroke", DFill = "DFill", UFill = "UFill", Hover = "Hover", Sel = "Sel",
        PA = "PA", PB = "PB", Mark = "Mark", TitleGlyph = "TitleGlyph";

    private static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        byte P(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
        return hex.Length == 8 ? Color.FromArgb(P(6), P(0), P(2), P(4)) : Color.FromRgb(P(0), P(2), P(4));
    }

    // fill, dstroke, ustroke, dfill, ufill, hover, sel — values precomputed from spec/tokens.json
    public static readonly IReadOnlyList<PaletteInfo> Palettes =
    [
        new("electric", "Electric", C("#2E7DFF"), C("#00E5C3"),
            [C("#2E7DFF"), C("#2E7DFF"), C("#00E5C3"), C("#2E7DFF4D"), C("#00E5C342"), C("#2E7DFF24"), C("#2E7DFF3D")],
            [C("#2568D7"), C("#2568D7"), C("#008E78"), C("#2E7DFF3D"), C("#00E5C34D"), C("#2E7DFF1A"), C("#2E7DFF2E")]),
        new("neon", "Neon", C("#00C8FF"), C("#FF4FD8"),
            [C("#00C8FF"), C("#00C8FF"), C("#FF4FD8"), C("#00C8FF4D"), C("#FF4FD842"), C("#00C8FF24"), C("#00C8FF3D")],
            [C("#00A8D7"), C("#00A8D7"), C("#9E2E85"), C("#00C8FF3D"), C("#FF4FD84D"), C("#00C8FF1A"), C("#00C8FF2E")]),
        new("lime", "Lime", C("#2FE07A"), C("#E8FF3A"),
            [C("#2FE07A"), C("#2FE07A"), C("#E8FF3A"), C("#2FE07A4D"), C("#E8FF3A42"), C("#2FE07A24"), C("#2FE07A3D")],
            [C("#26BD66"), C("#26BD66"), C("#909E20"), C("#2FE07A3D"), C("#E8FF3A4D"), C("#2FE07A1A"), C("#2FE07A2E")]),
        new("ember", "Ember", C("#FF5A1F"), C("#FFC531"),
            [C("#FF5A1F"), C("#FF5A1F"), C("#FFC531"), C("#FF5A1F4D"), C("#FFC53142"), C("#FF5A1F24"), C("#FF5A1F3D")],
            [C("#D74B18"), C("#D74B18"), C("#9E791A"), C("#FF5A1F3D"), C("#FFC5314D"), C("#FF5A1F1A"), C("#FF5A1F2E")]),
        new("violet", "Violet", C("#8B5CFF"), C("#FF5C93"),
            [C("#8B5CFF"), C("#8B5CFF"), C("#FF5C93"), C("#8B5CFF4D"), C("#FF5C9342"), C("#8B5CFF24"), C("#8B5CFF3D")],
            [C("#744CD7"), C("#744CD7"), C("#9E3659"), C("#8B5CFF3D"), C("#FF5C934D"), C("#8B5CFF1A"), C("#8B5CFF2E")]),
        new("steel", "Steel", C("#5980A6"), C("#94BCE3"),
            [C("#5980A6"), C("#5980A6"), C("#94BCE3"), C("#5980A64D"), C("#94BCE342"), C("#5980A624"), C("#5980A63D")],
            [C("#4A6B8B"), C("#4A6B8B"), C("#5A738C"), C("#5980A63D"), C("#94BCE34D"), C("#5980A61A"), C("#5980A62E")]),
    ];

    public static PaletteInfo Palette(string id) => Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];

    public static bool SystemIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception) { return false; }
    }

    public static bool IsDark(Settings s) => s.Theme == "dark" || (s.Theme == "system" && !SystemIsLight());

    public static Color Bgc(bool dark) => dark ? C("#18191B") : C("#F2F2F3");

    public static void Apply(ResourceDictionary r, Settings s)
    {
        var dark = IsDark(s);
        var p = Palette(s.Palette);
        var bg = Bgc(dark);
        var text = dark ? C("#F5F5F8") : C("#1D1F20");
        void Set(string key, Color c)
        {
            if (r[key] is SolidColorBrush b && !b.IsFrozen) b.Color = c;
            else r[key] = new SolidColorBrush(c);
        }
        Set(Bg, bg);
        Set(BgAlpha, Color.FromArgb((byte)Math.Round(s.Opacity / 100.0 * 255), bg.R, bg.G, bg.B));
        Set(Text, text);
        Set(Muted, dark ? C("#A3A3A8") : C("#5D5D60"));
        Set(Line, dark ? C("#F5F5F821") : C("#1D1F2026"));
        Set(Track, dark ? C("#F5F5F817") : C("#1D1F2017"));
        Set(Mark, Color.FromArgb(140, text.R, text.G, text.B));
        var v = dark ? p.Dark : p.Light;
        Set(Fill, v[0]); Set(DStroke, v[1]); Set(UStroke, v[2]); Set(DFill, v[3]); Set(UFill, v[4]); Set(Hover, v[5]); Set(Sel, v[6]);
        Set(PA, p.A); Set(PB, p.B);
        var glyph = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        glyph.GradientStops.Add(new GradientStop(p.A, 0)); glyph.GradientStops.Add(new GradientStop(p.A, 0.5));
        glyph.GradientStops.Add(new GradientStop(p.B, 0.5)); glyph.GradientStops.Add(new GradientStop(p.B, 1));
        r[TitleGlyph] = glyph;
    }

    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];

    public static Brush Diagonal(Color a, Color b)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop(a, 0)); g.GradientStops.Add(new GradientStop(a, 0.5));
        g.GradientStops.Add(new GradientStop(b, 0.5)); g.GradientStops.Add(new GradientStop(b, 1));
        g.Freeze();
        return g;
    }
}

/// <summary>
/// Embedded Barlow. WPF only finds extra weights by the Win32 name of each file
/// ("Barlow Condensed SemiBold"), so the family is chosen by weight.
/// </summary>
public static class AppFonts
{
    private static readonly Uri Base = new("pack://application:,,,/MonitorTray;component/");
    public static readonly FontFamily Head = new(Base, "./Fonts/#Barlow Condensed");
    public static readonly FontFamily HeadMedium = new(Base, "./Fonts/#Barlow Condensed Medium");
    public static readonly FontFamily HeadSemiBold = new(Base, "./Fonts/#Barlow Condensed SemiBold");
    public static readonly FontFamily Body = new(Base, "./Fonts/#Barlow");
    public static readonly FontFamily BodyMedium = new(Base, "./Fonts/#Barlow Medium");

    public static FontFamily For(bool head, int weight) => head
        ? weight >= 600 ? HeadSemiBold : weight >= 500 ? HeadMedium : Head
        : weight >= 500 ? BodyMedium : Body;
}
