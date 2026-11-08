using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using MonitorTray.App.Theming;

namespace MonitorTray.App.Controls;

/// <summary>Frame: 1 px border, radius 0, Bg background with alpha. No "+" marks on the corners (user request; deviates from the kit).</summary>
public sealed class BlueprintFrame : Grid
{
    public Border Body { get; }

    /// <param name="outline">
    /// false = no outline (popup): no border at all, only the background, with 1 px of padding so the size does not change.
    /// A border in the background color does not work: at fractional DPI (125%, 150%) the border/background seam gets a lower alpha
    /// and the blur shows up as a light line around it.
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
            // The shadow is drawn by the opaque rectangle behind; the semi-transparent body sits on top.
            // So the opaque background does not "leak" through the opacity, we clip the shadow to the outside.
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
        // Outer region: a large rectangle minus the inside of the body.
        var w = sh.ActualWidth; var h = sh.ActualHeight; if (w <= 0) return;
        var outer = new RectangleGeometry(new Rect(-60, -60, w + 120, h + 120));
        var inner = new RectangleGeometry(new Rect(0, 0, w, h));
        sh.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
    }
}
