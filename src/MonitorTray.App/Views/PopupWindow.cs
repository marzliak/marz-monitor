using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Platform;

namespace MonitorTray.App.Views;

/// <summary>Popup ancorado acima do relógio. Topmost, fora do Alt+Tab, some ao perder foco (opcional).</summary>
public sealed class PopupWindow : Window
{
    /// <summary>Sem margem fora da moldura: a margem transparente mostrava o acrílico como uma borda clara.</summary>
    public const double Margin6 = 0;
    private readonly AppState app;
    public PopupContent Content2 { get; }
    public DateTime HiddenByDeactivateAt { get; private set; }
    private Native.POINT? anchor;
    /// <summary>Usuário arrastou o popup: fica onde foi deixado (nesta sessão) em vez de voltar para o relógio.</summary>
    private bool moved;

    private static bool IsInteractive(DependencyObject? d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x is FlatButton) return true;
        return false;
    }

    public PopupWindow(AppState app)
    {
        this.app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Marz Monitor";
        UseLayoutRounding = true;
        Content2 = new PopupContent(app);
        var frame = new BlueprintFrame(Content2, shadow: false, outline: false) { Margin = new Thickness(Margin6) };
        Content = frame;
        Content2.CloseRequested += () => HideNow();
        Content2.Header.MouseLeftButtonDown += (_, e) =>
        {
            if (IsInteractive(e.OriginalSource as DependencyObject)) return;
            if (e.ClickCount == 2) { moved = false; Reposition(); return; }
            try { DragMove(); moved = true; } catch (InvalidOperationException) { }
        };
        Content2.LayoutChanged += () => Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        SourceInitialized += (_, _) => { Native.ToolWindow(this); Native.SquareCorners(this); Native.NoBorder(this); ApplyBlur(); };
        // Região pelo retângulo real da moldura (depois do layout), não pela margem: sem pixel de blur sem fundo nas bordas.
        void Clip() { if (!FrameRegion.Apply(this, frame)) FrameRegion.Apply(this, Margin6, true); }
        SizeChanged += (_, _) => { Clip(); Reposition(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { Clip(); Reposition(); }, System.Windows.Threading.DispatcherPriority.Loaded);
        Deactivated += (_, _) =>
        {
            if (app.Settings.Autohide && IsVisible) { HiddenByDeactivateAt = DateTime.UtcNow; HideNow(); }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { if (Content2.InSettings) Content2.ShowSettings(false); else HideNow(); } };
        app.Changed += (_, s) => { Content2.ApplySettings(s); ApplyBlur(); };
    }

    public void ApplyBlur() => Native.Blur(this, app.Settings.Opacity < 100 && Native.TransparencyEffectsEnabled());

    public void ShowAt(Native.POINT? clickPoint, bool settings = false)
    {
        anchor = clickPoint;
        Content2.ShowSettings(settings);
        if (!IsVisible) Show();
        Reposition();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    public void HideNow()
    {
        Hide();
        Content2.ShowSettings(false);
    }

    public void Reposition()
    {
        if (!IsLoaded || moved) return;
        var place = Taskbar.Current(anchor);
        var s = place.Scale;
        var fw = (ActualWidth - 2 * Margin6) * s; var fh = (ActualHeight - 2 * Margin6) * s;
        const double M = 12;
        double x = place.WorkPx.Right - M * s - fw, y = place.WorkPx.Bottom - M * s - fh;
        if (place.Edge == Edge.Top) y = place.WorkPx.Top + M * s;
        if (place.Edge == Edge.Left) x = place.WorkPx.Left + M * s;
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(h, new IntPtr(-1), (int)Math.Round(x - Margin6 * s), (int)Math.Round(y - Margin6 * s), 0, 0, 0x0001 | 0x0010); // NOSIZE|NOACTIVATE
    }
}
