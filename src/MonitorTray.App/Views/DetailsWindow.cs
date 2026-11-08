using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using MonitorTray.App.Controls;
using MonitorTray.App.Platform;
using MonitorTray.Core.Model;
using MonitorTray.Gpu;
using MonitorTray.Sensors;

namespace MonitorTray.App.Views;

/// <summary>Details window: standalone, draggable, resizable, maximizable with a 12 margin; remembers its position.</summary>
public sealed class DetailsWindow : Window
{
    private const double M = 6;
    private readonly AppState app;
    public DetailsContent Body { get; }
    public event Action? QuitRequested;
    private bool maxed;
    private Rect restore;

    public DetailsWindow(AppState app, SensorService service, GpuFanService? fans)
    {
        this.app = app;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = true; Topmost = false; ResizeMode = ResizeMode.CanResize;
        Title = "System details"; UseLayoutRounding = true;
        MinWidth = 720 + 2 * M; MinHeight = 440 + 2 * M;
        Width = 960 + 2 * M; Height = 640 + 2 * M;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/MonitorTray;component/Assets/app.ico")); } catch (Exception) { }
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(M + 6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false });
        Body = new DetailsContent(app, service, fans);
        Content = new BlueprintFrame(Body, shadow: false) { Margin = new Thickness(M) };
        Body.TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInteractive(d)) return;
            if (e.ClickCount == 2) { ToggleMax(); return; }
            if (maxed) { RestoreFromMaxForDrag(e); }
            try { DragMove(); } catch (InvalidOperationException) { }
            SaveBounds();
        };
        Body.MaxButton.Click += ToggleMax;
        // X: with "Close hides to tray" (default) it only closes the window; the app stays in the tray. Off: quits the app.
        Body.CloseButton.Click += () => { if (app.Settings.CloseToTray) Close(); else QuitRequested?.Invoke(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { Body.FocusSearch(); e.Handled = true; }
        };
        SourceInitialized += (_, _) => { Native.SquareCorners(this); ApplyBlur(); RestoreBounds2(); };
        SizeChanged += (_, _) => FrameRegion.Apply(this, M, true);
        LocationChanged += (_, _) => { };
        Closing += (_, _) => SaveBounds();
        app.Changed += (_, _) => ApplyBlur();
    }

    private static bool IsInteractive(DependencyObject d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
        {
            if (x is FlatButton or System.Windows.Controls.TextBox) return true;
            if (x is System.Windows.Controls.Border b && b.Cursor == Cursors.IBeam) return true;
        }
        return false;
    }

    public void ApplyBlur() => Native.Blur(this, app.Settings.Opacity < 100 && Native.TransparencyEffectsEnabled());

    private Rect WorkAreaDip()
    {
        var center = new Point(Left + Width / 2, Top + Height / 2);
        var h = new WindowInteropHelper(this).Handle;
        var mon = MonitorFromWindow(h, 2);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var w = mi.rcWork;
        return new Rect(w.Left / s, w.Top / s, (w.Right - w.Left) / s, (w.Bottom - w.Top) / s);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

    public void ToggleMax()
    {
        if (!maxed)
        {
            restore = new Rect(Left, Top, Width, Height);
            var wa = WorkAreaDip();
            Left = wa.Left + 12 - M; Top = wa.Top + 12 - M; Width = wa.Width - 24 + 2 * M; Height = wa.Height - 24 + 2 * M;
            maxed = true;
        }
        else
        {
            maxed = false;
            Left = restore.Left; Top = restore.Top; Width = restore.Width; Height = restore.Height;
        }
        Body.MaxButton.ToolTip = maxed ? "Restore" : "Maximize";
        if (Body.MaxButton.Child is System.Windows.Controls.Viewbox vb && vb.Child is System.Windows.Controls.Canvas c && c.Children[0] is System.Windows.Shapes.Path p)
            p.Data = Icons.Geo(maxed ? Icons.Minimize : Icons.Maximize);
        SaveBounds();
    }

    private void RestoreFromMaxForDrag(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        var frac = pos.X / ActualWidth;
        maxed = false;
        Width = restore.Width; Height = restore.Height;
        var screen = PointToScreen(pos);
        var s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Left = screen.X / s - Width * frac; Top = screen.Y / s - pos.Y;
    }

    private void RestoreBounds2()
    {
        var st = app.Settings.DetailsWindow;
        var wa = WorkAreaDip();
        if (st is { X: double x, Y: double y } && IsOnSomeScreen(x, y, st.W ?? 960, st.H ?? 640))
        {
            Left = x - M; Top = y - M;
            if (st.W is double w) Width = w + 2 * M;
            if (st.H is double h) Height = h + 2 * M;
        }
        else
        {
            // Centered on the tray monitor (main taskbar monitor).
            var t = Taskbar.Current();
            var s = t.Scale;
            Left = t.WorkPx.Left / s + (t.WorkPx.Width / s - Width) / 2;
            Top = t.WorkPx.Top / s + (t.WorkPx.Height / s - Height) / 2;
        }
        if (st?.Maximized == true) Dispatcher.BeginInvoke(() => { if (!maxed) ToggleMax(); });
        _ = wa;
    }

    private static bool IsOnSomeScreen(double x, double y, double w, double h)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(x, y + 4, Math.Min(w, 200), 24); // the title bar must be visible
        return vs.IntersectsWith(r) && vs.Contains(new Point(x + 40, y + 12));
    }

    private void SaveBounds()
    {
        var r = maxed ? restore : new Rect(Left, Top, Width, Height);
        if (r.Width <= 0) return;
        var st = new DetailsWindowState(r.Left + M, r.Top + M, Math.Max(720, r.Width - 2 * M), Math.Max(440, r.Height - 2 * M), maxed);
        app.Update(s => s with { DetailsWindow = st });
    }

    public void Update(Snapshot s) { if (IsVisible) Body.Update(s); }
}
