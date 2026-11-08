using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Theming;
using MonitorTray.Core.Fans;

namespace MonitorTray.App.Views;

/// <summary>Hatched protection band: from <see cref="TFrom"/> °C on (full height) and/or below <see cref="PTo"/> %.</summary>
public sealed record ChartGuard(double? TFrom, double? PTo, string Label);

/// <summary>What the chart shows: X axis = source temperature, Y = output %, edited curve, previous curve and the operating point.</summary>
public sealed record CurveChartModel
{
    public (double Min, double Max, double Step) Axis { get; init; } = (20, 100, 10);
    public string XLabel { get; init; } = "°C";
    public string YLabel { get; init; } = "OUTPUT %";
    /// <summary>Curve being edited (empty = no curve: GPU on driver automatic).</summary>
    public IReadOnlyList<CurvePoint> Points { get; init; } = [];
    /// <summary>Curve saved before the edit, dashed (null = no change).</summary>
    public IReadOnlyList<CurvePoint>? Ghost { get; init; }
    public bool Editable { get; init; }
    /// <summary>FIXED mode: straight line; dragging moves the value up/down (both points together).</summary>
    public bool FixedLine { get; init; }
    public CurveLimits Limits { get; init; } = new(20, 100, 0, 2, 8);
    /// <summary>Current source temperature (vertical "NOW" line).</summary>
    public double? Now { get; init; }
    /// <summary>% applied now when it does not come from the curve (GPU: % read from the fans).</summary>
    public double? NowPercent { get; init; }
    public IReadOnlyList<ChartGuard> Guards { get; init; } = [];
    /// <summary>Text in the middle of the chart (e.g. driver curve, not exposed).</summary>
    public string? Overlay { get; init; }
}

/// <summary>
/// Large chart of the curve editor: dragging moves the point (between its neighbours), double-click adds, Del removes,
/// arrows adjust the selected one by 1 °C / 1 %. All editing rules live in <see cref="CurveEdit"/>; here only drawing and mouse.
/// </summary>
public sealed class CurveEditorChart : FrameworkElement
{
    private const double L = 42, R = 16, Top = 22, B = 26, Hit = 9;
    private CurveChartModel m = new();
    private int drag = -1;
    private double? hoverT;
    public int Selected { get; private set; } = -1;
    /// <summary>Curve changed by mouse/keyboard (points already validated) and the selected index.</summary>
    public event Action<IReadOnlyList<CurvePoint>, int>? Edited;
    public event Action<int>? SelectionChanged;

    public CurveEditorChart() { Focusable = true; FocusVisualStyle = null; SnapsToDevicePixels = true; MinHeight = 200; ClipToBounds = true; }

    public CurveChartModel Model => m;

    public void Set(CurveChartModel model, int selected)
    {
        m = model;
        Selected = model.Editable && !model.FixedLine && model.Points.Count > 0 ? Math.Clamp(selected, 0, model.Points.Count - 1) : -1;
        if (drag >= model.Points.Count) drag = -1;
        InvalidateVisual();
    }

    public void Select(int i) { if (m.Editable && !m.FixedLine && i >= 0 && i < m.Points.Count) { Selected = i; InvalidateVisual(); } }

    private Rect Plot => new(L, Top, Math.Max(10, ActualWidth - L - R), Math.Max(10, ActualHeight - Top - B));
    private double X(double t) { var p = Plot; return p.Left + (Math.Clamp(t, m.Axis.Min, m.Axis.Max) - m.Axis.Min) / (m.Axis.Max - m.Axis.Min) * p.Width; }
    private double Y(double pct) { var p = Plot; return p.Top + (1 - Math.Clamp(pct, 0, 100) / 100) * p.Height; }
    private double IX(double x) { var p = Plot; return m.Axis.Min + (x - p.Left) / p.Width * (m.Axis.Max - m.Axis.Min); }
    private double IY(double y) { var p = Plot; return (1 - (y - p.Top) / p.Height) * 100; }

    /// <summary>Drawn points: in FIXED, a line from end to end of the axis.</summary>
    private IReadOnlyList<CurvePoint> Drawn => m.FixedLine && m.Points.Count > 0 ? [new(m.Axis.Min, m.Points[0].Percent), new(m.Axis.Max, m.Points[0].Percent)] : m.Points;

    private FormattedText Text(string s, double size, string brush, int weight = 400) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(AppFonts.For(true, weight), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size, Res.B(this, brush), VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Label(DrawingContext dc, string s, double x, double y, double size, string fg, string? bg, TextAlignment a = TextAlignment.Left, int weight = 600, string? border = null)
    {
        var t = Text(s, size, fg, weight);
        double w = t.Width + 12, h = t.Height + 2, left = a == TextAlignment.Center ? x - w / 2 : a == TextAlignment.Right ? x - w : x;
        var p = Plot;
        left = Math.Clamp(left, p.Left, Math.Max(p.Left, p.Right - w));
        if (bg != null) dc.DrawRectangle(Res.B(this, bg), border != null ? Res.P(this, border, 1) : null, new Rect(left, y, w, h));
        dc.DrawText(t, new Point(left + 6, y + 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 120 || ActualHeight < 80) return;
        var p = Plot;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // click area
        var line = Res.P(this, Theme.Line, 1);
        var faint = new Pen(Res.B(this, Theme.Track), 1);

        // Protections (hatched), behind the grid.
        var hatch = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat, StartPoint = new Point(0, 0), EndPoint = new Point(4, 4) };
        var mc = Res.B(this, Theme.Muted) is SolidColorBrush sb ? sb.Color : Colors.Gray;
        hatch.GradientStops.Add(new GradientStop(Color.FromArgb(70, mc.R, mc.G, mc.B), 0)); hatch.GradientStops.Add(new GradientStop(Color.FromArgb(70, mc.R, mc.G, mc.B), 0.22));
        hatch.GradientStops.Add(new GradientStop(Colors.Transparent, 0.22)); hatch.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        double guardY = p.Top + 14;
        foreach (var g in m.Guards)
        {
            if (g.TFrom is double tf && tf >= m.Axis.Max) continue;
            double x0 = g.TFrom is double t0 ? X(t0) : p.Left, y0 = g.PTo is double pt ? Y(pt) : p.Top;
            dc.DrawRectangle(hatch, null, new Rect(x0, y0, p.Right - x0, p.Bottom - y0));
            if (g.TFrom != null) dc.DrawLine(new Pen(Res.B(this, Theme.Muted), 1) { DashStyle = new DashStyle([2, 3], 0) }, new Point(x0, y0), new Point(x0, p.Bottom));
            var lt = Text(g.Label + " (protection)", 10, Theme.Muted, 500);
            double ly;
            if (g.TFrom != null && g.PTo != null) ly = y0 + 3;          // partial band: inside it, at the top
            else if (g.PTo != null) ly = p.Bottom - lt.Height - 3;      // bottom band (pump): at the foot
            else { ly = guardY; guardY += lt.Height + 2; }              // full height: stack at the top
            dc.DrawText(lt, new Point(Math.Min(x0 + 6, p.Right - lt.Width - 2), ly));
        }

        // Grid: % every 10 (stronger at 0/50/100, label every 20) and °C at every axis step.
        for (int pc = 0; pc <= 100; pc += 10)
        {
            var y = Math.Round(Y(pc)) + 0.5;
            dc.DrawLine(pc % 50 == 0 ? line : faint, new Point(p.Left, y), new Point(p.Right, y));
            if (pc % 20 == 0) { var t = Text(pc + "%", 10, Theme.Muted); dc.DrawText(t, new Point(p.Left - 6 - t.Width, y - t.Height / 2)); }
        }
        for (double tc = Math.Ceiling(m.Axis.Min / m.Axis.Step) * m.Axis.Step; tc <= m.Axis.Max + 0.01; tc += m.Axis.Step)
        {
            var x = Math.Round(X(tc)) + 0.5;
            dc.DrawLine(faint, new Point(x, p.Top), new Point(x, p.Bottom));
            var t = Text(tc.ToString("0", CultureInfo.InvariantCulture) + "°", 10, Theme.Muted);
            dc.DrawText(t, new Point(x - t.Width / 2, p.Bottom + 5));
        }
        dc.DrawText(Text(m.YLabel, 10.5, Theme.Text, 600), new Point(p.Left, 4));
        var xl = Text(m.XLabel, 10, Theme.Muted, 500); dc.DrawText(xl, new Point(p.Right - xl.Width, 4));

        // Previous curve (dashed) and the edited one (area + line).
        if (m.Ghost is { Count: > 0 } ghost)
            dc.DrawGeometry(null, new Pen(Res.B(this, Theme.Muted), 1.5) { DashStyle = new DashStyle([3, 3], 0) }, Path(ghost, false));
        var pts = Drawn;
        if (pts.Count > 0)
        {
            dc.DrawGeometry(Res.B(this, Theme.DFill), null, Path(pts, true));
            dc.DrawGeometry(null, Res.P(this, Theme.DStroke, 2), Path(pts, false));
        }

        // Now: vertical line, label on the axis and operating point.
        if (m.Now is double now && now >= m.Axis.Min - 30)
        {
            double x = Math.Round(X(now)) + 0.5;
            dc.DrawLine(Res.P(this, Theme.Text, 1), new Point(x, p.Top), new Point(x, p.Bottom));
            Label(dc, "NOW " + now.ToString("0.0", CultureInfo.InvariantCulture) + "°", x, p.Bottom + 3, 10.5, Theme.Bg, Theme.Text, TextAlignment.Center);
            double? op = m.NowPercent ?? (pts.Count > 0 ? CurveEdit.Interpolate(pts, now) : null);
            if (m.Ghost is { Count: > 0 } g2) dc.DrawEllipse(Res.B(this, Theme.Bg), Res.P(this, Theme.Muted, 1.25), new Point(x, Y(CurveEdit.Interpolate(g2, now))), 3.5, 3.5);
            if (op is double o)
            {
                var c = new Point(x, Y(o));
                dc.DrawEllipse(null, Res.P(this, Theme.Text, 1.25), c, 7, 7);
                dc.DrawEllipse(Res.B(this, Theme.Text), null, c, 3, 3);
                var lab = Math.Round(o).ToString("0", CultureInfo.InvariantCulture) + "%";
                var lt = Text(lab, 11.5, Theme.Text);
                bool left = x + 12 + lt.Width + 12 > p.Right;
                Label(dc, lab, left ? x - 12 : x + 12, Math.Min(c.Y + 8, p.Bottom - lt.Height - 4), 11.5, Theme.Text, Theme.Bg, left ? TextAlignment.Right : TextAlignment.Left, 600, Theme.Line);
            }
        }

        // Editable points; the selected one filled, with the label "P3 · 60 °C · 62%".
        if (m.Editable && pts.Count > 0)
        {
            var src = m.FixedLine ? [pts[^1]] : pts;
            for (int i = 0; i < src.Count; i++)
            {
                bool sel = !m.FixedLine && i == Selected || m.FixedLine;
                var c = new Point(Math.Round(X(src[i].TempC)), Math.Round(Y(src[i].Percent)));
                double s = sel ? 5.5 : 4.5;
                dc.DrawRectangle(Res.B(this, sel ? Theme.DStroke : Theme.Bg), Res.P(this, Theme.DStroke, 2), new Rect(c.X - s, c.Y - s, 2 * s, 2 * s));
                if (sel)
                {
                    var tag = m.FixedLine ? $"FIXED · {src[i].Percent:0}%" : $"P{i + 1} · {src[i].TempC:0} °C · {src[i].Percent:0}%";
                    double ty = c.Y - 32 < p.Top ? c.Y + 12 : c.Y - 32;
                    Label(dc, tag, c.X, ty, 11, Theme.Bg, Theme.DStroke, m.FixedLine ? TextAlignment.Right : TextAlignment.Center);
                }
            }
        }
        else if (pts.Count > 0 && !m.FixedLine)
            foreach (var pt in pts) dc.DrawRectangle(Res.B(this, Theme.DStroke), null, new Rect(X(pt.TempC) - 2.5, Y(pt.Percent) - 2.5, 5, 5));

        // Reading under the mouse (outside a drag).
        if (hoverT is double ht && drag < 0 && pts.Count > 0)
        {
            double x = Math.Round(X(ht)) + 0.5, pc = CurveEdit.Interpolate(pts, ht);
            dc.DrawLine(new Pen(Res.B(this, Theme.Muted), 1) { DashStyle = new DashStyle([1, 3], 0) }, new Point(x, p.Top), new Point(x, p.Bottom));
            dc.DrawEllipse(Res.B(this, Theme.DStroke), null, new Point(x, Y(pc)), 3, 3);
            Label(dc, $"{ht:0} °C → {pc:0}%", x, p.Top + 2, 10.5, Theme.Text, Theme.Bg, TextAlignment.Center, 500, Theme.Line);
        }

        if (m.Overlay is { Length: > 0 } ov)
        {
            var t = Text(ov, 13, Theme.Muted, 500);
            t.MaxTextWidth = Math.Max(60, p.Width - 80); t.TextAlignment = TextAlignment.Center;
            dc.DrawText(t, new Point(p.Left + 40, p.Top + p.Height * 0.38));
        }
    }

    private StreamGeometry Path(IReadOnlyList<CurvePoint> pts, bool area)
    {
        var all = new List<CurvePoint> { new(m.Axis.Min, CurveEdit.Interpolate(pts, m.Axis.Min)) };
        all.AddRange(pts.Where(x => x.TempC > m.Axis.Min && x.TempC < m.Axis.Max));
        all.Add(new(m.Axis.Max, CurveEdit.Interpolate(pts, m.Axis.Max)));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (area) { c.BeginFigure(new Point(X(m.Axis.Min), Y(0)), true, true); foreach (var x in all) c.LineTo(new Point(X(x.TempC), Y(x.Percent)), false, false); c.LineTo(new Point(X(m.Axis.Max), Y(0)), false, false); }
            else { c.BeginFigure(new Point(X(all[0].TempC), Y(all[0].Percent)), false, false); foreach (var x in all.Skip(1)) c.LineTo(new Point(X(x.TempC), Y(x.Percent)), true, true); }
        }
        g.Freeze();
        return g;
    }

    private int HitPoint(Point at)
    {
        if (!m.Editable) return -1;
        if (m.FixedLine) return m.Points.Count > 0 && Math.Abs(at.Y - Y(m.Points[0].Percent)) <= Hit && at.X >= Plot.Left - Hit && at.X <= Plot.Right + Hit ? 0 : -1;
        for (int i = m.Points.Count - 1; i >= 0; i--)
            if (Math.Abs(at.X - X(m.Points[i].TempC)) <= Hit && Math.Abs(at.Y - Y(m.Points[i].Percent)) <= Hit) return i;
        return -1;
    }

    private void Emit(IReadOnlyList<CurvePoint> pts, int sel)
    {
        m = m with { Points = pts };
        if (!m.FixedLine) Selected = sel;
        InvalidateVisual();
        Edited?.Invoke(pts, sel);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var at = e.GetPosition(this);
        if (!m.Editable) return;
        int hit = HitPoint(at);
        if (e.ClickCount == 2 && hit < 0 && !m.FixedLine)
        {
            if (CurveEdit.Add(m.Points, IX(at.X), m.Limits) is { } add) Emit(add.Points, add.Index);
            e.Handled = true; return;
        }
        if (hit >= 0)
        {
            drag = hit;
            if (!m.FixedLine && Selected != hit) { Selected = hit; SelectionChanged?.Invoke(hit); }
            CaptureMouse(); InvalidateVisual(); e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        if (drag >= 0 && IsMouseCaptured)
        {
            if (m.FixedLine)
            {
                int v = CurveEdit.Fixed(IY(at.Y), m.Limits);
                if (m.Points.Count > 0 && v != (int)m.Points[0].Percent) Emit(m.Points.Select(x => x with { Percent = v }).ToList(), 0);
            }
            else
            {
                var moved = CurveEdit.Move(m.Points, drag, IX(at.X), IY(at.Y), m.Limits);
                if (!moved.SequenceEqual(m.Points)) Emit(moved, drag);
            }
            return;
        }
        var p = Plot;
        double? h = at.X >= p.Left && at.X <= p.Right && at.Y >= p.Top && at.Y <= p.Bottom && !m.FixedLine ? Math.Round(IX(at.X)) : null;
        Cursor = HitPoint(at) >= 0 ? (m.FixedLine ? Cursors.SizeNS : Cursors.SizeAll) : null;
        if (h != hoverT) { hoverT = h; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (drag >= 0) { drag = -1; ReleaseMouseCapture(); InvalidateVisual(); } }
    protected override void OnMouseLeave(MouseEventArgs e) { if (hoverT != null) { hoverT = null; InvalidateVisual(); } }

    /// <summary>Del removes the selected one; arrows move 1 °C / 1 %.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!m.Editable || m.FixedLine || Selected < 0) return;
        if (e.Key is Key.Delete or Key.Back) { RemoveSelected(); e.Handled = true; return; }
        var s = m.Points[Selected];
        (double dt, double dp) = e.Key switch { Key.Left => (-1d, 0d), Key.Right => (1d, 0d), Key.Up => (0d, 1d), Key.Down => (0d, -1d), _ => (0d, 0d) };
        if (dt == 0 && dp == 0) return;
        var moved = CurveEdit.Move(m.Points, Selected, s.TempC + dt, s.Percent + dp, m.Limits);
        if (!moved.SequenceEqual(m.Points)) Emit(moved, Selected);
        e.Handled = true;
    }

    /// <summary>Removes the selected point (Del or button); never goes below the minimum number of points.</summary>
    public bool RemoveSelected()
    {
        if (!m.Editable || m.FixedLine || Selected < 0 || CurveEdit.Remove(m.Points, Selected, m.Limits) is not { } r) return false;
        Emit(r, Math.Min(Selected, r.Count - 1));
        return true;
    }
}
