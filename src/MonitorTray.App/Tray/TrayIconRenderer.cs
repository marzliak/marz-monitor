using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MonitorTray.App.Platform;
using MonitorTray.Core.Normalize;

namespace MonitorTray.App.Tray;

/// <summary>16×16 icon drawn on each tick (docs/04 §A). Scales to 24/32 px at high DPI. No HICON leaks.</summary>
public sealed class TrayIconRenderer : IDisposable
{
    private Icon? current;
    private IntPtr currentHandle;

    public static int SizeFor(double scale) => scale >= 1.75 ? 32 : scale >= 1.25 ? 24 : 16;

    /// <summary>Tray icon size for the DPI of the main monitor.</summary>
    public static int SystemSize()
    {
        try { using var g = Graphics.FromHwnd(IntPtr.Zero); return SizeFor(g.DpiX / 96.0); } catch (Exception) { return 16; }
    }

    public static Bitmap Draw(int px, double down, double up, System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var k = px / 16f;
        using (var outline = new Pen(Color.FromArgb(0x98, 0x98, 0x9B), Math.Max(1, (int)k)))
            g.DrawRectangle(outline, 0.5f * k - (k > 1 ? 0.5f : 0), 0.5f * k - (k > 1 ? 0.5f : 0), px - k, px - k);
        using (var center = new SolidBrush(Color.FromArgb(0x7A, 0x7A, 0x7D)))
            g.FillRectangle(center, 1 * k, 8 * k - (k >= 2 ? k / 2 : 0), px - 2 * k, Math.Max(1, (int)k));
        int dh = (int)Math.Round(Math.Clamp(down, 0, 1) * 6), uh = (int)Math.Round(Math.Clamp(up, 0, 1) * 6);
        using (var ba = new SolidBrush(Color.FromArgb(a.R, a.G, a.B)))
            if (dh > 0) g.FillRectangle(ba, 3 * k, (8 - dh) * k, 4 * k, dh * k);
        using (var bb = new SolidBrush(Color.FromArgb(b.R, b.G, b.B)))
            if (uh > 0) g.FillRectangle(bb, 9 * k, 9 * k, 4 * k, uh * k);
        return bmp;
    }

    /// <summary>
    /// Icon color: normal text (white on a dark taskbar, near-black on a light one) or alert — amber near the limit
    /// (≥ limit − 5 °C) and red at the limit. Darker shades on a light taskbar to keep the contrast.
    /// </summary>
    public static Color ColorFor(TempLevel level, bool lightTaskbar) => level switch
    {
        TempLevel.Over => lightTaskbar ? Color.FromArgb(0xC4, 0x1E, 0x14) : Color.FromArgb(0xFF, 0x4D, 0x40),
        TempLevel.Near => lightTaskbar ? Color.FromArgb(0xA8, 0x5F, 0x00) : Color.FromArgb(0xFF, 0xB3, 0x1F),
        _ => lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White,
    };

    private static FontFamily? digits;
    /// <summary>Condensed font (tall, narrow digits fit in 16 px); without it, Segoe UI.</summary>
    private static FontFamily Digits()
    {
        if (digits != null) return digits;
        foreach (var name in new[] { "Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Segoe UI" })
            try { return digits = new FontFamily(name); } catch (ArgumentException) { }
        return digits = FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// Icon with the temperature in °C (without the symbol), centered. <paramref name="scale"/> = max digit height as a fraction of the side
    /// (Settings.TrayFontScale: 1 fills the whole square); 3 digits (≥ 100) get narrower to fit the width.
    /// Digits are drawn as a filled outline and pixel-aligned, legible at 16, 24 and 32 px.
    /// </summary>
    public static Bitmap DrawTemp(int px, float celsius, TempLevel level, bool lightTaskbar, double scale = 1.0)
    {
        double maxH = Math.Max(6, Math.Round(px * Math.Clamp(scale, 0.3, 1.0))); // whole-number height: top and bottom edges on the pixel
        var text = Math.Clamp((int)Math.Round(celsius, MidpointRounding.AwayFromZero), 0, 199).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var fam = Digits();
        var style = fam.Name.Contains("Bahnschrift", StringComparison.Ordinal) ? FontStyle.Regular : FontStyle.Bold;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        // Largest size that fits: total width ≤ px and digit height ≤ maxH (no margin; Windows already spaces the icons).
        GraphicsPath? path = null;
        for (float em = px * 1.4f; em >= 4; em -= 0.25f)
        {
            path?.Dispose();
            path = new GraphicsPath();
            path.AddString(text, fam, (int)style, em, PointF.Empty, fmt);
            var r = path.GetBounds();
            if (r.Width <= px && r.Height <= maxH) break;
        }
        var b = path!.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate((float)Math.Round((px - b.Width) / 2 - b.X), (float)Math.Round((px - b.Height) / 2 - b.Y));
            path.Transform(m);
        }
        using (var brush = new SolidBrush(ColorFor(level, lightTaskbar))) g.FillPath(brush, path);
        path.Dispose();
        return bmp;
    }

    /// <summary>Static (pulse) icon recolored in the alert color: same drawing, only the color changes.</summary>
    public static Bitmap Tint(Icon icon, int px, Color color)
    {
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var sized = new Icon(icon, px, px);
            g.DrawIcon(sized, new Rectangle(0, 0, px, px));
        }
        var data = bmp.LockBits(new Rectangle(0, 0, px, px), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[data.Stride * px];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            for (int i = 0; i < buf.Length; i += 4)
                if (buf[i + 3] > 0) { buf[i] = color.B; buf[i + 1] = color.G; buf[i + 2] = color.R; }
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    public Icon Render(int px, double down, double up, System.Windows.Media.Color a, System.Windows.Media.Color b)
    {
        using var bmp = Draw(px, down, up, a, b);
        return Make(bmp);
    }

    private Icon? previous;
    private IntPtr previousHandle;

    /// <summary>
    /// HICON from the bitmap. Frees the one from two swaps ago: the previous one is still in the tray until the caller
    /// hands over the new one (UpdateIcon), so it only dies on the following swap.
    /// </summary>
    public Icon Make(Bitmap bmp)
    {
        var h = bmp.GetHicon();
        var icon = Icon.FromHandle(h);
        previous?.Dispose();
        if (previousHandle != IntPtr.Zero) Native.DestroyIcon(previousHandle);
        previous = current; previousHandle = currentHandle;
        current = icon; currentHandle = h;
        return icon;
    }

    public void Dispose()
    {
        current?.Dispose(); previous?.Dispose();
        if (currentHandle != IntPtr.Zero) Native.DestroyIcon(currentHandle);
        if (previousHandle != IntPtr.Zero) Native.DestroyIcon(previousHandle);
        currentHandle = previousHandle = IntPtr.Zero;
    }
}
