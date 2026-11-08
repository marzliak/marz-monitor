using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Platform;
using MonitorTray.App.Theming;
using MonitorTray.Core.Keyboard;
using MonitorTray.Core.Normalize;
using MonitorTray.Gpu;
using MonitorTray.Ryujin;
using MonitorTray.Sensors;

namespace MonitorTray.App.Views;

/// <summary>
/// "Thermal control" window, in two tabs: CURVES & FANS (<see cref="ThermalPanel"/>: all fans, the curve of each actuator,
/// modes and protections) and COOLER LCD (the RYUJIN II LCD screens, 1 to 4 info items each; each item picks a
/// sensor, the label and the group/unit).
/// </summary>
public sealed class LcdWindow : Window
{
    private const double M = 6;
    private readonly RyujinService lcd;
    private readonly KeyboardValues values;
    private readonly SensorService sensors;
    private readonly StackPanel pageList = new(), editor = new(), screen = new();
    private readonly Grid lcdBody = new();
    private readonly ThermalPanel thermal;
    private readonly FlatButton tabFans, tabLcd;
    private bool showFans = true;
    private readonly TextBlock status;
    private readonly Check15 enabled = new();
    private int selected;
    private readonly System.Windows.Threading.DispatcherTimer tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private static readonly (string Id, string Name, LcdGroup Group)[] CoolerSensors =
    [
        (RyujinSensorIds.Water, "Cooler · water", LcdGroup.Temperature),
        (RyujinSensorIds.Pump, "Cooler · pump", LcdGroup.FanSpeed),
        (RyujinSensorIds.Embedded, "Cooler · embedded fan", LcdGroup.FanSpeed),
        (RyujinSensorIds.RadiatorFan(0), "Cooler · radiator fan 1", LcdGroup.FanSpeed),
        (RyujinSensorIds.RadiatorFan(1), "Cooler · radiator fan 2", LcdGroup.FanSpeed),
        (RyujinSensorIds.RadiatorFan(2), "Cooler · radiator fan 3", LcdGroup.FanSpeed),
    ];

    private static readonly (LcdGroup G, string Label)[] Groups =
        [(LcdGroup.Temperature, "°C"), (LcdGroup.FanSpeed, "RPM"), (LcdGroup.Frequency, "GHz"), (LcdGroup.Voltage, "V"), (LcdGroup.WaterFlow, "FLOW")];

    public LcdWindow(AppState app, RyujinService lcd, KeyboardValues values, SensorService sensors, AsusFanModes? asus = null, GpuFanService? gpu = null)
    {
        this.lcd = lcd; this.values = values; this.sensors = sensors;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize; ShowInTaskbar = true; Title = "Marz Monitor · Thermal control"; UseLayoutRounding = true;
        Width = 1300 + 2 * M; Height = 860 + 2 * M; MinWidth = 1000; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(M + 6), GlassFrameThickness = new Thickness(0) });
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/MonitorTray;component/Assets/app.ico")); } catch (Exception) { }

        var root = new DockPanel();
        var close = FlatButton.Icon(Icons.X, 24, 12, "Close"); close.Click += Close;
        tabFans = Tab("CURVES & FANS", true); tabLcd = Tab("COOLER LCD", false);
        var tb = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right); tb.Children.Add(close);
        status = Ui.T("", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 12, 0); });
        DockPanel.SetDock(status, Dock.Right); tb.Children.Add(status);
        tb.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            Ui.Glyph(7).With(g => { g.Margin = new Thickness(0, 0, 8, 0); g.VerticalAlignment = VerticalAlignment.Center; }),
            Ui.T("THERMAL CONTROL", 13, weight: 600).With(t => { t.Margin = new Thickness(0, 0, 18, 0); t.VerticalAlignment = VerticalAlignment.Center; }),
            tabFans, tabLcd,
        } });
        var title = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(12, 0, 6, 0), tb); title.Background = Brushes.Transparent; title.Height = 40;
        title.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is DependencyObject d && IsButton(d)) return; try { DragMove(); } catch (InvalidOperationException) { } };
        DockPanel.SetDock(title, Dock.Top); root.Children.Add(title);

        thermal = new ThermalPanel(app, lcd, asus, gpu, sensors, values.Value, Label, (anchor, pick) => OpenSensorMenu(anchor, (id, _, _) => pick(id)));

        // LCD tab: on/off, what is on the LCD now, screens and the editor of the selected screen.
        enabled.Value = lcd.Config.Enabled;
        enabled.Changed += v => { lcd.Save(lcd.Config with { Enabled = v }); Render(); };
        var lcdFace = new Border { Background = new SolidColorBrush(Color.FromRgb(8, 8, 9)), Padding = new Thickness(12, 8, 12, 8), Width = 240, Height = 132, Child = screen };
        var top = new Grid { Margin = new Thickness(12, 10, 12, 10) };
        top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            new Border { Background = Brushes.Transparent, ToolTip = "Off: Marz does not write to the LCD (Armoury Crate keeps owning the screen)",
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { enabled, Ui.T("SHOW ON COOLER LCD", 11, Theme.Muted).With(x => { x.Margin = new Thickness(6, 0, 0, 0); x.VerticalAlignment = VerticalAlignment.Center; }) } } },
            Ui.T("With Armoury Crate controlling the cooler, both fight over the screen.", 11, Theme.Muted, head: false).With(t => t.Margin = new Thickness(0, 6, 0, 0)),
            Ui.T("Screen text and layout only: this tab never touches the pump or fans.", 11, Theme.Muted, head: false).With(t => t.Margin = new Thickness(0, 2, 0, 0)),
        } });
        var face = new StackPanel { Children = { Ui.T("ON LCD NOW", 11, Theme.Muted).With(t => t.Margin = new Thickness(0, 0, 0, 4)), lcdFace } };
        Grid.SetColumn(face, 1); top.Children.Add(face);
        var lcdTab = new DockPanel();
        var topBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), top); DockPanel.SetDock(topBox, Dock.Top); lcdTab.Children.Add(topBox);
        var body = lcdBody;
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var add = new FlatButton(Theme.Text) { Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(8), BorderThickness = new Thickness(1) }.Content(Ui.T("+ NEW SCREEN", 11));
        add.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        add.Click += () =>
        {
            var c = lcd.Config; if (c.Pages.Count >= LcdConfig.MaxPages) return;
            var ps = c.Pages.ToList(); ps.Add(new LcdPage { Slots = [LcdConfig.S("calc:cpu-temp", "CPU Temp", LcdGroup.Temperature)] });
            lcd.Save(c with { Pages = ps }); selected = ps.Count - 1; Render();
        };
        DockPanel.SetDock(add, Dock.Bottom); left.Children.Add(add);
        left.Children.Add(new ScrollViewer { Content = pageList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(Ui.Box(new Thickness(0, 0, 1, 0), new Thickness(0), left));
        var sv = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 12, 16, 12) };
        Grid.SetColumn(sv, 1); body.Children.Add(sv);
        lcdTab.Children.Add(body);

        var host = new Grid(); host.Children.Add(thermal); host.Children.Add(lcdTab);
        lcdHost = lcdTab;
        root.Children.Add(host);

        Content = new BlueprintFrame(root, shadow: false) { Margin = new Thickness(M) };
        SourceInitialized += (_, _) => { Native.SquareCorners(this); Native.Blur(this, app.Settings.Opacity < 100 && Native.TransparencyEffectsEnabled()); };
        SizeChanged += (_, _) => FrameRegion.Apply(this, M, true);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        tick.Tick += (_, _) => { RefreshLive(); if (showFans) thermal.Refresh(); };
        Loaded += (_, _) => { tick.Start(); Render(); ApplyTab(); };
        Closed += (_, _) => tick.Stop();
        Render(); ApplyTab();
    }

    private readonly DockPanel lcdHost;

    /// <summary>Curves tab (preview and whoever opens the window): the selected actuator.</summary>
    public ThermalPanel Thermal => thermal;

    /// <summary>Switches tab (preview).</summary>
    public void ShowTab(bool fans) { showFans = fans; ApplyTab(); }
    /// <summary>Opens the curves tab already on the actuator (quick card → "Edit curve…").</summary>
    public void ShowActuator(string id) { showFans = true; ApplyTab(); thermal.SelectActuator(id); thermal.Refresh(force: true); }

    private FlatButton Tab(string text, bool fans)
    {
        var b = new FlatButton(Theme.Text) { Padding = new Thickness(14, 0, 14, 0), Height = 39, ActiveBg = Theme.Sel }.Content(Ui.T(text, 12, weight: 600).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        b.Click += () => { showFans = fans; ApplyTab(); if (fans) thermal.Refresh(); };
        return b;
    }

    private void ApplyTab()
    {
        tabFans.Active = showFans; tabLcd.Active = !showFans;
        thermal.Visibility = showFans ? Visibility.Visible : Visibility.Collapsed;
        lcdHost.Visibility = showFans ? Visibility.Collapsed : Visibility.Visible;
        status.Visibility = showFans ? Visibility.Collapsed : Visibility.Visible; // "screen n/m on LCD" only on the LCD tab
    }

    private static bool IsButton(DependencyObject d) { for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x is FlatButton) return true; return false; }

    private void RefreshLive()
    {
        var s = lcd.Status;
        var r = lcd.Latest;
        status.Text = !lcd.DeviceFound ? "cooler not found" : s.Error != null ? "error: " + s.Error
            : !s.Enabled ? $"cooler ok{(r?.Firmware is { } fw ? " · firmware " + fw : "")} · LCD off in Marz"
            : $"screen {s.Page + 1}/{s.PageCount} on LCD";
        screen.Children.Clear();
        var lines = s.Enabled && s.Lines.Count > 0 ? s.Lines : [s.Enabled ? "…" : "(Armoury Crate shows its own screen)"];
        foreach (var l in lines)
            screen.Children.Add(new TextBlock { Text = l, FontFamily = new FontFamily("Consolas"), FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(235, 235, 235)), Margin = new Thickness(0, 2, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = s.Enabled ? TextWrapping.NoWrap : TextWrapping.Wrap });
        foreach (var (row, i) in pageList.Children.OfType<FlatButton>().Select((x, i) => (x, i))) row.Active = i == selected;
        foreach (var (tbx, slot) in previews) tbx.Text = LcdComposer.Value(slot.Group, lcd.Value(slot.SensorId));
    }

    private readonly List<(TextBlock, LcdSlot)> previews = [];

    private void Render()
    {
        var cfg = lcd.Config;
        enabled.Value = cfg.Enabled;
        selected = Math.Clamp(selected, 0, cfg.Pages.Count - 1);
        pageList.Children.Clear();
        for (int i = 0; i < cfg.Pages.Count; i++)
        {
            var p = cfg.Pages[i];
            var col = new StackPanel { Children =
            {
                Ui.T($"SCREEN {i + 1} · {p.Slots.Count} INFO · {p.DurationMs / 1000.0:0.#}s", 10, Theme.Muted),
                Ui.T(string.Join(" / ", p.Slots.Select(s => s.Name)), 12, head: false),
            } };
            var idx = i;
            var b = new FlatButton(Theme.Text) { Padding = new Thickness(12, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), ActiveBg = Theme.Sel }.Content(col);
            b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            b.Click += () => { selected = idx; Render(); };
            pageList.Children.Add(b);
        }
        RenderEditor();
        RefreshLive();
    }

    private void Update(Func<LcdPage, LcdPage> f)
    {
        var c = lcd.Config; var ps = c.Pages.ToList();
        ps[selected] = f(ps[selected]);
        lcd.Save(c with { Pages = ps });
    }

    private void SetSlot(int i, Func<LcdSlot, LcdSlot> f) => Update(p => { var s = p.Slots.ToList(); s[i] = f(s[i]); return p with { Slots = s }; });

    private void RenderEditor()
    {
        editor.Children.Clear(); previews.Clear();
        var cfg = lcd.Config; var page = cfg.Pages[selected];
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        FlatButton Act(string text, string tip, Action a, bool on = true)
        {
            var b = new FlatButton { Padding = new Thickness(6, 2, 6, 2), ToolTip = tip, IsEnabled = on, Opacity = on ? 1 : 0.4 }.Content(Ui.T(text, 11, Theme.Muted));
            b.Click += a; return b;
        }
        actions.Children.Add(Act("↑", "Move up", () => Move(-1), selected > 0));
        actions.Children.Add(Act("↓", "Move down", () => Move(1), selected < cfg.Pages.Count - 1));
        actions.Children.Add(Act("DUPLICATE", "Duplicate screen", () =>
        {
            if (cfg.Pages.Count >= LcdConfig.MaxPages) return;
            var ps = cfg.Pages.ToList(); ps.Insert(selected + 1, ps[selected]); lcd.Save(cfg with { Pages = ps }); selected++; Render();
        }));
        actions.Children.Add(Act("REMOVE", "Remove screen", () =>
        {
            if (cfg.Pages.Count <= 1) return;
            var ps = cfg.Pages.ToList(); ps.RemoveAt(selected); lcd.Save(cfg with { Pages = ps }); selected = Math.Max(0, selected - 1); Render();
        }, cfg.Pages.Count > 1));
        DockPanel.SetDock(actions, Dock.Right); head.Children.Add(actions);
        head.Children.Add(Ui.T($"SCREEN {selected + 1}", 16, weight: 600));
        editor.Children.Add(head);

        var timing = new WrapPanel { Margin = new Thickness(0, 10, 0, 4) };
        var durs = new[] { 5000, 10000, 20000, 30000, 60000 };
        var dur = new Segmented(durs.Select(d => ((object)d, $"{d / 1000}s")));
        dur.Select(durs.OrderBy(x => Math.Abs(x - page.DurationMs)).First());
        dur.Changed += v => { Update(p => p with { DurationMs = (int)v }); Render(); };
        timing.Children.Add(Field("TIME ON SCREEN", dur));
        var count = new Segmented(Enumerable.Range(1, 4).Select(n => ((object)n, n.ToString(CultureInfo.InvariantCulture))));
        count.Select(page.Slots.Count);
        count.Changed += v => { Update(p => p with { Slots = Resize(p.Slots, (int)v) }); Render(); };
        timing.Children.Add(Field("INFO ITEMS", count));
        editor.Children.Add(timing);

        for (int i = 0; i < page.Slots.Count; i++)
        {
            var slot = page.Slots[i]; int idx = i;
            var box = new StackPanel();
            box.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0, 0, 0, 4), Ui.T($"INFO {i + 1}" + (i == 0 ? " · sets the screen icon" : ""), 13, weight: 600)).With(b => b.Margin = new Thickness(0, 14, 0, 6)));
            var pick = new FlatButton(Theme.Text) { Height = 26, Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Choose the sensor" }
                .Content(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.T(Label(slot.SensorId), 12, head: false), Ui.T("  ▾", 11, Theme.Muted) } });
            pick.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            pick.Click += () => OpenSensorMenu(pick, (id, name, g) => { SetSlot(idx, s => s with { SensorId = id, Name = LcdText.Ascii(name, RyujinProtocol.LcdNameMax), Group = g ?? s.Group }); Render(); });
            box.Children.Add(pick);
            var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var name = Box(slot.Name, 130); name.MaxLength = RyujinProtocol.LcdNameMax;
            name.TextChanged += (_, _) => SetSlot(idx, s => s with { Name = LcdText.Ascii(name.Text, RyujinProtocol.LcdNameMax) });
            var grp = new Segmented(Groups.Select(g => ((object)g.G, g.Label))); grp.Select(slot.Group);
            grp.Changed += v => { SetSlot(idx, s => s with { Group = (LcdGroup)v }); Render(); };
            var val = Ui.T(LcdComposer.Value(slot.Group, lcd.Value(slot.SensorId)), 12, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
            previews.Add((val, slot));
            row.Children.Add(Field("LABEL (11, no accents)", name)); row.Children.Add(Field("UNIT", grp)); row.Children.Add(Field("VALUE NOW", val));
            box.Children.Add(row);
            editor.Children.Add(box);
        }
        editor.Children.Add(Ui.T("The LCD shows the label and value of each info item in the dark theme. The unit of item 1 also picks the screen icon.", 11, Theme.Muted, head: false)
            .With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 16, 0, 0); }));
    }

    private static List<LcdSlot> Resize(IReadOnlyList<LcdSlot> s, int n)
    {
        var l = s.Take(n).ToList();
        while (l.Count < n) l.Add(LcdConfig.S("calc:cpu-load", "CPU Load", LcdGroup.Temperature));
        return l;
    }

    private void Move(int d)
    {
        var c = lcd.Config; var ps = c.Pages.ToList(); int j = selected + d;
        if (j < 0 || j >= ps.Count) return;
        (ps[selected], ps[j]) = (ps[j], ps[selected]);
        lcd.Save(c with { Pages = ps }); selected = j; Render();
    }

    private string Label(string id)
    {
        foreach (var c in CoolerSensors) if (c.Id == id) return c.Name;
        return values.Label(id);
    }

    private static LcdGroup? GroupOf(string kind) => kind switch
    {
        "Temperature" => LcdGroup.Temperature, "Fan" => LcdGroup.FanSpeed, "Clock" => LcdGroup.Frequency,
        "Voltage" => LcdGroup.Voltage, "Flow" => LcdGroup.WaterFlow, _ => null,
    };

    private static LcdGroup? GroupOfCalc(string unit) => unit switch { "°C" => LcdGroup.Temperature, "GHz" => LcdGroup.Frequency, _ => null };

    /// <summary>Cooler sensors, calculated ones and every LHM sensor (grouped by device and type).</summary>
    private void OpenSensorMenu(FrameworkElement anchor, Action<string, string, LcdGroup?> pick)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 520 };
        var cooler = new MenuItem { Header = "Cooler (RYUJIN II)" };
        foreach (var c in CoolerSensors) { var x = c; var it = new MenuItem { Header = x.Name }; it.Click += (_, _) => pick(x.Id, x.Name.Split('·').Last().Trim(), x.Group); cooler.Items.Add(it); }
        m.Items.Add(cooler);
        var calc = new MenuItem { Header = "Calculated (any PC)" };
        foreach (var c in CalculatedSensors.All) { var x = c; var it = new MenuItem { Header = x.Name }; it.Click += (_, _) => pick(x.Id, x.Name.Split('·')[0].Trim() + " " + x.Name.Split('·').Last().Trim().Split(' ')[0], GroupOfCalc(x.Unit)); calc.Items.Add(it); }
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
                    var x = r;
                    var it = new MenuItem { Header = $"{x.Name}  ({MonitorTray.Core.Format.Formatter.Format(x.Value, x.Unit)})" };
                    it.Click += (_, _) => pick(x.Id, x.Name, GroupOf(x.KindRaw)); ki.Items.Add(it);
                }
                di.Items.Add(ki);
            }
            m.Items.Add(di);
        }
        if (sensors.LastRaw.Count == 0) m.Items.Add(new MenuItem { Header = "(sensors still loading…)", IsEnabled = false });
        m.IsOpen = true;
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
}
