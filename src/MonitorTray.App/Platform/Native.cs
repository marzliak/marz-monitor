using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MonitorTray.App.Platform;

public static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    public const uint ABM_GETTASKBARPOS = 5;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;

    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hMon, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttribData { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr h, ref WindowCompositionAttribData d);

    public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1;
    public const int DWMWA_BORDER_COLOR = 34;
    public const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    public static void ToolWindow(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        var ex = GetWindowLong(h, GWL_EXSTYLE);
        SetWindowLong(h, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);
    }

    public static void SquareCorners(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        int v = DWMWCP_DONOTROUND;
        try { DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int)); } catch (Exception) { }
    }

    /// <summary>
    /// Sem a borda de 1 px que o DWM do Windows 11 desenha em volta da janela (aparece com o acrílico ligado).
    /// Windows 10 não tem o atributo: a chamada só falha.
    /// </summary>
    public static void NoBorder(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        int v = unchecked((int)DWMWA_COLOR_NONE);
        try { DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref v, sizeof(int)); } catch (Exception) { }
    }

    /// <summary>
    /// Blur atrás da janela. Janelas WPF com AllowsTransparency são layered; o backdrop do DWM (Win11 SYSTEMBACKDROP)
    /// não se aplica a elas, então usamos ACCENT_ENABLE_ACRYLICBLURBEHIND (funciona em Win10 e Win11).
    /// A cor fica na nossa Border, por isso GradientColor quase transparente.
    /// </summary>
    public static void Blur(Window w, bool on)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        var accent = new AccentPolicy { AccentState = on ? 4 : 0, AccentFlags = 0, GradientColor = 0x01000000 };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttribData { Attribute = 19, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(h, ref data);
        }
        catch (Exception) { }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public static bool TransparencyEffectsEnabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch (Exception) { return true; }
    }
}
