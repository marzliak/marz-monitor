using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MonitorTray.App.Theming;

namespace MonitorTray.App.Controls;

/// <summary>Moldura: borda 1 px raio 0, fundo Bg com alpha. Sem marcas "+" nos vértices (pedido do usuário; desvio do kit).</summary>
public sealed class BlueprintFrame : Grid
{
    public Border Body { get; }

    /// <param name="outline">
    /// false = sem contorno (popup): sem borda nenhuma, só o fundo, com 1 px de padding para o tamanho não mudar.
    /// Uma borda da mesma cor do fundo não serve: em DPI fracionário (125%, 150%) a emenda borda/fundo sai com alpha menor
    /// e o blur aparece como uma linha clara em volta.
    /// </param>
    public BlueprintFrame(UIElement content, bool shadow = true, bool outline = true)
    {
        ClipToBounds = false;
        SnapsToDevicePixels = true;
        if (shadow)
        {
            var sh = new Border { Background = Brushes.Black, Opacity = 1, IsHitTestVisible = false };
            sh.SetResourceReference(Border.BackgroundProperty, Theme.Bg);
            sh.Effect = new DropShadowEffect { BlurRadius = 32, ShadowDepth = 12, Direction = 270, Opacity = 0.22, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
            sh.Margin = new Thickness(1);
            Children.Add(sh);
            // A sombra é desenhada pelo retângulo opaco atrás; o corpo semitransparente fica por cima.
            // Para não "vazar" o fundo opaco através da opacidade, recortamos a sombra ao exterior.
            sh.Loaded += (_, _) => ClipShadow(sh);
            sh.SizeChanged += (_, _) => ClipShadow(sh);
        }
        Body = new Border { BorderThickness = new Thickness(outline ? 1 : 0), Padding = new Thickness(outline ? 0 : 1), Child = content, SnapsToDevicePixels = true };
        Body.SetResourceReference(Border.BackgroundProperty, Theme.BgAlpha);
        if (outline) Body.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        Children.Add(Body);
    }

    private static void ClipShadow(FrameworkElement sh)
    {
        // Região externa: retângulo grande menos o interior do corpo.
        var w = sh.ActualWidth; var h = sh.ActualHeight; if (w <= 0) return;
        var outer = new RectangleGeometry(new Rect(-60, -60, w + 120, h + 120));
        var inner = new RectangleGeometry(new Rect(0, 0, w, h));
        sh.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
    }
}
