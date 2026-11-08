using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using MonitorTray.App.Platform;
using MonitorTray.Core.Normalize;

namespace MonitorTray.App.Tray;

/// <summary>Ícone 16×16 desenhado a cada tick (docs/04 §A). Escala para 24/32 px em DPI alto. Sem vazar HICON.</summary>
public sealed class TrayIconRenderer : IDisposable
{
    private Icon? current;
    private IntPtr currentHandle;

    public static int SizeFor(double scale) => scale >= 1.75 ? 32 : scale >= 1.25 ? 24 : 16;

    /// <summary>Tamanho do ícone da bandeja para o DPI do monitor principal.</summary>
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
    /// Cor do ícone: texto normal (branco na barra escura, quase preto na clara) ou de alerta — âmbar perto do limite
    /// (≥ limite − 5 °C) e vermelho no limite. Tons mais escuros na barra clara para manter o contraste.
    /// </summary>
    public static Color ColorFor(TempLevel level, bool lightTaskbar) => level switch
    {
        TempLevel.Over => lightTaskbar ? Color.FromArgb(0xC4, 0x1E, 0x14) : Color.FromArgb(0xFF, 0x4D, 0x40),
        TempLevel.Near => lightTaskbar ? Color.FromArgb(0xA8, 0x5F, 0x00) : Color.FromArgb(0xFF, 0xB3, 0x1F),
        _ => lightTaskbar ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.White,
    };

    private static FontFamily? digits;
    /// <summary>Fonte condensada (dígitos altos e estreitos cabem em 16 px); sem ela, Segoe UI.</summary>
    private static FontFamily Digits()
    {
        if (digits != null) return digits;
        foreach (var name in new[] { "Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Segoe UI" })
            try { return digits = new FontFamily(name); } catch (ArgumentException) { }
        return digits = FontFamily.GenericSansSerif;
    }

    /// <summary>
    /// Ícone com a temperatura em °C (sem o símbolo), centrado. <paramref name="scale"/> = altura máxima dos dígitos em fração do lado
    /// (Settings.TrayFontScale: 1 ocupa o quadrado todo); 3 dígitos (≥ 100) ficam mais estreitos para caber na largura.
    /// Os dígitos são desenhados como contorno preenchido e alinhados ao pixel, legíveis em 16, 24 e 32 px.
    /// </summary>
    public static Bitmap DrawTemp(int px, float celsius, TempLevel level, bool lightTaskbar, double scale = 1.0)
    {
        double maxH = Math.Max(6, Math.Round(px * Math.Clamp(scale, 0.3, 1.0))); // altura inteira: borda de cima e de baixo no pixel
        var text = Math.Clamp((int)Math.Round(celsius, MidpointRounding.AwayFromZero), 0, 199).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var fam = Digits();
        var style = fam.Name.Contains("Bahnschrift", StringComparison.Ordinal) ? FontStyle.Regular : FontStyle.Bold;
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        // Maior corpo que cabe: largura total ≤ px e altura dos dígitos ≤ maxH (sem margem; o Windows já espaça os ícones).
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

    /// <summary>Ícone fixo (pulso) recolorido na cor de alerta: mesmo desenho, só a cor muda.</summary>
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
    /// HICON a partir do bitmap. Libera o de duas trocas atrás: o anterior ainda está na bandeja até o chamador
    /// entregar o novo (UpdateIcon), então só morre na troca seguinte.
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
