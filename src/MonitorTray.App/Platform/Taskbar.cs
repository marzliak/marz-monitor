using System.Windows;

namespace MonitorTray.App.Platform;

public enum Edge { Left, Top, Right, Bottom }

/// <summary>Popup position above the clock, according to the taskbar edge (docs/07 §Positioning).</summary>
public static class Taskbar
{
    public readonly record struct Placement(Rect WorkPx, Edge Edge, double Scale);

    public static Placement Current(Native.POINT? anchor = null)
    {
        var abd = new Native.APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.APPBARDATA>() };
        var edge = Edge.Bottom;
        Native.POINT pt;
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            edge = abd.uEdge switch { Native.ABE_LEFT => Edge.Left, Native.ABE_TOP => Edge.Top, Native.ABE_RIGHT => Edge.Right, _ => Edge.Bottom };
            pt = anchor ?? new Native.POINT { X = (abd.rc.Left + abd.rc.Right) / 2, Y = (abd.rc.Top + abd.rc.Bottom) / 2 };
        }
        else if (anchor is { } a) pt = a;
        else Native.GetCursorPos(out pt);
        if (anchor is { } an) pt = an; // the icon's monitor (click) takes priority
        var mon = Native.MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        double scale = 1;
        try { if (Native.GetDpiForMonitor(mon, 0, out var dx, out _) == 0) scale = dx / 96.0; } catch (Exception) { }
        var w = mi.rcWork;
        return new Placement(new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top), edge, scale);
    }

    /// <summary>Popup corner in monitor DIP (W/H in DIP).</summary>
    public static Point PopupOrigin(Placement p, double w, double h)
    {
        const double M = 12;
        var work = new Rect(p.WorkPx.X / p.Scale, p.WorkPx.Y / p.Scale, p.WorkPx.Width / p.Scale, p.WorkPx.Height / p.Scale);
        double x = work.Right - M - w, y = work.Bottom - M - h;
        if (p.Edge == Edge.Top) y = work.Top + M;
        if (p.Edge == Edge.Left) x = work.Left + M;
        return new Point(x, y);
    }
}
