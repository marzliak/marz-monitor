using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MonitorTray.App.Theming;

namespace MonitorTray.App.Controls;

/// <summary>Fábrica de elementos com recursos dinâmicos do tema.</summary>
public static class Ui
{
    public static TextBlock T(string text, double size, string brush = Theme.Text, bool head = true, int weight = 400, bool tabular = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontFamily = AppFonts.For(head, weight),
            FontWeight = FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis,
            SnapsToDevicePixels = true,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (tabular) System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
        return t;
    }

    public static Border Line(bool vertical = false)
    {
        var b = new Border { SnapsToDevicePixels = true };
        if (vertical) b.Width = 1; else b.Height = 1;
        b.SetResourceReference(Border.BackgroundProperty, Theme.Line);
        return b;
    }

    public static Border Box(Thickness border, Thickness padding, UIElement? child = null)
    {
        var b = new Border { BorderThickness = border, Padding = padding, Child = child, SnapsToDevicePixels = true };
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        return b;
    }

    public static StackPanel Row(double gap = 0, params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0 && gap > 0 && items[i] is FrameworkElement fe) fe.Margin = new Thickness(gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(items[i]);
        }
        return p;
    }

    public static Rectangle Glyph(double size) { var r = new Rectangle { Width = size, Height = size }; r.SetResourceReference(Shape.FillProperty, Theme.TitleGlyph); return r; }

    public static T With<T>(this T e, Action<T> f) { f(e); return e; }
}

/// <summary>Botão plano: hover = fundo Hover e texto/ícone em Text (docs/03 §Interação).</summary>
public sealed class FlatButton : Border
{
    private readonly List<(DependencyObject Target, DependencyProperty Prop)> fg = [];
    private bool active, hover, pressed;
    private readonly string normalFg;
    public event Action? Click;

    public FlatButton(string normalFg = Theme.Muted)
    {
        this.normalFg = normalFg;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        Refresh();
    }

    public FlatButton Fg(DependencyObject target, DependencyProperty prop) { fg.Add((target, prop)); Refresh(); return this; }

    public FlatButton Content(UIElement c)
    {
        Child = c;
        Walk(c);
        Refresh();
        return this;
    }

    private void Walk(DependencyObject d)
    {
        if (d is TextBlock t) fg.Add((t, TextBlock.ForegroundProperty));
        else if (d is Shape s && s.Stroke != null || d is Path) fg.Add((d, Shape.StrokeProperty));
        if (d is Panel p) foreach (UIElement c in p.Children) Walk(c);
        else if (d is Decorator dec && dec.Child != null) Walk(dec.Child);
        else if (d is Viewbox vb && vb.Child != null) Walk(vb.Child);
    }

    public bool Active { get => active; set { active = value; Refresh(); } }
    public string? ActiveBg { get; set; } = Theme.Hover;

    private void Refresh()
    {
        if (active && ActiveBg != null) SetResourceReference(BackgroundProperty, ActiveBg);
        else if (hover) SetResourceReference(BackgroundProperty, Theme.Hover);
        else Background = Brushes.Transparent;
        var key = hover || IsKeyboardFocused ? Theme.Text : normalFg;
        foreach (var (t, p) in fg) if (t is FrameworkElement fe) fe.SetResourceReference(p, key);
    }

    protected override void OnMouseEnter(MouseEventArgs e) { hover = true; Refresh(); }
    protected override void OnMouseLeave(MouseEventArgs e) { hover = false; pressed = false; Refresh(); }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { pressed = true; CaptureMouse(); e.Handled = true; }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var was = pressed && IsMouseOver; pressed = false; ReleaseMouseCapture(); e.Handled = true;
        if (was) Click?.Invoke();
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space) { Click?.Invoke(); e.Handled = true; }
    }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { Refresh(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { Refresh(); }

    public static FlatButton Icon(string data, double box, double icon, string tooltip)
    {
        var (root, _) = Icons.Make(data, icon, Theme.Muted);
        var b = new FlatButton { Width = box, Height = box, ToolTip = tooltip };
        root.HorizontalAlignment = HorizontalAlignment.Center; root.VerticalAlignment = VerticalAlignment.Center;
        return b.Content(root);
    }
}

/// <summary>Segmentado: borda 1 px, opções 3/9, selecionada com fundo Fill e texto Bg.</summary>
public sealed class Segmented : Border
{
    private readonly List<(object Value, Border Cell, TextBlock Text)> cells = [];
    private object? selected;
    public event Action<object>? Changed;

    public Segmented(IEnumerable<(object Value, string Label)> options)
    {
        BorderThickness = new Thickness(1);
        SetResourceReference(BorderBrushProperty, Theme.Line);
        SnapsToDevicePixels = true;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        bool first = true;
        foreach (var (v, l) in options)
        {
            var t = Ui.T(l, 12);
            var c = new Border { Padding = new Thickness(9, 3, 9, 3), Child = t, Cursor = Cursors.Hand, BorderThickness = new Thickness(first ? 0 : 1, 0, 0, 0), Background = Brushes.Transparent };
            c.SetResourceReference(BorderBrushProperty, Theme.Line);
            var value = v;
            c.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(value); Changed?.Invoke(value); };
            cells.Add((v, c, t));
            row.Children.Add(c);
            first = false;
        }
        Child = row;
    }

    public void Select(object v)
    {
        selected = v;
        foreach (var (val, c, t) in cells)
        {
            bool on = Equals(val, v);
            if (on) c.SetResourceReference(BackgroundProperty, Theme.Fill); else c.Background = Brushes.Transparent;
            t.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.Bg : Theme.Text);
        }
    }
    public object? Selected => selected;
}

/// <summary>Caixa de seleção de 15 px.</summary>
public sealed class Check15 : Border
{
    private readonly Path mark;
    private bool value;
    public event Action<bool>? Changed;
    public Check15()
    {
        Width = 15; Height = 15; BorderThickness = new Thickness(1); Cursor = Cursors.Hand; SnapsToDevicePixels = true;
        var (root, p) = Icons.Make(Icons.Check, 11, Theme.Bg);
        p.StrokeThickness = 3;
        mark = p;
        root.HorizontalAlignment = HorizontalAlignment.Center; root.VerticalAlignment = VerticalAlignment.Center;
        Child = root;
        MouseLeftButtonUp += (_, e) => { e.Handled = true; Value = !value; Changed?.Invoke(value); };
        Value = false;
    }
    public bool Value
    {
        get => value;
        set
        {
            this.value = value;
            SetResourceReference(BorderBrushProperty, value ? Theme.Fill : Theme.Muted);
            if (value) SetResourceReference(BackgroundProperty, Theme.Fill); else Background = Brushes.Transparent;
            mark.Visibility = value ? Visibility.Visible : Visibility.Hidden;
        }
    }
}

/// <summary>Slider 40–100 passo 5 desenhado no tema.</summary>
public sealed class MiniSlider : FrameworkElement
{
    public int Min { get; init; } = 40;
    public int Max { get; init; } = 100;
    public int Step { get; init; } = 5;
    private int value = 90;
    public event Action<int>? Changed;
    /// <summary>Valor final: ao soltar o mouse ou a cada tecla (para gravar só no fim do arrasto).</summary>
    public event Action<int>? Committed;

    public MiniSlider() { Width = 130; Height = 16; Cursor = Cursors.Hand; Focusable = true; FocusVisualStyle = null; }

    public int Value { get => value; set { this.value = Math.Clamp(value, Min, Max); InvalidateVisual(); } }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var f = (double)(value - Min) / (Max - Min);
        var track = (Brush)FindResource(Theme.Track); var fill = (Brush)FindResource(Theme.Fill);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        dc.DrawRectangle(track, null, new Rect(0, h / 2 - 2, w, 4));
        dc.DrawRectangle(fill, null, new Rect(0, h / 2 - 2, (w - 10) * f + 5, 4));
        dc.DrawRectangle(fill, null, new Rect((w - 10) * f, h / 2 - 5, 10, 10));
    }

    private void SetFromX(double x)
    {
        var f = Math.Clamp((x - 5) / (ActualWidth - 10), 0, 1);
        var v = Min + (int)Math.Round(f * (Max - Min) / Step) * Step;
        if (v != value) { Value = v; Changed?.Invoke(v); }
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); SetFromX(e.GetPosition(this).X); Focus(); e.Handled = true; }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) SetFromX(e.GetPosition(this).X); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (!IsMouseCaptured) return; ReleaseMouseCapture(); Committed?.Invoke(value); e.Handled = true; }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int d = e.Key is Key.Left or Key.Down ? -Step : e.Key is Key.Right or Key.Up ? Step : 0;
        if (d != 0) { Value = value + d; Changed?.Invoke(value); Committed?.Invoke(value); e.Handled = true; }
    }
}
