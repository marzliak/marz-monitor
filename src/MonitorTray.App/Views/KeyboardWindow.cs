using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Platform;
using MonitorTray.App.Theming;
using MonitorTray.Core.Keyboard;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Oled;
using MonitorTray.Sensors;

namespace MonitorTray.App.Views;

/// <summary>
/// Keyboard screens (Apex OLED): screen list on the left, editor on the right with a live preview of the 2 lines of 20 columns.
/// Each line picks a sensor (calculated or any LHM sensor), prefix, suffix and decimals. Saved to keyboard.json immediately.
/// </summary>
public sealed class KeyboardWindow : Window
{
    private const double M = 6;
    private readonly KeyboardService kb;
    private readonly KeyboardValues values;
    private readonly SensorService sensors;
    private readonly StackPanel pageList = new(), editor = new();
    private readonly TextBlock status, preview1, preview2;
    private readonly Check15 enabled = new(), wheel = new(), mute = new();
    private int selected;
    private readonly System.Windows.Threading.DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public KeyboardWindow(AppState app, KeyboardService kb, KeyboardValues values, SensorService sensors)
    {
        this.kb = kb; this.values = values; this.sensors = sensors;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize; ShowInTaskbar = true; Title = "Marz Monitor · Keyboard"; UseLayoutRounding = true;
        Width = 760 + 2 * M; Height = 560 + 2 * M; MinWidth = 640; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(M + 6), GlassFrameThickness = new Thickness(0) });
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/MonitorTray;component/Assets/app.ico")); } catch (Exception) { }

        var root = new DockPanel();
        // Title
        var close = FlatButton.Icon(Icons.X, 24, 12, "Close"); close.Click += Close;
        var tb = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right); tb.Children.Add(close);
        tb.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.Glyph(7).With(g => { g.Margin = new Thickness(0, 0, 8, 0); g.VerticalAlignment = VerticalAlignment.Center; }),
            Ui.T("KEYBOARD SCREENS", 13, weight: 600).With(t => t.Margin = new Thickness(0, 0, 10, 0)),
            (status = Ui.T("", 11, Theme.Muted)),
        } });
        var title = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(12, 6, 6, 6), tb); title.Background = Brushes.Transparent;
        title.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject d && IsButton(d)) return; try { DragMove(); } catch (InvalidOperationException) { } };
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);

        // Options
        enabled.Value = kb.Config.Enabled; wheel.Value = kb.Config.WheelEnabled; mute.Value = kb.Config.MuteCompensation;
        enabled.Changed += v => kb.Save(kb.Config with { Enabled = v });
        wheel.Changed += v => kb.Save(kb.Config with { WheelEnabled = v });
        mute.Changed += v => kb.Save(kb.Config with { MuteCompensation = v });
        var opts = new WrapPanel { Margin = new Thickness(12, 8, 12, 8) };
        opts.Children.Add(Opt(enabled, "SHOW ON KEYBOARD", "Off: the OLED goes back to the SteelSeries GG screen"));
        opts.Children.Add(Opt(wheel, "WHEEL SWITCHES SCREEN", "1 click: next screen · 2 clicks: pin/unpin"));
        opts.Children.Add(Opt(mute, "UNDO WHEEL MUTE", "Undoes the mute that the wheel click turns on in Windows (experimental)"));
        var optBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), opts); DockPanel.SetDock(optBox, Dock.Top); root.Children.Add(optBox);

        // Preview
        preview1 = Mono(); preview2 = Mono();
        var screen = new Border { Background = new SolidColorBrush(Color.FromRgb(8, 8, 9)), Padding = new Thickness(10, 6, 10, 6), Width = 20 * 9.6 + 20, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel { Children = { preview1, preview2 } } };
        var prevBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(12, 8, 12, 10), new StackPanel { Children =
        {
            Ui.T("ON KEYBOARD NOW", 11, Theme.Muted).With(t => t.Margin = new Thickness(0, 0, 0, 4)), screen,
        } });
        DockPanel.SetDock(prevBox, Dock.Top); root.Children.Add(prevBox);

        // Body
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var add = new FlatButton(Theme.Text) { Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(8), BorderThickness = new Thickness(1) }.Content(Ui.T("+ NEW SCREEN", 11));
        add.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        add.Click += () =>
        {
            if (kb.Config.Pages.Count >= KeyboardConfig.MaxPages) return;
            var pages = kb.Config.Pages.ToList(); pages.Add(new OledPage { Lines = [KeyboardConfig.L("calc:cpu-load", "CPU ", "%", 0), new OledLine()] });
            kb.Save(kb.Config with { Pages = pages }); selected = pages.Count - 1; Render();
        };
        DockPanel.SetDock(add, Dock.Bottom); left.Children.Add(add);
        left.Children.Add(new ScrollViewer { Content = pageList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Ui.Box(new Thickness(0, 0, 1, 0), new Thickness(0), left));
        var sv = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 12, 16, 12) };
        Grid.SetColumn(sv, 1); body.Children.Add(sv);
        root.Children.Add(body);

        Content = new BlueprintFrame(root, shadow: false) { Margin = new Thickness(M) };
        SourceInitialized += (_, _) => { Native.SquareCorners(this); Native.Blur(this, app.Settings.Opacity < 100 && Native.TransparencyEffectsEnabled()); };
        SizeChanged += (_, _) => FrameRegion.Apply(this, M, true);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        tick.Tick += (_, _) => RefreshLive();
        Loaded += (_, _) => { tick.Start(); Render(); };
        Closed += (_, _) => tick.Stop();
        Render();
    }

    private static bool IsButton(DependencyObject d) { for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x is FlatButton) return true; return false; }

    private static TextBlock Mono() => new()
    {
        FontFamily = new FontFamily("Consolas"), FontSize = 16, Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 235)),
        Text = " ", TextTrimming = TextTrimming.None,
    };

    private static Border Opt(Check15 c, string label, string tip)
    {
        var t = Ui.T(label, 11, Theme.Muted).With(x => { x.Margin = new Thickness(6, 0, 18, 0); x.VerticalAlignment = VerticalAlignment.Center; });
        return new Border { ToolTip = tip, Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { c, t } } };
    }

    private void RefreshLive()
    {
        var s = kb.State;
        var d = s.Delivery;
        status.Text = s.GgStatus ?? (d.Error != null ? "error: " + d.Error : !s.Enabled ? "off" : $"screen {s.Page + 1}/{s.PageCount}{(s.Pinned ? " · pinned" : "")} · {(s.WheelReady ? "wheel ok" : "wheel unavailable")}");
        var lines = d.Lines.Length > 0 ? d.Lines : (s.Enabled ? ["…", ""] : ["(GG shows its own screen)", ""]);
        preview1.Text = Pad(lines.ElementAtOrDefault(0)); preview2.Text = Pad(lines.ElementAtOrDefault(1));
        foreach (var (row, i) in pageList.Children.OfType<FlatButton>().Select((r, i) => (r, i))) row.Active = i == selected;
        foreach (var (pv, line) in livePreviews) pv.Text = Fmt(line);
    }

    private static string Pad(string? s) => (s ?? "").PadRight(KeyboardConfig.LineWidth)[..KeyboardConfig.LineWidth];
    private readonly List<(TextBlock, OledLine)> livePreviews = [];
    private string Fmt(OledLine l) => l.SensorId.Length == 0 ? "—" : values.Value(l.SensorId) is double v ? v.ToString("F" + l.Decimals, CultureInfo.InvariantCulture) + l.Suffix : "-";

    private void Render()
    {
        var cfg = kb.Config;
        selected = Math.Clamp(selected, 0, cfg.Pages.Count - 1);
        pageList.Children.Clear();
        for (int i = 0; i < cfg.Pages.Count; i++)
        {
            var p = cfg.Pages[i];
            var name = string.Join(" / ", p.Lines.Where(l => l.SensorId.Length > 0).Select(l => (l.Prefix.Length > 0 ? l.Prefix : values.Name(l.SensorId)).Trim()));
            var col = new StackPanel { Children =
            {
                Ui.T($"SCREEN {i + 1} · {p.DurationMs / 1000.0:0.#}s", 10, Theme.Muted),
                Ui.T(name.Length == 0 ? "(empty)" : name, 12, head: false),
            } };
            var idx = i;
            var b = new FlatButton(Theme.Text) { Padding = new Thickness(12, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), ActiveBg = Theme.Sel }.Content(col);
            b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            b.Click += () => { selected = idx; kb.Select(idx); Render(); };
            pageList.Children.Add(b);
        }
        RenderEditor();
        RefreshLive();
    }

    private void Update(Func<OledPage, OledPage> f)
    {
        var pages = kb.Config.Pages.ToList();
        pages[selected] = f(pages[selected]);
        kb.Save(kb.Config with { Pages = pages });
    }

    private void RenderEditor()
    {
        editor.Children.Clear(); livePreviews.Clear();
        var cfg = kb.Config; var page = cfg.Pages[selected];
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        FlatButton Act(string text, string tip, Action a, bool enabled = true)
        {
            var b = new FlatButton { Padding = new Thickness(6, 2, 6, 2), ToolTip = tip, IsEnabled = enabled, Opacity = enabled ? 1 : 0.4 }.Content(Ui.T(text, 11, Theme.Muted));
            b.Click += a; return b;
        }
        actions.Children.Add(Act("↑", "Move up", () => Move(-1), selected > 0));
        actions.Children.Add(Act("↓", "Move down", () => Move(1), selected < cfg.Pages.Count - 1));
        actions.Children.Add(Act("DUPLICATE", "Duplicate screen", () =>
        {
            if (cfg.Pages.Count >= KeyboardConfig.MaxPages) return;
            var ps = cfg.Pages.ToList(); ps.Insert(selected + 1, ps[selected]); kb.Save(cfg with { Pages = ps }); selected++; Render();
        }));
        actions.Children.Add(Act("REMOVE", "Remove screen", () =>
        {
            if (cfg.Pages.Count <= 1) return;
            var ps = cfg.Pages.ToList(); ps.RemoveAt(selected); kb.Save(cfg with { Pages = ps }); selected = Math.Max(0, selected - 1); Render();
        }, cfg.Pages.Count > 1));
        actions.Children.Add(Act("SHOW NOW", "Show this screen on the keyboard", () => kb.Select(selected)));
        DockPanel.SetDock(actions, Dock.Right); head.Children.Add(actions);
        head.Children.Add(Ui.T($"SCREEN {selected + 1}", 16, weight: 600));
        editor.Children.Add(head);

        // Duration and icon
        var timing = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        var dur = new Segmented([(2000, "2s"), (3000, "3s"), (5000, "5s"), (8000, "8s"), (15000, "15s")]);
        dur.Select(new[] { 2000, 3000, 5000, 8000, 15000 }.OrderBy(x => Math.Abs(x - page.DurationMs)).First());
        dur.Changed += v => { Update(p => p with { DurationMs = (int)v }); Render(); };
        timing.Children.Add(Field("TIME ON SCREEN", dur));
        var icon = Box(page.IconId.ToString(CultureInfo.InvariantCulture), 48);
        icon.LostFocus += (_, _) => { if (int.TryParse(icon.Text, out var n)) Update(p => p with { IconId = Math.Clamp(n, 0, 255) }); };
        timing.Children.Add(Field("GG ICON (0–255)", icon));
        editor.Children.Add(timing);

        for (int li = 0; li < 2; li++)
        {
            var line = page.Lines.ElementAtOrDefault(li) ?? new OledLine();
            int idx = li;
            var box = new StackPanel();
            box.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0, 0, 0, 4), Ui.T($"LINE {li + 1}", 13, weight: 600)).With(b => b.Margin = new Thickness(0, 16, 0, 6)));
            var pick = new FlatButton(Theme.Text) { Height = 26, Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Choose what this line shows" }
                .Content(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.T(values.Label(line.SensorId), 12, head: false), Ui.T("  ▾", 11, Theme.Muted) } });
            pick.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            pick.Click += () => OpenSensorMenu(pick, id => { SetLine(idx, l => l with { SensorId = id, Prefix = l.SensorId.Length == 0 ? Short(id) : l.Prefix, Suffix = l.SensorId.Length == 0 ? UnitOf(id) : l.Suffix }); Render(); });
            box.Children.Add(pick);
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var pre = Box(line.Prefix, 130); pre.TextChanged += (_, _) => SetLine(idx, l => l with { Prefix = pre.Text });
            var suf = Box(line.Suffix, 60); suf.TextChanged += (_, _) => SetLine(idx, l => l with { Suffix = suf.Text });
            var dec = new Segmented([(0, "0"), (1, "1"), (2, "2"), (3, "3")]); dec.Select(line.Decimals);
            dec.Changed += v => SetLine(idx, l => l with { Decimals = (int)v });
            var live = Ui.T(Fmt(line), 12, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
            livePreviews.Add((live, line));
            row.Children.Add(Field("PREFIX", pre)); row.Children.Add(Field("SUFFIX", suf)); row.Children.Add(Field("DECIMALS", dec)); row.Children.Add(Field("VALUE NOW", live));
            box.Children.Add(row);
            if (line.SensorId.Length > 0)
            {
                var clear = new FlatButton { Padding = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }.Content(Ui.T("clear line", 11, Theme.Muted));
                clear.Click += () => { SetLine(idx, _ => new OledLine()); Render(); };
                box.Children.Add(clear);
            }
            editor.Children.Add(box);
        }
        editor.Children.Add(Ui.T("Each line has 20 columns on the keyboard; longer text is cut off. Prefixes and values of both lines are aligned.", 11, Theme.Muted, head: false)
            .With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 16, 0, 0); }));
    }

    private void SetLine(int i, Func<OledLine, OledLine> f) => Update(p =>
    {
        var lines = p.Lines.ToList(); while (lines.Count < 2) lines.Add(new());
        lines[i] = f(lines[i]); return p with { Lines = lines };
    });

    private void Move(int d)
    {
        var ps = kb.Config.Pages.ToList(); int j = selected + d;
        if (j < 0 || j >= ps.Count) return;
        (ps[selected], ps[j]) = (ps[j], ps[selected]);
        kb.Save(kb.Config with { Pages = ps }); selected = j; Render();
    }

    private static string Short(string id) => CalculatedSensors.All.FirstOrDefault(x => x.Id == id) is { Id: not null } c ? c.Name.Split('·')[0].Trim() + " " : "";
    private string UnitOf(string id)
    {
        var c = CalculatedSensors.All.FirstOrDefault(x => x.Id == id);
        if (c.Id != null) return c.Unit is "%" ? "%" : " " + c.Unit.Replace("°C", "C");
        var r = sensors.LastRaw.FirstOrDefault(x => x.Id == id);
        return r == null ? "" : r.Unit switch { "degC" => " C", "%" => "%", "MHz" => " MHz", "W" => " W", "V" => " V", "RPM" => " RPM", "GiB" => " GB", "MiB" => " MB", _ => "" };
    }

    private static TextBox Box(string text, double width)
    {
        var t = new TextBox { Text = text, Width = width, Height = 24, FontFamily = AppFonts.Body, FontSize = 12, BorderThickness = new Thickness(1), Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBox.ForegroundProperty, Theme.Text); t.SetResourceReference(TextBox.BackgroundProperty, Theme.Track);
        t.SetResourceReference(TextBox.BorderBrushProperty, Theme.Line); t.SetResourceReference(TextBox.CaretBrushProperty, Theme.Text);
        return t;
    }

    private static StackPanel Field(string label, FrameworkElement control) => new()
    {
        Margin = new Thickness(0, 0, 16, 6),
        Children = { Ui.T(label, 10, Theme.Muted).With(t => t.Margin = new Thickness(0, 0, 0, 3)), control },
    };

    /// <summary>Menu with the calculated sensors and every LHM sensor grouped by device and type.</summary>
    private void OpenSensorMenu(FrameworkElement anchor, Action<string> pick)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 520 };
        var calc = new MenuItem { Header = "Calculated (any PC)" };
        foreach (var c in CalculatedSensors.All) { var id = c.Id; var it = new MenuItem { Header = c.Name }; it.Click += (_, _) => pick(id); calc.Items.Add(it); }
        m.Items.Add(calc);
        m.Items.Add(new Separator());
        foreach (var dev in sensors.LastRaw.GroupBy(r => r.DeviceName).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var di = new MenuItem { Header = dev.Key };
            foreach (var kind in dev.GroupBy(r => r.KindRaw).OrderBy(g => Units.TypeInfo(g.Key).Order))
            {
                var ki = new MenuItem { Header = Units.TypeInfo(kind.Key).Label };
                foreach (var r in kind.OrderBy(x => x.Name, StringComparer.CurrentCulture))
                {
                    var id = r.Id;
                    var it = new MenuItem { Header = $"{r.Name}  ({MonitorTray.Core.Format.Formatter.Format(r.Value, r.Unit)})" };
                    it.Click += (_, _) => pick(id); ki.Items.Add(it);
                }
                di.Items.Add(ki);
            }
            m.Items.Add(di);
        }
        if (sensors.LastRaw.Count == 0) m.Items.Add(new MenuItem { Header = "(sensors still loading…)", IsEnabled = false });
        m.IsOpen = true;
    }
}
