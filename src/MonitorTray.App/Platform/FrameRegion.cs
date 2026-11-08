using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MonitorTray.App.Platform;

/// <summary>
/// Recorta a janela (e o blur do DWM) ao retângulo da moldura.
/// Sem isso, a margem transparente apareceria como um halo desfocado.
/// </summary>
public static class FrameRegion
{
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

    /// <param name="margin">Margem (DIP) entre a borda da janela e a moldura.</param>
    public static void Apply(Window w, double margin, bool clip)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        if (!clip) { SetWindowRgn(h, IntPtr.Zero, true); return; }
        var s = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int P(double v) => (int)Math.Round(v * s);
        double W = w.ActualWidth, H = w.ActualHeight;
        if (W <= 0 || H <= 0) return;
        int l = P(margin), t = P(margin), r = P(W - margin), b = P(H - margin);
        Set(h, l, t, r, b);
    }

    /// <summary>
    /// Recorta ao retângulo do <paramref name="frame"/> como o WPF o desenhou, arredondado para dentro: nenhum pixel da janela
    /// fica fora da moldura (em DPI fracionário a margem arredondada à parte podia deixar 1 px de blur sem fundo, uma linha clara).
    /// </summary>
    /// <returns>false = ainda sem layout/handle (nada aplicado).</returns>
    public static bool Apply(Window w, FrameworkElement frame)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero || frame.ActualWidth <= 0 || frame.ActualHeight <= 0 || !frame.IsDescendantOf(w)) return false;
        var s = VisualTreeHelper.GetDpi(w).DpiScaleX;
        var rc = frame.TransformToAncestor(w).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight));
        const double Eps = 0.01;
        int l = (int)Math.Ceiling(rc.Left * s - Eps), t = (int)Math.Ceiling(rc.Top * s - Eps);
        int r = (int)Math.Floor(rc.Right * s + Eps), b = (int)Math.Floor(rc.Bottom * s + Eps);
        if (r <= l || b <= t) return false;
        Set(h, l, t, r, b);
        return true;
    }

    private static void Set(IntPtr h, int l, int t, int r, int b)
    {
        var rgn = CreateRectRgn(l, t, r, b);
        if (SetWindowRgn(h, rgn, true) == 0) DeleteObject(rgn); // em sucesso o sistema é dono da região
    }
}
