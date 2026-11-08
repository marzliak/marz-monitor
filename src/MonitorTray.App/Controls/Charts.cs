using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MonitorTray.App.Theming;
using MonitorTray.Core.Format;

namespace MonitorTray.App.Controls;

internal static class Res
{
    public static Brush B(FrameworkElement e, string key) => e.TryFindResource(key) as Brush ?? Brushes.Gray;
    public static Pen P(FrameworkElement e, string key, double w) => new(B(e, key), w) { LineJoin = PenLineJoin.Round };
}

/// <summary>
/// Gráficos que "rolam": a geometria é refeita só quando chega amostra nova; o deslocamento de um passo
/// para a esquerda é uma animação de TranslateTransform (composição do WPF, sem OnRender por quadro).
/// A duração se adapta ao intervalo real entre amostras, então o movimento é contínuo mesmo quando a coleta
/// do LHM atrasa. A curva é monotônica (sem picos inventados) e null continua quebrando a linha.
/// </summary>
public abstract class ScrollChart : FrameworkElement
{
    public static bool AnimationsEnabled { get; set; } = true;
    protected readonly TranslateTransform Shift = new();
    private DateTime lastShift = DateTime.MinValue;
    private double interval = 1000;

    /// <summary>Quantos pontos cabem na largura (o ponto extra mais antigo fica fora, à esquerda).</summary>
    public int Slots { get; set; }

    protected double StepFor(int count) { var n = Slots > 1 ? Slots : count; return n > 1 ? ActualWidth / (n - 1) : 0; }

    protected void Scroll(bool shifted, double step)
    {
        var now = DateTime.UtcNow;
        if (shifted && AnimationsEnabled && IsVisible && step > 0)
        {
            var dt = (now - lastShift).TotalMilliseconds;
            if (dt is > 120 and < 5000) interval = interval * 0.5 + dt * 0.5;
            var a = new DoubleAnimation(step, 0, TimeSpan.FromMilliseconds(Math.Clamp(interval, 150, 3000)));
            Timeline.SetDesiredFrameRate(a, 50);
            Shift.BeginAnimation(TranslateTransform.XProperty, a);
        }
        else { Shift.BeginAnimation(TranslateTransform.XProperty, null); Shift.X = 0; }
        if (shifted) lastShift = now;
    }

    protected static bool IsShift<T>(T[] old, T[] now)
    {
        if (old.Length == 0 || now.Length == 0) return false;
        if (now.Length == old.Length) return now.AsSpan(0, now.Length - 1).SequenceEqual(old.AsSpan(1));
        if (now.Length == old.Length + 1) return now.AsSpan(0, old.Length).SequenceEqual(old);
        return false;
    }

    protected static void AddSmooth(StreamGeometryContext c, IReadOnlyList<Pt> run)
    {
        foreach (var (c1, c2, p) in ChartGeometry.Smooth(run))
            c.BezierTo(new Point(c1.X, c1.Y), new Point(c2.X, c2.Y), new Point(p.X, p.Y), true, true);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); Rebuild(); InvalidateVisual(); }
    protected abstract void Rebuild();
}

/// <summary>Gráfico espelhado do popup: download para cima, upload para baixo (docs/04 §B5, docs/06 §2).</summary>
public sealed class MirrorChart : ScrollChart
{
    private double[] down = [], up = [];
    private double sDown = 1, sUp = 1;
    private Geometry? gDown, gUp;
    private const double C = 32, H = 30;

    public MirrorChart() { Height = 64; SnapsToDevicePixels = true; }

    /// <param name="slots">Pontos visíveis (histórico). Passe slots+1 valores para a borda esquerda não abrir buraco ao rolar.</param>
    public void Set(IReadOnlyList<double> down, IReadOnlyList<double> up, int slots, double scaleDown, double scaleUp)
    {
        var d = down.ToArray(); var u = up.ToArray();
        bool same = d.AsSpan().SequenceEqual(this.down) && u.AsSpan().SequenceEqual(this.up) && scaleDown == sDown && scaleUp == sUp && slots == Slots;
        if (same) return;
        bool shifted = IsShift(this.down, d);
        this.down = d; this.up = u; Slots = slots; sDown = scaleDown; sUp = scaleUp;
        Rebuild();
        Scroll(shifted, StepFor(d.Length));
        InvalidateVisual();
    }

    private Geometry? Half(double[] v, double scale, int dir)
    {
        var w = ActualWidth; if (w <= 0 || v.Length < 2) return null;
        var step = StepFor(v.Length);
        var x0 = w - (v.Length - 1) * step;
        var pts = new List<Pt>(v.Length);
        for (int i = 0; i < v.Length; i++) pts.Add(new(x0 + i * step, C - dir * Math.Clamp(scale > 0 ? v[i] / scale : 0, 0, 1) * H));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(pts[0].X, C), true, true);
            c.LineTo(new Point(pts[0].X, pts[0].Y), true, true);
            AddSmooth(c, pts);
            c.LineTo(new Point(pts[^1].X, C), true, true);
        }
        g.Freeze();
        return g;
    }

    protected override void Rebuild() { gDown = Half(down, sDown, 1); gUp = Half(up, sUp, -1); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; if (w <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, 64));
        dc.DrawLine(Res.P(this, Theme.Line, 1), new Point(0, C + 0.5), new Point(w, C + 0.5));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, 64)));
        dc.PushTransform(Shift);
        if (gDown != null) dc.DrawGeometry(Res.B(this, Theme.DFill), Res.P(this, Theme.DStroke, 1.25), gDown);
        if (gUp != null) dc.DrawGeometry(Res.B(this, Theme.UFill), Res.P(this, Theme.UStroke, 1.25), gUp);
        dc.Pop(); dc.Pop();
    }
}

/// <summary>Área/sparkline que rola; null quebra a linha (docs/06 §3). A escala acompanha os dados suavemente.</summary>
public sealed class AreaChart : ScrollChart
{
    private float?[] values = [];
    private (double Lo, double Hi)? range;
    private Geometry? area, line;
    public string StrokeKey { get; set; } = Theme.DStroke;
    public string FillKey { get; set; } = Theme.DFill;
    public double Thickness { get; set; } = 1.25;
    public bool BottomLine { get; set; }

    public void Set(float?[] values, (double, double)? fixedRange, bool zeroBase)
    {
        if (values.AsSpan().SequenceEqual(this.values)) return;
        bool shifted = IsShift(this.values, values);
        this.values = values;
        var target = ChartGeometry.Range(values, fixedRange, zeroBase);
        if (target is not { } t) range = null;
        else if (fixedRange != null || range is not { } r || !shifted) range = t;
        else
        {
            // Expande na hora para nunca cortar dado; encolhe aos poucos para a curva não "pular".
            var lo = t.Lo < r.Lo ? t.Lo : r.Lo + (t.Lo - r.Lo) * 0.3;
            var hi = t.Hi > r.Hi ? t.Hi : r.Hi + (t.Hi - r.Hi) * 0.3;
            range = (lo, hi);
        }
        Rebuild();
        Scroll(shifted, StepFor(values.Length));
        InvalidateVisual();
    }

    protected override void Rebuild()
    {
        area = line = null;
        var w = ActualWidth; var h = ActualHeight;
        if (w <= 0 || h <= 0 || values.Length < 2 || range is not { } r) return;
        var step = StepFor(values.Length);
        var runs = ChartGeometry.Runs(values, step, w - (values.Length - 1) * step, h, r.Lo, r.Hi);
        if (runs.Count == 0) return;
        var a = new StreamGeometry(); var l = new StreamGeometry();
        using (var ac = a.Open())
        using (var lc = l.Open())
        {
            foreach (var run in runs)
            {
                ac.BeginFigure(new Point(run[0].X, h), true, true);
                ac.LineTo(new Point(run[0].X, run[0].Y), true, true);
                AddSmooth(ac, run);
                ac.LineTo(new Point(run[^1].X, h), true, true);
                lc.BeginFigure(new Point(run[0].X, run[0].Y), false, false);
                AddSmooth(lc, run);
            }
        }
        a.Freeze(); l.Freeze();
        area = a; line = l;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight; if (w <= 0 || h <= 0) return;
        if (BottomLine) dc.DrawLine(Res.P(this, Theme.Line, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
        if (area == null) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.PushTransform(Shift);
        dc.DrawGeometry(Res.B(this, FillKey), null, area);
        dc.DrawGeometry(null, Res.P(this, StrokeKey, Thickness), line);
        dc.Pop(); dc.Pop();
    }
}

/// <summary>Base com Fraction animada (400 ms ease-out, docs/03 §Interação).</summary>
public abstract class FractionElement : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(FractionElement),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public bool Animate { get; set; } = true;
    private double target = -1;
    public string ColorKey { get; set; } = Theme.Fill;
    /// <summary>Atraso próprio do elemento (ms): cada caixinha anda no seu tempo, não a tela inteira de uma vez.</summary>
    public int StaggerMs { get; set; }

    public void SetFraction(double f, bool emphasis)
    {
        // Sem leitura neste tick (NaN): mantém a barra onde estava, em vez de despencar para 0 e voltar.
        if (!double.IsFinite(f)) return;
        f = Math.Clamp(f, 0, 1);
        var key = emphasis ? Theme.UStroke : Theme.Fill;
        if (key != ColorKey) { ColorKey = key; InvalidateVisual(); }
        if (Math.Abs(f - target) < 1e-4) return;
        bool first = target < 0;
        target = f;
        // Primeira atribuição (elemento recém-criado/reconstruído): já nasce no valor, sem subir do zero.
        if (first) { BeginAnimation(FractionProperty, null); Fraction = f; return; }
        if (!Animate || !IsVisible) { BeginAnimation(FractionProperty, null); Fraction = f; return; }
        var anim = new DoubleAnimation(f, TimeSpan.FromMilliseconds(420)) { EasingFunction = Ease, BeginTime = TimeSpan.FromMilliseconds(StaggerMs) };
        Timeline.SetDesiredFrameRate(anim, 50);
        BeginAnimation(FractionProperty, anim);
    }
}

/// <summary>Barra horizontal: trilho Track + preenchimento.</summary>
public sealed class Bar : FractionElement
{
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Res.B(this, Theme.Track), null, new Rect(0, 0, w, h));
        if (Fraction > 0) dc.DrawRectangle(Res.B(this, ColorKey), null, new Rect(0, 0, w * Fraction, h));
    }
}

/// <summary>Célula da grade: preenchimento de baixo para cima e valor curto no topo.</summary>
public sealed class Cell : FractionElement
{
    private string label = "";
    private string unit = "";
    private readonly NumberTween tween;
    public Cell() { Height = 48; tween = new NumberTween(v => { var t = Formatter.Short(v, unit); if (t != label) { label = t; InvalidateVisual(); } }); }
    public void SetValue(double? v, string unit, string fallbackText)
    {
        this.unit = unit;
        if (v is null) { tween.Snap(null); if (label != fallbackText) { label = fallbackText; InvalidateVisual(); } return; }
        tween.StaggerMs = StaggerMs;
        tween.To(v.Value, IsVisible && Animate);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Res.B(this, Theme.Track), null, new Rect(0, 0, w, h));
        if (Fraction > 0) dc.DrawRectangle(Res.B(this, ColorKey), null, new Rect(0, h * (1 - Fraction), w, h * Fraction));
        var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(AppFonts.HeadSemiBold, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, Res.B(this, Theme.Text), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point((w - ft.Width) / 2, 3));
    }
}

/// <summary>Gauge circular 38×38, r=15, traço 5, começando às 12h.</summary>
public sealed class Gauge : FractionElement
{
    public Gauge() { Width = 38; Height = 38; }
    protected override void OnRender(DrawingContext dc)
    {
        var s = ActualWidth / 40.0;
        var c = new Point(20 * s, 20 * s); var r = 15 * s;
        dc.DrawEllipse(null, Res.P(this, Theme.Track, 5 * s), c, r, r);
        var f = Fraction;
        if (f <= 0) return;
        var pen = Res.P(this, ColorKey, 5 * s);
        if (f >= 0.9999) { dc.DrawEllipse(null, pen, c, r, r); return; }
        var a = -Math.PI / 2 + f * 2 * Math.PI;
        var end = new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(end, new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Grade responsiva: colunas = floor((W+gap)/(min+gap)), itens esticados (CSS auto-fill/minmax).</summary>
public sealed class FillGrid : System.Windows.Controls.Panel
{
    public double MinItemWidth { get; set; } = 200;
    public double Gap { get; set; } = 8;
    public double RowGap { get; set; } = double.NaN;

    private int Cols(double w) => Math.Max(1, (int)Math.Floor((w + Gap) / (MinItemWidth + Gap)));

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? MinItemWidth * 3 + Gap * 2 : available.Width;
        int cols = Cols(w); var iw = Math.Max(0, (w - Gap * (cols - 1)) / cols);
        double rg = double.IsNaN(RowGap) ? Gap : RowGap;
        double total = 0, rowH = 0; int i = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(iw, double.PositiveInfinity));
            rowH = Math.Max(rowH, c.DesiredSize.Height);
            if (++i % cols == 0) { total += rowH + rg; rowH = 0; }
        }
        if (i % cols != 0) total += rowH; else if (i > 0) total -= rg;
        return new Size(w, Math.Max(0, total));
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Cols(final.Width); var iw = Math.Max(0, (final.Width - Gap * (cols - 1)) / cols);
        double rg = double.IsNaN(RowGap) ? Gap : RowGap;
        var kids = InternalChildren.Cast<UIElement>().ToList();
        double y = 0;
        for (int r = 0; r < kids.Count; r += cols)
        {
            var row = kids.Skip(r).Take(cols).ToList();
            var h = row.Max(k => k.DesiredSize.Height);
            for (int j = 0; j < row.Count; j++) row[j].Arrange(new Rect(j * (iw + Gap), y, iw, h));
            y += h + rg;
        }
        return final;
    }
}

/// <summary>
/// Interpola um número até o novo valor (ease-out, ~420 ms, com atraso próprio). Cada TextBlock/célula tem a sua
/// instância, então os valores mudam um a um em vez de a tela inteira trocar no mesmo quadro.
/// Só o texto exibido é interpolado; o valor real (último lido) é sempre o destino.
/// </summary>
public sealed class NumberTween
{
    private readonly Action<double?> apply;
    private double from, to, current;
    private bool has;
    private DateTime start;
    private bool running;
    public int StaggerMs { get; set; }
    public int DurationMs { get; set; } = 420;
    public NumberTween(Action<double?> apply) { this.apply = apply; }

    public void Snap(double? v)
    {
        Stop();
        has = v.HasValue; if (v is double d) current = to = from = d;
        apply(v);
    }

    /// <summary>Números trocam direto (pedido do usuário: sem contagem rápida). Barras e gráficos seguem animados.</summary>
    public static bool AnimateNumbers { get; set; }

    public void To(double v, bool animate)
    {
        if (!AnimateNumbers || !has || !animate || !ScrollChart.AnimationsEnabled || !double.IsFinite(current)) { Snap(v); return; }
        if (v == to) return;
        from = current; to = v;
        start = DateTime.UtcNow.AddMilliseconds(StaggerMs);
        if (!running) { running = true; CompositionTarget.Rendering += Tick; }
    }

    private void Stop() { if (running) { running = false; CompositionTarget.Rendering -= Tick; } }

    private void Tick(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - start).TotalMilliseconds / DurationMs;
        if (t < 0) return;
        if (t >= 1) { current = to; Stop(); apply(current); return; }
        var k = 1 - Math.Pow(1 - t, 3);
        current = from + (to - from) * k;
        apply(current);
    }
}

/// <summary>Valor + unidade de um sensor com transição numérica própria.</summary>
public sealed class AnimatedValue
{
    private readonly System.Windows.Controls.TextBlock value, unit;
    private string u = "";
    private readonly NumberTween tween;
    public AnimatedValue(System.Windows.Controls.TextBlock value, System.Windows.Controls.TextBlock unit)
    {
        this.value = value; this.unit = unit;
        tween = new NumberTween(v => { var f = Formatter.Format(v, u); if (value.Text != f.Value) value.Text = f.Value; if (unit.Text != f.Unit) unit.Text = f.Unit; });
    }
    public int StaggerMs { get => tween.StaggerMs; set => tween.StaggerMs = value; }
    public void Set(double? v, string unit)
    {
        bool unitChanged = unit != u; u = unit;
        // Acumulados/contadores e mudanças de unidade não animam (seria um número que não existiu).
        if (v is null || unitChanged || unit is "1" or "GiB" or "MiB" or "ns") { tween.Snap(v); return; }
        tween.To(v.Value, value.IsVisible);
    }
}
