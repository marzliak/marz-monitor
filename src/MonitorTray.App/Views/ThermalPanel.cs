using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MonitorTray.App.Controls;
using MonitorTray.App.Theming;
using MonitorTray.Core.Fans;
using MonitorTray.Core.Normalize;
using MonitorTray.Gpu;
using MonitorTray.Ryujin;
using MonitorTray.Sensors;
using CoolerMode = MonitorTray.Ryujin.FanMode;
using GpuMode = MonitorTray.Gpu.FanMode;

namespace MonitorTray.App.Views;

/// <summary>
/// "Curves and fans" tab of the thermal control (mock 03e): strip with every fan, per-device tabs, device mode,
/// large chart of the actuator's curve (source → curve → actuator) and a panel with source, protections and fans turned off.
/// Nothing is written without a click: dragging only changes the edit; Apply writes (cooler: ryujin-fans.json; board: User curve via
/// Armoury Crate); the mode buttons do the same as in the popup.
/// </summary>
public sealed class ThermalPanel : DockPanel
{
    private readonly AppState app;
    private readonly RyujinService? cooler;
    private readonly AsusFanModes? asus;
    private readonly GpuFanService? gpu;
    private readonly SensorService sensors;
    private readonly Func<string, double?> values;
    private readonly Func<string, string> label;
    private readonly Action<FrameworkElement, Action<string>> pickSensor;

    /// <summary>Actuator: Name = label shown (the user's name or the original), Sub = extra info ("3 fans"), Tip = "name · ORIGINAL" when renamed.</summary>
    private sealed record Act(string Id, ThermalDevice Dev, string Name, string? Sub, string? Tip = null);
    /// <summary>Pending edit of an actuator. Fixed: fixed % = Points[0].Percent.</summary>
    private sealed record Edit(IReadOnlyList<CurvePoint> Points, bool Fixed);
    private sealed record View(Act A, Edit Saved, Edit Cur, (double Min, double Max, double Step) Axis, CurveLimits Limits,
        bool Editable, string? Block, string SourceId, double? Temp, double? NowPercent, string? Overlay, bool Flat, IReadOnlyList<ChartGuard> Guards);

    private readonly Dictionary<string, Edit> edits = new();
    private readonly Dictionary<ThermalDevice, string> lastByDevice = new();
    private string? pendingSource;
    private string selected = "";
    private int selPoint = 1;
    private string staticKey = "", pointsKey = "";
    private CancellationTokenSource? tuneCts;
    private readonly Dictionary<string, (TempSmoother S, long At)> smooth = new();
    private bool protOpen;

    private readonly WrapPanel strip = new();
    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel pills = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel modes = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock modeLabel = Ui.T("", 10, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(12, 0, 8, 0); });
    private readonly TextBlock title = Ui.T("", 30, weight: 600).With(t => { t.LineHeight = 33; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; });
    private readonly TextBlock sub = Ui.T("", 12, Theme.Muted, tabular: true);
    private readonly Border control = new() { VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock hint = Ui.T("", 11.5, Theme.Muted, head: false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.VerticalAlignment = VerticalAlignment.Center; });
    private readonly TextBlock legendRight = Ui.T("", 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center);
    private readonly FrameworkElement legendGhost;
    private readonly TextBlock legendCurveText;
    private readonly Border banner = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CurveEditorChart chart = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly DockPanel pointsRow = new() { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Border compare;
    private readonly StackPanel side = new();
    private readonly StackPanel nowBox = new();
    private readonly TextBlock changes = Ui.T("No changes", 12, head: false).With(t => t.VerticalAlignment = VerticalAlignment.Center);
    private readonly TextBlock changesNote = Ui.T("", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(8, 0, 0, 0); });
    private readonly FlatButton discard, apply;
    private readonly TextBlock applyText;
    private readonly Border changesDot = new() { Width = 6, Height = 6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

    public ThermalPanel(AppState app, RyujinService? cooler, AsusFanModes? asus, GpuFanService? gpu, SensorService sensors,
        Func<string, double?> values, Func<string, string> label, Action<FrameworkElement, Action<string>> pickSensor)
    {
        this.app = app; this.cooler = cooler; this.asus = asus; this.gpu = gpu; this.sensors = sensors;
        this.values = values; this.label = label; this.pickSensor = pickSensor;
        Snap = () => sensors.Latest;
        LastChildFill = true;

        // Fan strip: always visible, a click selects the actuator.
        var stripBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 6, 10, 8), strip);
        stripBox.SetResourceReference(Border.BackgroundProperty, Theme.Track);
        SetDock(stripBox, Dock.Top); Children.Add(stripBox);

        // Action bar.
        discard = new FlatButton { Height = 26, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 0), ToolTip = "Reverts the curves to what is saved" }.Content(Ui.T("DISCARD", 11, Theme.Muted, weight: 600).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        discard.Click += () => { edits.Clear(); pendingSource = null; Refresh(force: true); };
        applyText = Ui.T("APPLY", 11, Theme.Bg, weight: 600).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        apply = new FlatButton(Theme.Bg) { Height = 26, Padding = new Thickness(14, 0, 14, 0), ActiveBg = Theme.Fill }.Content(applyText);
        apply.Click += Apply;
        changesDot.SetResourceReference(Border.BackgroundProperty, Theme.UStroke);
        var bar = new DockPanel { LastChildFill = true };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Children = { discard, apply } };
        SetDock(btns, Dock.Right); bar.Children.Add(btns);
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { changesDot, changes, changesNote } });
        var barBox = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(14, 7, 10, 7), bar);
        SetDock(barBox, Dock.Bottom); Children.Add(barBox);

        // Body: center (tabs, mode, title, chart, points) and side panel.
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 520 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var center = new DockPanel { Margin = new Thickness(18, 12, 18, 10), LastChildFill = true };

        var tabsBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), tabs);
        SetDock(tabsBox, Dock.Top); center.Children.Add(tabsBox);
        var dev = new DockPanel { LastChildFill = true };
        var modeBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { modeLabel, modes } };
        SetDock(modeBox, Dock.Right); dev.Children.Add(modeBox); dev.Children.Add(pills);
        var devBox = Ui.Box(new Thickness(1, 0, 1, 1), new Thickness(10, 7, 10, 7), dev);
        SetDock(devBox, Dock.Top); center.Children.Add(devBox);

        var head = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        SetDock(control, Dock.Right); head.Children.Add(control);
        var titleBox = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 0, 12, 0), Child = new StackPanel { Children =
        {
            Ui.T("EDITING CURVE", 10, Theme.Muted).With(t => t.Margin = new Thickness(0, 0, 0, 1)), title, sub.With(t => t.Margin = new Thickness(0, 2, 0, 0)),
        } } };
        titleBox.SetResourceReference(Border.BorderBrushProperty, Theme.DStroke);
        head.Children.Add(titleBox);
        SetDock(head, Dock.Top); center.Children.Add(head);

        var (info, _) = Icons.Make(Icons.Info, 13, Theme.Muted);
        info.VerticalAlignment = VerticalAlignment.Top; info.Margin = new Thickness(0, 1, 6, 0);
        var hintRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        SetDock(info, Dock.Left); hintRow.Children.Add(info); hintRow.Children.Add(hint);
        SetDock(hintRow, Dock.Top); center.Children.Add(hintRow);

        var legend = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        legendGhost = Swatch("dash", "previous · saved");
        var legendCurve = Swatch("line", "editing");
        legendCurveText = (TextBlock)((Panel)legendCurve).Children[1];
        foreach (var e in new[] { legendCurve, legendGhost, Swatch("now", "now"), Swatch("hatch", "protection") }) { SetDock(e, Dock.Left); legend.Children.Add(e); }
        SetDock(legendRight, Dock.Right); legend.Children.Add(legendRight);
        SetDock(legend, Dock.Top); center.Children.Add(legend);
        SetDock(banner, Dock.Top); center.Children.Add(banner);

        compare = Ui.Box(new Thickness(1), new Thickness(0), null).With(b => b.Margin = new Thickness(0, 8, 0, 0));
        SetDock(compare, Dock.Bottom); center.Children.Add(compare);
        SetDock(pointsRow, Dock.Bottom); center.Children.Add(pointsRow);
        center.Children.Add(chart);
        body.Children.Add(center);

        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        var sideBox = Ui.Box(new Thickness(1, 0, 0, 0), new Thickness(0), sideScroll);
        Grid.SetColumn(sideBox, 1); body.Children.Add(sideBox);
        Children.Add(body);

        chart.Edited += OnChartEdited;
        chart.SelectionChanged += i => { selPoint = i; RefreshEdit(); };
        // No subscription to service events (the window is recreated on every open): the window tick calls Refresh every 500 ms.
        asus?.RefreshCurves();
        Refresh(force: true);
    }

    // ------------------------------------------------------------------ model

    /// <summary>Sensor reading (rpm/% of the board fans). The preview swaps it for a built-up reading.</summary>
    public Func<MonitorTray.Core.Model.Snapshot?> Snap { get; set; }

    private bool GpuOk => gpu?.State.Reading is { Fans.Count: > 0 };
    private bool BoardOk => asus is { Available: true } && asus.Fans.Count > 0;
    private bool CoolerOk => cooler is { DeviceFound: true };
    private string CoolerSource => pendingSource ?? cooler?.FanConfig.SourceId ?? ThermalMap.CpuSource;
    private string? Nick(string acName) => FanNames.Custom(app.Settings.AcFanNames, acName);
    private string? CoolerNick(string part) => FanNames.Custom(app.Settings.CoolerNames, part);
    private Act CoolerAct(string id, string part, string label, string? sub) =>
        CoolerNick(part) is string n ? new(id, ThermalDevice.Cooler, n, sub, app.Settings.CoolerTip(part)) : new(id, ThermalDevice.Cooler, label, sub);
    private string BoardMode => asus?.Pending ?? asus?.Mode ?? "curves";

    private static string DevName(ThermalDevice d) => d switch { ThermalDevice.Gpu => "GPU", ThermalDevice.Board => "MOTHERBOARD", _ => "RYUJIN II" };

    private List<Act> Acts()
    {
        var l = new List<Act>();
        if (GpuOk) { int n = gpu!.State.Reading!.Fans.Count; l.Add(new(ThermalMap.GpuId, ThermalDevice.Gpu, n > 1 ? "Fans " + string.Join(" + ", Enumerable.Range(1, n)) : "Fan", null)); }
        if (BoardOk) foreach (var f in asus!.Fans) l.Add(new(ThermalMap.BoardId(f), ThermalDevice.Board, app.Settings.BoardFanName(f), null, Nick(f) != null ? app.Settings.BoardFanTip(f) : null));
        if (CoolerOk)
        {
            int rad = cooler!.Latest?.Fans?.Rpm.Length ?? 0;
            l.Add(CoolerAct(ThermalMap.PumpId, "pump", "Pump", null));
            l.Add(CoolerAct(ThermalMap.RadiatorId, "radiator", "Radiator", rad > 1 ? $"{rad} fans" : null));
            l.Add(CoolerAct(ThermalMap.EmbeddedId, "embedded", "Embedded fan", null));
        }
        return l;
    }

    private static (CoolerFanSetting S, RyujinCurves.Fan Fan) Setting(CoolerConfig c, string id) => id switch
    {
        ThermalMap.PumpId => (c.Pump, RyujinCurves.Fan.Pump),
        ThermalMap.RadiatorId => (c.Radiator, RyujinCurves.Fan.Radiator),
        _ => (c.Embedded, RyujinCurves.Fan.Embedded),
    };

    /// <summary>
    /// Source temperature as the curve uses it: ~5 s moving average (<see cref="TempSmoother"/>), same as the cooler loop.
    /// The instantaneous CPU Package value jumps 10–20 °C in short spikes and made the NOW line unreadable.
    /// </summary>
    private double? Temp(string id)
    {
        double? v = cooler != null ? cooler.Value(id) : values(id);
        if (v == null && id == ThermalMap.GpuSource) v = gpu?.State.Reading?.TemperatureC;
        double? raw = v is double x && x is > 1 and < 150 ? x : null;
        var now = Environment.TickCount64;
        if (!smooth.TryGetValue(id, out var e)) e = (new TempSmoother(), 0);
        var r = e.S.Next(raw, e.At == 0 ? 1 : (now - e.At) / 1000.0);
        smooth[id] = (e.S, now);
        return r;
    }

    private string SourceName(string id) => id switch
    {
        ThermalMap.CpuSource => "CPU", ThermalMap.GpuSource => "GPU core", RyujinSensorIds.Water => "Liquid · RYUJIN II", _ => label(id),
    };

    private static Edit Flat(double pct, (double Min, double Max, double Step) axis) => new([new(axis.Min, pct), new(axis.Max, pct)], true);

    private View Build(Act a)
    {
        string src = ThermalMap.SourceOf(a.Id, CoolerSource);
        double? temp = Temp(src);
        edits.TryGetValue(a.Id, out var e);
        switch (a.Dev)
        {
            case ThermalDevice.Cooler:
            {
                var (s, fan) = Setting(cooler!.FanConfig, a.Id);
                var raw = CoolerLogic.CurveOf(s, fan).Select(n => new CurvePoint(n.TempC, n.Percent)).ToList();
                bool liquid = src == RyujinSensorIds.Water;
                var axis = ThermalMap.Axis(liquid, raw.Select(p => p.TempC).Concat(e is { Fixed: false } ? e.Points.Select(p => p.TempC) : []));
                var lim = ThermalMap.Limits(a.Id, axis, CoolerSafety.PumpMinPercent);
                var curve = CurveEdit.Normalize(raw, lim);
                if (curve.Count < 2) curve = [new(axis.Min, s.FixedPercent), new(axis.Max, s.FixedPercent)];
                var saved = s.Mode == CoolerMode.Fixed ? Flat(s.FixedPercent, axis) : new Edit(curve, false);
                var guards = new List<ChartGuard>();
                if (liquid) guards.Add(new(CoolerSafety.WaterPanicC, null, $"≥ {CoolerSafety.WaterPanicC:0} °C → 100%"));
                if (a.Id == ThermalMap.PumpId) guards.Add(new(null, CoolerSafety.PumpMinPercent, $"pump never below {CoolerSafety.PumpMinPercent}%"));
                var cur = e ?? saved;
                if (cur.Fixed) cur = Flat(cur.Points[0].Percent, axis); // the axis may have changed with the source
                return new View(a, saved, cur, axis, lim, true, null, src, temp, null, null, false, guards);
            }
            case ThermalDevice.Board:
            {
                var fan = ThermalMap.BoardFan(a.Id)!;
                bool off = asus!.Off.IsOff(fan);
                var mode = BoardMode;
                List<CurvePoint> raw; bool flat = false; string? overlay = null;
                int? man = asus.Off.ManualPercent(fan);
                if (off) raw = CurveEdit.FromChip(BoardFanOff.ProtectionCurve(4));
                else if (man is int mp) raw = CurveEdit.FromChip(BoardFanOff.HoldCurve(4, CurveEdit.PwmFromPercent(mp)));
                else if (mode == "full") { raw = [new(20, 100), new(100, 100)]; flat = true; }
                else if (asus.Curves.TryGetValue(fan, out var byMode) && byMode.TryGetValue(mode, out var pts)) raw = CurveEdit.FromChip(pts);
                else { raw = []; overlay = "Curve not read from Armoury Crate yet."; }
                var axis = ThermalMap.Axis(false, raw.Where(p => p.TempC <= 120).Select(p => p.TempC));
                var lim = ThermalMap.Limits(a.Id, axis, CoolerSafety.PumpMinPercent, raw.Count >= 2 ? raw.Count : 4);
                var block = raw.Count == 0 ? "curve not read yet" : flat ? "FULL: 100% at every temperature"
                    : man is int mm ? $"MANUAL {mm}% (switch back to CURVE on the popup card to edit)"
                    : ThermalMap.BoardEditBlock(asus.Available, asus.Mode, asus.Pending, off, asus.SavedPending.Contains(fan), CurveEdit.Fits(raw, lim));
                var saved = new Edit(raw, false);
                if (block != null) edits.Remove(a.Id);
                return new View(a, saved, block == null && e != null ? e : saved, axis, lim, block == null, block, src, temp, null, overlay, flat, []);
            }
            default:
            {
                var st = gpu!.State;
                var axis = ThermalMap.Axis(false, []);
                double? now = st.Reading?.Fans.Where(f => f.SpeedPercent != null).Select(f => (double)f.SpeedPercent!.Value).DefaultIfEmpty().Average();
                bool manual = st.Mode == GpuMode.Manual && st.RequestedPercent is uint;
                var saved = manual ? Flat(st.RequestedPercent!.Value, axis) : new Edit([], false);
                var guards = new List<ChartGuard> { new(80, null, "core ≥ 80 °C → 100%"), new(50, 30, "< 30% only with core < 50 °C") };
                var overlay = manual ? null : st.Mode == GpuMode.Automatic ? "DRIVER AUTOMATIC\nThe curve belongs to the driver and is not exposed. 30 / 50 / 75 / 100 set a fixed %." : "Fan mode undefined (mixed or no reading).";
                return new View(a, saved, saved, axis, new CurveLimits(axis.Min, axis.Max, 0, 2, 2), false, "the driver controls the curve", src, temp, now, overlay, manual, guards);
            }
        }
    }

    private static bool Same(Edit a, Edit b) => a.Fixed == b.Fixed && (a.Fixed ? a.Points[0].Percent == b.Points[0].Percent : CurveEdit.Changed(a.Points, b.Points) == 0);

    private static string ChangeText(Edit saved, Edit cur) =>
        cur.Fixed != saved.Fixed ? (cur.Fixed ? $"fixed at {cur.Points[0].Percent:0}%" : "curve instead of fixed")
        : cur.Fixed ? $"fixed {saved.Points[0].Percent:0}% → {cur.Points[0].Percent:0}%"
        : CurveEdit.Changed(saved.Points, cur.Points) is int n && n == 1 ? "1 point changed" : $"{CurveEdit.Changed(saved.Points, cur.Points)} points changed";

    // ------------------------------------------------------------------ refresh

    /// <summary>Window tick (~500 ms) and service events: rebuilds what changed; buttons only when the structure changes.</summary>
    public void Refresh(bool force = false)
    {
        var acts = Acts();
        if (acts.Count == 0) { ShowEmpty(); return; }
        if (acts.All(a => a.Id != selected)) selected = (acts.FirstOrDefault(a => a.Dev == ThermalDevice.Cooler) ?? acts[0]).Id;
        var key = StaticKey(acts);
        if (force || key != staticKey) { staticKey = key; RebuildStatic(acts); }
        RefreshStrip(acts);
        RefreshEdit();
    }

    private string StaticKey(List<Act> acts)
    {
        var st = gpu?.State; var cs = cooler?.FanStatus;
        return string.Join("|", selected, string.Join(",", acts.Select(a => a.Id + a.Name + a.Sub)), asus?.Mode, asus?.Pending, asus?.Error, string.Join(",", asus?.Off.Off ?? []),
            asus == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(asus.Curves), cooler == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(cooler.FanConfig),
            cs?.Notice, cs?.Tuning, cs?.Controlling, st?.Mode, st?.RequestedPercent, st?.Busy, st?.CanControl, st?.Error, pendingSource, string.Join(",", edits.Keys), app.Settings.GetHashCode());
    }

    private void ShowEmpty()
    {
        strip.Children.Clear(); tabs.Children.Clear(); pills.Children.Clear(); modes.Children.Clear(); side.Children.Clear();
        title.Text = "No controllable fan found";
        sub.Text = "GPU (NVML), board (Armoury Crate) and water cooler show up here when available.";
        chart.Set(new CurveChartModel { Overlay = "no devices" }, -1);
        apply.IsEnabled = false; apply.Opacity = 0.45;
    }

    private void Select(string id)
    {
        if (id == selected || ThermalMap.DeviceOf(id) is not { } d) return;
        selected = id; lastByDevice[d] = id; selPoint = 1; pointsKey = "";
        Refresh(force: true);
    }

    /// <summary>Selects an actuator (ThermalMap.*Id or BoardId).</summary>
    public void SelectActuator(string id) { if (Acts().Any(a => a.Id == id)) Select(id); }

    /// <summary>Preview only: a pending edit (nothing is written; Apply is not called).</summary>
    internal void PreviewEdit(string id, IReadOnlyList<CurvePoint> pts, int sel)
    {
        SelectActuator(id);
        edits[id] = new Edit(pts, false); selPoint = sel; pointsKey = "";
        Refresh(force: true);
    }

    private void OnChartEdited(IReadOnlyList<CurvePoint> pts, int sel)
    {
        var acts = Acts(); var a = acts.FirstOrDefault(x => x.Id == selected); if (a == null) return;
        var v = Build(a);
        if (!v.Editable) return;
        var next = new Edit(pts, v.Cur.Fixed);
        bool was = edits.ContainsKey(a.Id);
        if (Same(next, v.Saved)) edits.Remove(a.Id); else edits[a.Id] = next;
        selPoint = sel;
        if (was != edits.ContainsKey(a.Id)) Refresh(); // tab/pill with the change dot and the previous curve in the legend
        else RefreshEdit(fromChart: true);
    }

    private void SetEdit(View v, Edit next)
    {
        if (Same(next, v.Saved)) edits.Remove(v.A.Id); else edits[v.A.Id] = next;
        pointsKey = ""; Refresh(force: true);
    }

    // ------------------------------------------------------------------ strip, tabs and pills

    private void RefreshStrip(List<Act> acts)
    {
        var sel = ThermalMap.DeviceOf(selected);
        strip.Children.Clear();
        var snap = Snap();
        if (GpuOk)
        {
            var r = gpu!.State.Reading!;
            strip.Children.Add(Group(ThermalDevice.Gpu, "GPU", GpuChip(), ThermalMap.GpuStrip(r.Fans.Select(f => (f.SpeedPercent, f.SpeedRpm))), sel));
        }
        if (BoardOk)
            strip.Children.Add(Group(ThermalDevice.Board, "BOARD", MonitorTray.App.AsusFanModes.Label(BoardMode),
                ThermalMap.BoardStrip(asus!.Fans, Nick, snap?.MbFanRpm ?? new Dictionary<int, float>(), snap?.MbFanControl ?? new Dictionary<int, float>(), asus.Off.IsOff), sel));
        if (CoolerOk)
        {
            var r = cooler!.Latest; var ap = cooler.FanStatus.Applied;
            strip.Children.Add(Group(ThermalDevice.Cooler, "RYUJIN II", CoolerChip(), ThermalMap.CoolerStrip(r?.Pump?.PumpRpm, ap?.PumpPercent ?? r?.PumpPercent,
                r?.Fans?.Rpm ?? [], ap?.RadiatorPercent ?? r?.Fans?.DutyPercent, r?.Pump?.EmbeddedFanRpm, ap?.EmbeddedPercent ?? r?.EmbeddedPercent, CoolerNick), sel));
        }

        // Device tabs and actuator pills (click on press: the periodic refresh does not lose the click).
        tabs.Children.Clear();
        foreach (var d in new[] { ThermalDevice.Gpu, ThermalDevice.Board, ThermalDevice.Cooler })
        {
            var mine = acts.Where(a => a.Dev == d).ToList();
            if (mine.Count == 0) continue;
            bool on = sel == d;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                Ui.T(DevName(d), 14, on ? Theme.Text : Theme.Muted, weight: 600).With(t => t.VerticalAlignment = VerticalAlignment.Center),
                Chip(d == ThermalDevice.Gpu ? GpuChip() : d == ThermalDevice.Board ? MonitorTray.App.AsusFanModes.Label(BoardMode) : CoolerChip(), on).With(c => c.Margin = new Thickness(8, 0, 6, 0)),
                Ui.T(d == ThermalDevice.Gpu ? "" : $"{mine.Count} curves", 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center),
            } };
            if (mine.Any(a => edits.ContainsKey(a.Id))) row.Children.Add(Dot().With(x => x.Margin = new Thickness(6, 0, 0, 0)));
            var inner = new Border { Padding = new Thickness(12, 8, 14, 7), BorderThickness = new Thickness(on ? 1 : 0, 0, on ? 1 : 0, 0), Child = row };
            var tab = new Grid { Margin = new Thickness(0, 0, 0, -1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Children = { inner } };
            if (on)
            {
                inner.SetResourceReference(Border.BorderBrushProperty, Theme.Line); inner.SetResourceReference(Border.BackgroundProperty, Theme.Bg);
                var accent = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top };
                accent.SetResourceReference(Border.BackgroundProperty, Theme.DStroke);
                tab.Children.Add(accent);
            }
            var dd = d;
            tab.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (ThermalMap.DeviceOf(selected) != dd) Select(lastByDevice.TryGetValue(dd, out var last) && acts.Any(a => a.Id == last) ? last : mine[0].Id); };
            tabs.Children.Add(tab);
        }

        pills.Children.Clear();
        foreach (var a in acts.Where(x => x.Dev == sel))
        {
            bool on = a.Id == selected;
            var pct = PercentNow(a);
            bool off = a.Dev == ThermalDevice.Board && asus!.Off.IsOff(ThermalMap.BoardFan(a.Id)!);
            var t = new TextBlock { FontFamily = AppFonts.For(true, on ? 600 : 400), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            t.Inlines.Add(Rn(a.Name, on ? Theme.Bg : Theme.Text));
            t.Inlines.Add(Rn(off ? "  OFF" : pct is int p ? $"  {p}%" : "", on ? Theme.Bg : Theme.Muted, size: 11));
            var pill = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 4, 0), Child = t, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            pill.SetResourceReference(Border.BorderBrushProperty, on ? Theme.Fill : Theme.Line);
            if (on) pill.SetResourceReference(Border.BackgroundProperty, Theme.Fill);
            if (edits.ContainsKey(a.Id)) t.Inlines.Add(Rn("  ●", on ? Theme.Bg : Theme.UStroke, size: 9));
            var id = a.Id;
            pill.MouseLeftButtonDown += (_, e) => { e.Handled = true; Select(id); };
            pill.ToolTip = a.Tip;
            pills.Children.Add(pill);
        }
    }

    private int? PercentNow(Act a)
    {
        if (a.Dev == ThermalDevice.Gpu) return gpu?.State.Reading?.Fans.Max(f => (int?)f.SpeedPercent);
        if (a.Dev == ThermalDevice.Board) { var s = Snap(); return BoardFanMap.Read(ThermalMap.BoardFan(a.Id)!, s?.MbFanRpm ?? new Dictionary<int, float>(), s?.MbFanControl ?? new Dictionary<int, float>()).Percent; }
        var ap = cooler?.FanStatus.Applied; var r = cooler?.Latest;
        return a.Id switch { ThermalMap.PumpId => ap?.PumpPercent ?? r?.PumpPercent, ThermalMap.RadiatorId => ap?.RadiatorPercent ?? r?.Fans?.DutyPercent, _ => ap?.EmbeddedPercent ?? r?.EmbeddedPercent };
    }

    private string GpuChip() => gpu?.State is { } s ? s.Mode switch { GpuMode.Automatic => "AUTO", GpuMode.Manual => s.RequestedPercent is uint p ? $"{p}%" : "MANUAL", GpuMode.Mixed => "MIXED", _ => "—" } : "—";

    private string CoolerChip()
    {
        var c = cooler!.FanConfig;
        if (!c.Enabled) return "READ ONLY";
        return CoolerModes.UniformPreset(c) switch { RyujinCurves.Preset.Silent => "SILENT", RyujinCurves.Preset.Standard => "STD", RyujinCurves.Preset.Turbo => "TURBO", RyujinCurves.Preset.FullSpeed => "FULL", _ => "CURVES" };
    }

    private Border Group(ThermalDevice d, string name, string chip, IReadOnlyList<StripItem> items, ThermalDevice? sel)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4), Children =
        {
            Ui.T(name, 10.5, sel == d ? Theme.DStroke : Theme.Text, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); }),
            Chip(chip, false),
        } };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var it in items) row.Children.Add(Cell(it));
        var g = new Border { BorderThickness = new Thickness(strip.Children.Count == 0 ? 0 : 1, 0, 0, 0), Padding = new Thickness(strip.Children.Count == 0 ? 0 : 12, 0, 12, 0), Margin = new Thickness(0, 0, 0, 2), Child = new StackPanel { Children = { head, row } } };
        g.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        return g;
    }

    /// <summary>A fan in the strip: name (the user's, if renamed), measured rpm, %, mini bar. Selected: outline in color A; off: dashed.</summary>
    private FrameworkElement Cell(StripItem it)
    {
        bool sel = it.ActuatorId == selected;
        var name = new TextBlock { FontFamily = AppFonts.Head, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 104 };
        name.Inlines.Add(Rn(it.Nick ?? it.Name, Theme.Text, 500, 10));
        if (it.Off) name.TextDecorations = TextDecorations.Strikethrough;
        var val = new TextBlock { FontFamily = AppFonts.HeadSemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Bottom };
        System.Windows.Documents.Typography.SetNumeralAlignment(val, FontNumeralAlignment.Tabular);
        if (it.Off) { val.FontSize = 13; val.Inlines.Add(Rn("⏻ OFF", Theme.Text, 600, 13)); }
        else if (it.Rpm is int r) { val.Inlines.Add(Rn(r.ToString(CultureInfo.InvariantCulture), Theme.Text, 600, 15)); val.Inlines.Add(Rn(" rpm", Theme.Muted, 400, 9.5)); }
        else val.Inlines.Add(Rn("—", Theme.Muted, 600, 15));
        var pct = Ui.T(it.Off ? (it.Rpm is int ro ? $"{ro} rpm" : "") : it.Percent is int p ? p + "%" : "", 10.5, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(8, 0, 0, 1); t.TextTrimming = TextTrimming.None; });
        var line = new DockPanel { LastChildFill = true };
        SetDock(pct, Dock.Right); line.Children.Add(pct); line.Children.Add(val);
        var bar = new Bar { Height = 3, Margin = new Thickness(0, 2, 0, 0), Animate = false };
        bar.SetFraction(it.Off ? 0 : it.Percent is int q ? q / 100.0 : 0, it.Percent >= 85);
        var content = new StackPanel { Children = { name, line, bar } };
        var grid = new Grid { Children = { content } };
        if (it.Locked)
        {
            var (lk, _) = Icons.Make(Icons.Lock, 9, Theme.Muted);
            lk.HorizontalAlignment = HorizontalAlignment.Right; lk.VerticalAlignment = VerticalAlignment.Top; lk.Margin = new Thickness(0, 1, 0, 0);
            grid.Children.Add(lk);
        }
        var b = new Border { BorderThickness = new Thickness(it.Off ? 0 : 1), Padding = new Thickness(6, 3, 6, 4), Margin = new Thickness(0, 0, 3, 0), MinWidth = 78, Child = grid, Background = Brushes.Transparent };
        b.SetResourceReference(Border.BorderBrushProperty, sel ? Theme.DStroke : Theme.Line);
        if (sel) b.SetResourceReference(Border.BackgroundProperty, Theme.Sel);
        else if (it.Off) b.SetResourceReference(Border.BackgroundProperty, Theme.Track);
        if (it.Off)
        {
            var dash = new Rectangle { StrokeThickness = 1, StrokeDashArray = [3, 2], IsHitTestVisible = false, Margin = new Thickness(-6, -3, -6, -4) };
            dash.SetResourceReference(Shape.StrokeProperty, sel ? Theme.DStroke : Theme.Muted);
            grid.Children.Add(dash);
        }
        if (it.Rpm == null && it.Percent == null && !it.Off) b.Opacity = 0.5;
        if (it.ActuatorId is string id)
        {
            b.Cursor = Cursors.Hand;
            b.MouseLeftButtonDown += (_, e) => { e.Handled = true; Select(id); };
        }
        b.ToolTip = (it.Nick != null ? it.Nick + " · " + it.Name : it.Name) + (it.Off ? "\nOFF (protection curve on the chip)" : "")
            + (it.Rpm == null && !it.Off ? "\nrpm not measured on this header" : "") + (it.Locked ? "\nCPU and pumps never turn off" : "");
        return b;
    }

    // ------------------------------------------------------------------ static part (buttons)

    private void RebuildStatic(List<Act> acts)
    {
        var a = acts.First(x => x.Id == selected);
        var v = Build(a);
        lastByDevice[a.Dev] = a.Id;

        // Device mode: the same buttons as in the popup.
        modes.Children.Clear();
        modeLabel.Text = a.Dev == ThermalDevice.Gpu ? "FAN SPEED" : a.Dev == ThermalDevice.Board ? "MODE · ARMOURY CRATE" : "MODE · RYUJIN II";
        if (a.Dev == ThermalDevice.Gpu) GpuModes(); else if (a.Dev == ThermalDevice.Board) BoardModes(); else CoolerModesRow();

        // Large title.
        title.Inlines.Clear();
        title.Inlines.Add(Rn(DevName(a.Dev), Theme.Muted, 500, 30));
        title.Inlines.Add(Rn("  ·  ", Theme.Muted, 400, 30));
        title.Inlines.Add(Rn(a.Name, Theme.Text, 600, 30));
        if (a.Sub != null) { title.Inlines.Add(Rn("  ·  ", Theme.Muted, 400, 30)); title.Inlines.Add(Rn(a.Sub, Theme.Muted, 500, 30)); }
        title.ToolTip = a.Tip;

        // Control: CURVE/FIXED on the cooler; board and GPU only display.
        if (a.Dev == ThermalDevice.Cooler)
        {
            var seg = new Segmented([((object)false, "CURVE"), ((object)true, "FIXED")]);
            seg.Select(v.Cur.Fixed);
            seg.Changed += x =>
            {
                bool fx = (bool)x; if (fx == v.Cur.Fixed) return;
                var now = v.Temp ?? (v.Axis.Min + v.Axis.Max) / 2;
                var (s, fan) = Setting(cooler!.FanConfig, a.Id);
                SetEdit(v, fx ? Flat(CurveEdit.Fixed(v.Cur.Fixed ? v.Cur.Points[0].Percent : CurveEdit.Interpolate(v.Cur.Points, now), v.Limits), v.Axis)
                    : v.Saved.Fixed ? new Edit(CurveEdit.Normalize(CoolerLogic.CurveOf(s with { Mode = CoolerMode.Smart }, fan).Select(n => new CurvePoint(n.TempC, n.Percent)), v.Limits), false) : v.Saved);
            };
            control.Child = Field("CONTROL", seg);
        }
        else control.Child = Field("CONTROL", Chip(a.Dev == ThermalDevice.Board ? (v.Flat ? "FIXED 100%" : "CURVE ON CHIP") : GpuChip() == "AUTO" ? "AUTO · DRIVER" : "FIXED " + GpuChip(), true).With(c => c.Padding = new Thickness(8, 3, 8, 3)));

        hint.Text = a.Dev switch
        {
            ThermalDevice.Gpu => "In automatic the driver decides the curve; Marz only sets a fixed % (30/50/75/100) and returns to automatic on exit.",
            ThermalDevice.Board => (v.Block == null ? $"The curve runs on the board chip: {v.Limits.MaxPoints} points, no adding or removing. Apply writes the User curve via Armoury Crate (same path as FULL/OFF)."
                : $"Read only — {v.Block}.") + (ThermalMap.BoardSource(ThermalMap.BoardFan(a.Id)!) == ThermalMap.GpuSource ? " This header follows the GPU." : ""),
            _ => a.Id == ThermalMap.EmbeddedId && v.Cur.Fixed ? "Fixed: does not follow temperature. The cooler curve runs on the PC (Marz sends the % every second)."
                : $"The curve runs on the PC: Marz computes the % every second and sends it to the cooler (up to {ThermalMap.CoolerMaxPoints} points)." + (v.SourceId == RyujinSensorIds.Water ? " Liquid changes slowly: axis 20–60 °C." : ""),
        };
        legendGhost.Visibility = edits.ContainsKey(a.Id) ? Visibility.Visible : Visibility.Collapsed;
        legendCurveText.Text = v.Editable ? "editing" : a.Dev == ThermalDevice.Gpu ? "fixed by Marz" : "active mode's curve";
        legendRight.Text = v.Editable && !v.Cur.Fixed ? (v.Limits.FixedCount ? "drag the points · arrows adjust 1 °C / 1 %" : "drag the points · double-click adds · Del removes")
            : v.Editable ? "drag the line to change the %" : "read only";

        // Warning banner: board fan turned off.
        banner.Visibility = Visibility.Collapsed;
        if (a.Dev == ThermalDevice.Board && asus!.Off.IsOff(ThermalMap.BoardFan(a.Id)!))
        {
            var fan = ThermalMap.BoardFan(a.Id)!;
            var turnOn = SmallButton("TURN ON", "Turns the fan back on (User curve back + current mode)", () => { asus.TurnOn([fan], "thermal control"); Refresh(force: true); });
            var (pw, _) = Icons.Make(Icons.Power, 14, Theme.Text); pw.VerticalAlignment = VerticalAlignment.Center; pw.Margin = new Thickness(0, 0, 8, 0);
            var row = new DockPanel { LastChildFill = true };
            SetDock(pw, Dock.Left); row.Children.Add(pw);
            var lbl = Ui.T("OFF", 13, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 10, 0); });
            SetDock(lbl, Dock.Left); row.Children.Add(lbl);
            SetDock(turnOn, Dock.Right); row.Children.Add(turnOn);
            row.Children.Add(Ui.T($"{a.Name} at 0% up to {BoardFanOff.CurveOffUntilC} °C and 100% from {BoardFanOff.CurveFullFromC} °C on (protection curve). The User curve is kept and comes back when turned back on.", 12, Theme.Muted, head: false)
                .With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.VerticalAlignment = VerticalAlignment.Center; }));
            var grid = new Grid { Children = { new Border { Padding = new Thickness(10, 6, 10, 6), Child = row } } };
            var dash = new Rectangle { StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
            dash.SetResourceReference(Shape.StrokeProperty, Theme.Muted);
            grid.Children.Add(dash);
            banner.Child = grid; banner.SetResourceReference(Border.BackgroundProperty, Theme.Track);
            banner.Visibility = Visibility.Visible;
        }
        else if (a.Dev == ThermalDevice.Cooler && cooler!.FanStatus is { Tuning: string tn } ts)
        {
            var cancel = SmallButton("CANCEL", "Stops Auto Tuning and returns to the curve", () => tuneCts?.Cancel());
            var row = new DockPanel { LastChildFill = true };
            SetDock(cancel, Dock.Right); row.Children.Add(cancel);
            row.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), Children =
            {
                new TextBlock { Inlines = { Rn("RADIATOR AUTO TUNING  ", Theme.Text, 600, 13), Rn(tn + (ts.TuneSecondsLeft is int sl ? $" · ~{sl} s" : ""), Theme.Muted, 400, 12) } },
                Progress(ts.TuneProgress ?? 0),
            } });
            banner.Child = new Border { Padding = new Thickness(10, 7, 10, 8), Child = row };
            banner.SetResourceReference(Border.BackgroundProperty, Theme.Track);
            banner.Visibility = Visibility.Visible;
        }

        RebuildSide(a, v);
    }

    private void GpuModes()
    {
        var st = gpu!.State; var now = DateTimeOffset.UtcNow;
        foreach (var val in new uint?[] { null, 30, 50, 75, 100 })
        {
            bool on = val is null ? st.Mode == GpuMode.Automatic : st.Mode == GpuMode.Manual && st.RequestedPercent == val;
            string? block = val is uint p ? st.BlockFor(p, now) : !st.CanControl ? st.ReadOnlyReason ?? "Control unavailable." : st.Busy ? "Command in progress." : null;
            var x = val;
            modes.Children.Add(ModeBtn(val is uint pv ? pv.ToString(CultureInfo.InvariantCulture) : "A", on, val == 100, block ?? (val is null ? "Return to driver automatic" : $"Set the fans to {val}%"), block == null,
                async () => { FanChoices.Update(d => d with { Gpu = x }); try { await gpu.CommandAsync(x); } catch (Exception) { } Dispatcher.Invoke(() => Refresh()); }));
        }
    }

    private void BoardModes()
    {
        var fans = app.Settings.AcFans.Where(asus!.Fans.Contains).ToList();
        foreach (var mode in MonitorTray.App.AsusFanModes.AllModes)
        {
            var m = mode;
            var tip = (mode == "curves" ? "Your Armoury Crate curves (User profile)" : mode == "full" ? "Fans at 100% (turns the off ones back on)" : $"Armoury Crate {MonitorTray.App.AsusFanModes.Label(mode)} profile")
                + "\nFans: " + (fans.Count == 0 ? "none (choose them in the popup Settings)" : string.Join(", ", fans.Select(app.Settings.BoardFanName)));
            modes.Children.Add(ModeBtn(MonitorTray.App.AsusFanModes.Label(mode), BoardMode == mode, mode == "full", tip, fans.Count > 0, () =>
            {
                foreach (var k in edits.Keys.Where(k => ThermalMap.DeviceOf(k) == ThermalDevice.Board).ToList()) edits.Remove(k);
                asus.Apply(m, fans); Refresh(force: true);
            }));
        }
    }

    private static readonly (RyujinCurves.Preset? P, string Label)[] CoolerPresets =
        [(null, "CURVES"), (RyujinCurves.Preset.Silent, "SILENT"), (RyujinCurves.Preset.Standard, "STD"), (RyujinCurves.Preset.Turbo, "TURBO"), (RyujinCurves.Preset.FullSpeed, "FULL")];

    private void CoolerModesRow()
    {
        var c = cooler!.FanConfig;
        RyujinCurves.Preset? cur = c.Enabled ? CoolerModes.UniformPreset(c) : null;
        foreach (var (p, lbl) in CoolerPresets)
        {
            var pp = p;
            modes.Children.Add(ModeBtn(lbl, c.Enabled && (p == null ? cur == null : cur == p), p == RyujinCurves.Preset.FullSpeed,
                (pp is RyujinCurves.Preset ? $"Pump and radiator on {lbl}" : "Pump and radiator on the cooler's curves (the ones from before the preset)") + (c.Enabled ? "" : "\n(turns on cooler control by Marz)"), true, () =>
            {
                foreach (var k in edits.Keys.Where(k => ThermalMap.DeviceOf(k) == ThermalDevice.Cooler).ToList()) edits.Remove(k);
                if (pp is RyujinCurves.Preset preset) cooler.ApplyPreset(preset, enable: true); else cooler.ApplyCurves();
                cooler.ResumeControl(); Refresh(force: true);
            }));
        }
    }

    // ------------------------------------------------------------------ side panel

    private void RebuildSide(Act a, View v)
    {
        side.Children.Clear();
        (nowBox.Parent as Panel)?.Children.Remove(nowBox); // reused: detach it from the previous section
        side.Children.Add(Section("WITH THIS CURVE, NOW", "preview", nowBox));

        // Curve source (X axis).
        var src = new StackPanel();
        if (a.Dev == ThermalDevice.Cooler)
        {
            var btn = new FlatButton(Theme.Text) { Height = 30, Padding = new Thickness(10, 0, 10, 0), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "Sensor the cooler curve follows (applies to pump, radiator and embedded fan)" };
            btn.SetResourceReference(Border.BorderBrushProperty, Theme.DStroke);
            btn.Content(new DockPanel { Children =
            {
                Ui.T("▾", 12, Theme.Muted).With(t => { SetDock(t, Dock.Right); t.VerticalAlignment = VerticalAlignment.Center; }),
                Ui.T(Fmt(v.Temp, "0.0", " °C"), 15, weight: 600, tabular: true).With(t => { SetDock(t, Dock.Right); t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(8, 0, 8, 0); }),
                Ui.T(SourceName(v.SourceId), 13, head: false, weight: 500).With(t => t.VerticalAlignment = VerticalAlignment.Center),
            } });
            btn.Click += () => pickSensor(btn, id => { pendingSource = id == cooler!.FanConfig.SourceId ? null : id; pointsKey = ""; Refresh(force: true); });
            src.Children.Add(btn);
            src.Children.Add(Note(pendingSource != null ? "New source: takes effect on Apply. Applies to all three cooler curves." : "Applies to pump, radiator and embedded fan. Default: CPU, like Armoury Crate."));
        }
        else
        {
            src.Children.Add(Ui.Box(new Thickness(1), new Thickness(10, 5, 10, 5), new DockPanel { Children =
            {
                Ui.T(Fmt(v.Temp, "0.0", " °C"), 15, weight: 600, tabular: true).With(t => { SetDock(t, Dock.Right); t.VerticalAlignment = VerticalAlignment.Center; }),
                Ui.T(SourceName(v.SourceId), 13, head: false, weight: 500).With(t => t.VerticalAlignment = VerticalAlignment.Center),
            } }));
            src.Children.Add(Note(a.Dev == ThermalDevice.Board ? "Header source in Armoury Crate (read only here)." : "Core temperature; in automatic the driver uses its own."));
        }
        side.Children.Add(Section("CURVE SOURCE (X AXIS)", null, src));

        if (a.Dev == ThermalDevice.Cooler) side.Children.Add(Section("COOLER CONTROL", null, CoolerControl()));
        if (a.Dev == ThermalDevice.Board) side.Children.Add(Section("BOARD FANS OFF", $"{asus!.Off.Off.Count} of {asus.Fans.Count(f => !BoardFanOff.Locked(f))} switchable", BoardOffList()));

        side.Children.Add(ProtectionsBox(Protections(a.Dev).ToList()));
    }

    private IEnumerable<string> Protections(ThermalDevice d) => d switch
    {
        ThermalDevice.Cooler =>
        [
            $"Pump never below {CoolerSafety.PumpMinPercent}%",
            $"Liquid ≥ {CoolerSafety.WaterPanicC:0} °C → pump and radiator at 100%",
            $"No source reading → {CoolerSafety.NoSourcePercent}%",
            $"On closing Marz → leaves ≥ {CoolerSafety.ExitRadiatorPercent}% (the cooler keeps the last value)",
            "Another program writing to the cooler → Marz stops and warns",
            "Rises up to 10 %/s and falls 3 %/s (smoothing)",
        ],
        ThermalDevice.Board =>
        [
            "The curve runs on the chip: still in effect without Marz",
            $"CPU_FAN and pumps: never below {ThermalMap.BoardLockedMinPercent}% and never turn off",
            "Write verified point by point; if it doesn't match, the previous curve comes back",
            "SILENT, STD and TURBO belong to Armoury Crate: read only",
        ],
        _ =>
        [
            $"Core ≥ {TempLimits.GpuC:0} °C (or driver limit − 3) or thermal throttling → 100%",
            "Below 30% only with core < 50 °C",
            "VRAM ≥ 95 °C → 100%",
            "On quitting Marz → back to driver automatic",
        ],
    };

    /// <summary>Collapsed protections: just the title with an info icon (tooltip with the rules); a click shows the list.</summary>
    private Border ProtectionsBox(IReadOnlyList<string> rules)
    {
        var list = new StackPanel { Margin = new Thickness(0, 7, 0, 0), Visibility = protOpen ? Visibility.Visible : Visibility.Collapsed };
        foreach (var t in rules) list.Children.Add(Rule(Icons.Shield, t));
        var (ic, _) = Icons.Make(Icons.Info, 13, Theme.Muted);
        ic.VerticalAlignment = VerticalAlignment.Center;
        var chev = Ui.T(protOpen ? "▴" : "▾", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(6, 0, 0, 0); });
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { ic, chev } };
        var head = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = string.Join("\n", rules) };
        SetDock(right, Dock.Right); head.Children.Add(right);
        head.Children.Add(Ui.T($"PROTECTIONS ({rules.Count})", 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        head.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; protOpen = !protOpen;
            list.Visibility = protOpen ? Visibility.Visible : Visibility.Collapsed; chev.Text = protOpen ? "▴" : "▾";
        };
        return Ui.Box(new Thickness(0), new Thickness(14, 11, 14, 11), new StackPanel { Children = { head, list } });
    }

    private static Border Progress(double frac)
    {
        var g = new Grid { Height = 6, Margin = new Thickness(0, 6, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(frac, 0.001, 1), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(1 - frac, 0.001, 1), GridUnitType.Star) });
        var fill = new Border(); fill.SetResourceReference(Border.BackgroundProperty, Theme.Fill);
        g.Children.Add(fill);
        var bg = new Border { Child = g }; bg.SetResourceReference(Border.BackgroundProperty, Theme.Track);
        return bg;
    }

    private StackPanel CoolerControl()
    {
        var c = cooler!.FanConfig; var st = cooler.FanStatus;
        var on = new Check15 { Value = c.Enabled, VerticalAlignment = VerticalAlignment.Center };
        on.Changed += x => { cooler.SaveFans(cooler.FanConfig with { Enabled = x }); if (x) cooler.ResumeControl(); Refresh(force: true); };
        var p = new StackPanel();
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, ToolTip = "Off: Marz only reads the cooler (let Armoury Crate control it)", Children =
        {
            on, Ui.T("MARZ CONTROLS PUMP AND FANS", 11, weight: 600).With(t => { t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }),
        } });
        if (st.Tuning != null)
        {
            p.Children.Add(Ui.T("AUTO TUNING IN PROGRESS", 13, weight: 600).With(t => t.Margin = new Thickness(0, 8, 0, 0)));
            p.Children.Add(Ui.T(st.Tuning + (st.TuneSecondsLeft is int sl ? $" · ~{sl} s" : ""), 11.5, Theme.Muted, head: false).With(t => t.Margin = new Thickness(0, 2, 0, 0)));
            p.Children.Add(Progress(st.TuneProgress ?? 0));
            p.Children.Add(Note("Measuring radiator rpm from 0 to 100%. The curve is paused; the radiator changes speed on its own. The pump does not change."));
        }
        else p.Children.Add(Note((!c.Enabled ? "Read only. Applying a curve or a mode turns control on (Armoury Crate must not be controlling the cooler)."
            : st.Notice is { } n ? "⚠ " + n : st.Applied is { } ap ? $"Applying: pump {ap.PumpPercent}% · radiator {ap.RadiatorPercent}% · embedded {ap.EmbeddedPercent}%" : "Starting…")));
        var tuneLbl = cooler.Tuning ? "CANCEL" : "AUTO TUNING";
        var tune = SmallButton(tuneLbl, "Measures radiator rpm from 0 to 100% (~1 min, loud near 100%). The pump does not change.", () =>
        {
            if (cooler.Tuning) { tuneCts?.Cancel(); return; }
            tuneCts = new CancellationTokenSource();
            _ = cooler.TuneRadiatorAsync(tuneCts.Token).ContinueWith(_ => Dispatcher.BeginInvoke(() => Refresh(force: true)));
            Refresh(force: true);
        });
        p.Children.Add(new DockPanel { Margin = new Thickness(0, 6, 0, 0), Children =
        {
            tune.With(b => SetDock(b, Dock.Right)),
            Ui.T(c.RadiatorTunedAt is { } at ? $"radiator measured {at:MMM d, HH:mm}" : "radiator rpm table: default", 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center),
        } });
        return p;
    }

    private StackPanel BoardOffList()
    {
        var p = new StackPanel();
        var off = asus!.Off.Off;
        if (off.Count == 0) p.Children.Add(Note("None. OFF is set by clicking the fan card in the popup (experimental option in Settings)."));
        foreach (var f in off)
        {
            var fan = f;
            var (pw, _) = Icons.Make(Icons.Power, 12, Theme.Text); pw.VerticalAlignment = VerticalAlignment.Center; pw.Margin = new Thickness(0, 0, 8, 0);
            var row = new DockPanel { LastChildFill = true };
            SetDock(pw, Dock.Left); row.Children.Add(pw);
            var b = SmallButton("TURN ON", "Turns this fan back on", () => { asus.TurnOn([fan], "thermal control"); Refresh(force: true); });
            SetDock(b, Dock.Right); row.Children.Add(b);
            var t = new TextBlock { FontFamily = AppFonts.Head, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            t.Inlines.Add(Rn(app.Settings.BoardFanName(f), Theme.Text, 600, 12.5));
            if (Nick(f) != null) { t.Inlines.Add(Rn(" · " + ThermalMap.BoardHeader(f), Theme.Muted, 400, 12.5)); t.ToolTip = app.Settings.BoardFanTip(f); }
            row.Children.Add(t);
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4), Children = { new Border { Padding = new Thickness(6, 4, 6, 4), Child = row } } };
            var dash = new Rectangle { StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
            dash.SetResourceReference(Shape.StrokeProperty, Theme.Muted);
            g.Children.Add(dash);
            p.Children.Add(g);
        }
        p.Children.Add(new Border { Height = 6 });
        foreach (var r in new[]
        {
            $"Turns back on by itself if CPU > {BoardFanOff.CpuLimitC:0} °C or GPU > {BoardFanOff.GpuLimitC:0} °C",
            "FULL and game mode turn them all back on",
            "Not persisted: quitting or restarting Marz turns it back on",
            $"On the chip: 0% up to {BoardFanOff.CurveOffUntilC} °C and 100% from {BoardFanOff.CurveFullFromC} °C on, even if Marz hangs",
        }) p.Children.Add(Rule(Icons.Power, r));
        return p;
    }

    // ------------------------------------------------------------------ editing (chart, points, comparison)

    private void RefreshEdit(bool fromChart = false)
    {
        var acts = Acts(); var a = acts.FirstOrDefault(x => x.Id == selected); if (a == null) return;
        var v = Build(a);
        var pts = v.Cur.Points;
        if (!fromChart)
            chart.Set(new CurveChartModel
            {
                Axis = v.Axis, Points = pts, Ghost = edits.ContainsKey(a.Id) ? v.Saved.Points : null,
                Editable = v.Editable, FixedLine = v.Cur.Fixed || v.Flat, Limits = v.Limits, Now = v.Temp, NowPercent = v.NowPercent, Guards = v.Guards, Overlay = v.Overlay,
                XLabel = SourceName(v.SourceId).ToUpperInvariant() + " °C",
                YLabel = $"{DevName(a.Dev)} · {a.Name.ToUpperInvariant()} · OUTPUT %",
            }, selPoint);
        selPoint = chart.Selected >= 0 ? chart.Selected : selPoint;

        // Subtitle: mode, source and fans of the actuator.
        string what = a.Dev == ThermalDevice.Gpu ? (v.Flat ? $"FIXED {v.Cur.Points[0].Percent:0}%" : "AUTO") : v.Cur.Fixed || v.Flat ? $"FIXED {v.Cur.Points[0].Percent:0}%" : "CURVE";
        var owner = a.Dev switch { ThermalDevice.Gpu => "driver", ThermalDevice.Board => "board chip", _ => "Marz (on the PC)" };
        sub.Inlines.Clear();
        _ = owner; // no "owner": the selected mode button already says who is in control
        sub.Inlines.Add(Rn(what, Theme.Text, 600, 12));
        if (!(v.Cur.Fixed && a.Dev == ThermalDevice.Cooler)) { sub.Inlines.Add(Rn($" ← {SourceName(v.SourceId)} ", Theme.Muted, 400, 12)); sub.Inlines.Add(Rn(Fmt(v.Temp, "0.0", " °C"), Theme.Text, 600, 12)); sub.Inlines.Add(Rn(" (5 s avg)", Theme.Muted, 400, 11)); }
        var fans = FansOf(a);
        if (fans.Count > 0) sub.Inlines.Add(Rn("  ·  " + string.Join(", ", fans.Select(f => f.Name)), Theme.Muted, 400, 12));

        // Points row (rebuilt only when the points change).
        var pk = $"{a.Id}|{v.Editable}|{v.Cur.Fixed}|{v.Flat}|{selPoint}|{string.Join(";", pts.Select(p => $"{p.TempC:0},{p.Percent:0}"))}";
        if (pk != pointsKey) { pointsKey = pk; BuildPoints(a, v); }

        // "With this curve, now" and comparison with the saved one.
        double? op = v.NowPercent ?? (pts.Count > 0 && (v.Temp != null || v.Cur.Fixed || v.Flat) ? CurveEdit.Interpolate(pts, v.Temp ?? v.Axis.Min) : null);
        double? before = v.Saved.Points.Count > 0 && (v.Temp != null || v.Saved.Fixed || v.Flat) ? CurveEdit.Interpolate(v.Saved.Points, v.Temp ?? v.Axis.Min) : null;
        var applied = PercentNow(a);
        nowBox.Children.Clear();
        var big = new TextBlock { FontFamily = AppFonts.HeadSemiBold, FontSize = 40, LineHeight = 40, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        System.Windows.Documents.Typography.SetNumeralAlignment(big, FontNumeralAlignment.Tabular);
        big.Inlines.Add(Rn(op is double o ? Math.Round(o).ToString("0", CultureInfo.InvariantCulture) : "—", Theme.Text, 600, 40));
        big.Inlines.Add(Rn(" %", Theme.Muted, 400, 16));
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Children =
        {
            Ui.T(SourceName(v.SourceId), 11, Theme.Muted).With(t => t.HorizontalAlignment = HorizontalAlignment.Right),
            Ui.T(Fmt(v.Temp, "0.0", " °C"), 18, weight: 600, tabular: true).With(t => t.HorizontalAlignment = HorizontalAlignment.Right),
        } };
        nowBox.Children.Add(new DockPanel { Children = { right.With(r => SetDock(r, Dock.Right)), big } });
        nowBox.Children.Add(Ui.T($"ACTUALLY APPLIED · {(applied is int ap ? ap + "%" : "—")} · {(fans.Count > 1 ? fans.Count + " fans" : "1 fan")}", 10, Theme.Muted).With(t => t.Margin = new Thickness(0, 10, 0, 4)));
        var fg = new UniformGrid { Columns = Math.Clamp(fans.Count, 1, 3) };
        foreach (var f in fans)
            fg.Children.Add(Ui.Box(new Thickness(1), new Thickness(6, 2, 6, 3), new StackPanel { Children =
            {
                Ui.T(f.Name, 9, Theme.Muted),
                Ui.T(f.Off ? "OFF" : f.Rpm is int r ? r + " rpm" : "— rpm", 13, weight: 600, tabular: true),
            } }).With(b => b.Margin = new Thickness(0, 0, 3, 3)));
        nowBox.Children.Add(fg);
        if (fans.Any(f => f.Rpm == null && !f.Off)) nowBox.Children.Add(Note("rpm only when measured (a header without a confirmed reading shows —)."));

        var cmp = new DockPanel { LastChildFill = true };
        cmp.Children.Add(Cmp(v.Temp is double tt ? $"AT {tt:0.0} °C (NOW)" : v.Cur.Fixed ? "FIXED" : "NO SOURCE READING",
            (op is double o2 ? $"{o2:0}%" : "—"), before is double b0 && edits.ContainsKey(a.Id) ? $"before {b0:0}%" : applied is int ap2 ? $"applied {ap2}%" : "").With(x => SetDock(x, Dock.Left)));
        if (edits.ContainsKey(a.Id) && op is double o3 && before is double b1)
        {
            var d = Math.Round(o3) - Math.Round(b1);
            cmp.Children.Add(Cmp("DIFF", $"{(d > 0 ? "+" : d < 0 ? "−" : "")}{Math.Abs(d):0}", "pp").With(x => SetDock(x, Dock.Left)));
        }
        cmp.Children.Add(Ui.T(CompareText(a, v), 11.5, Theme.Muted, head: false).With(t => { t.Margin = new Thickness(12, 0, 12, 0); t.VerticalAlignment = VerticalAlignment.Center; t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        compare.Child = cmp;

        // Action bar.
        var pending = acts.Where(x => edits.ContainsKey(x.Id)).Select(x => (x, Build(x))).ToList();
        bool any = pending.Count > 0 || pendingSource != null;
        changes.Text = any ? string.Join(" · ", pending.Select(p => $"{p.x.Name}: {ChangeText(p.Item2.Saved, p.Item2.Cur)}").Concat(pendingSource != null ? [$"source: {SourceName(pendingSource)}"] : [])) : "No changes";
        var devs = pending.Select(p => p.x.Dev).Concat(pendingSource != null ? [ThermalDevice.Cooler] : []).Distinct().ToList();
        changesNote.Text = devs.Count == 0 ? "" : "· " + string.Join(" · ", devs.Select(d => d == ThermalDevice.Cooler ? "RYUJIN II: saved as CURVES, turns on control by Marz" : "board: writes the User curve to Armoury Crate")) + " · other devices don't change";
        changesDot.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        apply.IsEnabled = any; apply.Opacity = any ? 1 : 0.45; apply.Active = true;
        discard.IsEnabled = any; discard.Opacity = any ? 1 : 0.45;
        apply.ToolTip = any ? "Writes the changes (only now does the hardware change)" : "Nothing to apply";
    }

    private string CompareText(Act a, View v)
    {
        if (a.Dev == ThermalDevice.Gpu) return v.Flat ? "Fixed by Marz; the driver decides again on A (automatic)." : "Driver automatic.";
        if (!edits.ContainsKey(a.Id)) return v.Editable ? "No changes from the saved curve." : "Active mode's curve (read only).";
        if (v.Cur.Fixed || v.Saved.Fixed) return "Apply switches the actuator's mode.";
        double? lo = null, eq = null;
        for (double t = v.Axis.Min; t <= v.Axis.Max; t++)
        {
            double x = CurveEdit.Interpolate(v.Cur.Points, t), y = CurveEdit.Interpolate(v.Saved.Points, t);
            if (x < y - 0.5) lo = t;
            if (Math.Abs(x - y) < 0.5 && lo != null && eq == null && t > lo) eq = t;
        }
        bool higher = Enumerable.Range((int)v.Axis.Min, (int)(v.Axis.Max - v.Axis.Min) + 1).Any(t => CurveEdit.Interpolate(v.Cur.Points, t) > CurveEdit.Interpolate(v.Saved.Points, t) + 0.5);
        return lo == null ? "Same as or stronger than the previous one across the whole range."
            : $"Quieter up to {lo:0} °C" + (eq != null ? $"; same as the previous one from {eq:0} °C on." : ".") + (higher ? " Stronger in another range." : "");
    }

    private void BuildPoints(Act a, View v)
    {
        pointsRow.Children.Clear();
        var pts = v.Cur.Points;
        if (a.Dev == ThermalDevice.Gpu || pts.Count == 0)
        {
            pointsRow.Children.Add(Ui.T(a.Dev == ThermalDevice.Gpu ? "No points: the driver does not expose the curve. Marz only sets a fixed % via the mode above." : "No curve to show.", 11, Theme.Muted));
            return;
        }
        if (v.Cur.Fixed || v.Flat)
        {
            pointsRow.Children.Add(PointCard("FIXED", $"{pts[0].Percent:0}", "%", a.Dev == ThermalDevice.Cooler ? "no source" : "all temps", true, null));
            pointsRow.Children.Add(Ui.T(v.Editable ? "drag the line on the chart" : "", 10.5, Theme.Muted).With(t => { SetDock(t, Dock.Right); t.VerticalAlignment = VerticalAlignment.Center; }));
            return;
        }
        for (int i = 0; i < pts.Count; i++)
        {
            int idx = i;
            pointsRow.Children.Add(PointCard($"P{i + 1}", $"{pts[i].TempC:0}", "°C", $"{pts[i].Percent:0}%", v.Editable && i == selPoint, v.Editable ? () => { selPoint = idx; chart.Select(idx); chart.Focus(); pointsKey = ""; RefreshEdit(); } : null));
        }
        if (v.Editable && !v.Limits.FixedCount)
        {
            if (pts.Count < v.Limits.MaxPoints)
                pointsRow.Children.Add(Chip("+ POINT", false, 11).With(c => { c.Padding = new Thickness(10, 9, 10, 9); c.Cursor = Cursors.Hand; c.VerticalAlignment = VerticalAlignment.Stretch; c.ToolTip = "New point in the middle of the segment after the selected one (or double-click the chart)";
                    c.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (CurveEdit.AddAfter(pts, selPoint, v.Limits) is { } add) { selPoint = add.Index; SetEdit(v, new Edit(add.Points, false)); } }; }));
            if (pts.Count > v.Limits.MinPoints)
                pointsRow.Children.Add(Chip("− REMOVE", false, 11).With(c => { c.Padding = new Thickness(10, 9, 10, 9); c.Margin = new Thickness(4, 0, 0, 0); c.Cursor = Cursors.Hand; c.ToolTip = "Removes the selected point (Del)";
                    c.MouseLeftButtonDown += (_, e) => { e.Handled = true; if (CurveEdit.Remove(pts, selPoint, v.Limits) is { } r) { selPoint = Math.Min(selPoint, r.Count - 1); SetEdit(v, new Edit(r, false)); } }; }));
        }
        if (v.Editable && a.Dev == ThermalDevice.Cooler)
        {
            var (_, fan) = Setting(cooler!.FanConfig, a.Id);
            var def = CurveEdit.Normalize(RyujinCurves.CurveFor(fan, RyujinCurves.Preset.Standard).Select(n => new CurvePoint(n.TempC, n.Percent)), v.Limits);
            pointsRow.Children.Add(Chip("↺ RESTORE", false, 11).With(c => { c.Padding = new Thickness(10, 9, 10, 9); c.Margin = new Thickness(4, 0, 0, 0); c.Cursor = Cursors.Hand;
                c.ToolTip = "Resets this curve to the default (STD points). Only written on Apply.";
                c.MouseLeftButtonDown += (_, e) => { e.Handled = true; selPoint = 0; SetEdit(v, new Edit(def, false)); }; }));
        }
        var cap = Ui.T($"{pts.Count}/{v.Limits.MaxPoints} points" + (v.Limits.FixedCount ? "\nboard chip limit" : ""), 10.5, Theme.Muted).With(t => { t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.None; });
        SetDock(cap, Dock.Right); pointsRow.Children.Add(cap);
    }

    private sealed record FanNow(string Name, int? Rpm, bool Off);

    private List<FanNow> FansOf(Act a)
    {
        var snap = Snap();
        IEnumerable<StripItem> items = a.Dev switch
        {
            ThermalDevice.Gpu => ThermalMap.GpuStrip(gpu!.State.Reading!.Fans.Select(f => (f.SpeedPercent, f.SpeedRpm))),
            ThermalDevice.Board => ThermalMap.BoardStrip([ThermalMap.BoardFan(a.Id)!], Nick, snap?.MbFanRpm ?? new Dictionary<int, float>(), snap?.MbFanControl ?? new Dictionary<int, float>(), asus!.Off.IsOff),
            _ => ThermalMap.CoolerStrip(cooler!.Latest?.Pump?.PumpRpm, null, cooler.Latest?.Fans?.Rpm ?? [], null, cooler.Latest?.Pump?.EmbeddedFanRpm, null, CoolerNick).Where(i => i.ActuatorId == a.Id),
        };
        return items.Select(i => new FanNow(i.Name, i.Rpm, i.Off)).ToList();
    }

    private void Apply()
    {
        var acts = Acts();
        if (cooler != null && (pendingSource != null || edits.Keys.Any(k => ThermalMap.DeviceOf(k) == ThermalDevice.Cooler)))
        {
            var c = cooler.FanConfig;
            CoolerFanSetting Up(CoolerFanSetting s, string id) => !edits.TryGetValue(id, out var e) ? s
                : e.Fixed ? s with { Mode = CoolerMode.Fixed, FixedPercent = (int)e.Points[0].Percent }
                : s with { Mode = CoolerMode.Smart, Custom = e.Points.Select(p => new CurveNode(p.TempC, p.Percent)).ToList() };
            cooler.SaveFans(c with
            {
                Enabled = true, SourceId = pendingSource ?? c.SourceId,
                Pump = Up(c.Pump, ThermalMap.PumpId), Radiator = Up(c.Radiator, ThermalMap.RadiatorId), Embedded = Up(c.Embedded, ThermalMap.EmbeddedId),
            });
            cooler.ResumeControl();
        }
        if (asus != null)
        {
            // Check before writing: the first SaveUserCurve already marks the board as "applying".
            var board = edits.ToList().Where(k => ThermalMap.DeviceOf(k.Key) == ThermalDevice.Board && acts.FirstOrDefault(x => x.Id == k.Key) is { } a && Build(a).Editable).ToList();
            foreach (var (id, e) in board) asus.SaveUserCurve(ThermalMap.BoardFan(id)!, CurveEdit.ToChip(e.Points));
        }
        edits.Clear(); pendingSource = null; pointsKey = "";
        Refresh(force: true);
    }

    // ------------------------------------------------------------------ visual pieces

    private static Run Rn(string text, string brush, int weight = 600, double? size = null)
    {
        var r = new Run(text) { FontFamily = AppFonts.For(true, weight) };
        if (size is double s) r.FontSize = s;
        r.SetResourceReference(TextElement.ForegroundProperty, brush);
        return r;
    }

    private static string Fmt(double? v, string f, string unit) => v is double x ? x.ToString(f, CultureInfo.InvariantCulture) + unit : "—";

    private static Border Chip(string text, bool on, double size = 9.5)
    {
        var t = Ui.T(text, size, on ? Theme.Bg : Theme.Text, weight: 600).With(x => { x.TextTrimming = TextTrimming.None; x.VerticalAlignment = VerticalAlignment.Center; });
        var b = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0), Child = t, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        b.SetResourceReference(Border.BorderBrushProperty, on ? Theme.Fill : Theme.Line);
        if (on) b.SetResourceReference(Border.BackgroundProperty, Theme.Fill);
        return b;
    }

    private static Border Dot() { var d = new Border { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center }; d.SetResourceReference(Border.BackgroundProperty, Theme.UStroke); return d; }

    /// <summary>Mode button (board/cooler/GPU): like in the popup, a bit larger; FULL/100 lights up in color B.</summary>
    private static FlatButton ModeBtn(string label, bool on, bool full, string tip, bool enabled, Action click)
    {
        var t = Ui.T(label, 11, on ? Theme.Bg : Theme.Muted, weight: 600, tabular: true).With(x => { x.VerticalAlignment = VerticalAlignment.Center; x.TextTrimming = TextTrimming.None; });
        var b = new FlatButton(Theme.Muted) { Height = 22, MinWidth = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 2, 0), BorderThickness = new Thickness(1), ActiveBg = full ? Theme.UStroke : Theme.Fill, ToolTip = tip, IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 }.Content(t);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        b.Active = on; t.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.Bg : Theme.Muted);
        b.Click += () => { if (b.IsEnabled) click(); };
        return b;
    }

    private static FlatButton SmallButton(string text, string tip, Action click)
    {
        var b = new FlatButton(Theme.Text) { Height = 22, Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center }
            .Content(Ui.T(text, 10.5, weight: 600).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        b.Click += click;
        return b;
    }

    private static StackPanel Field(string label, FrameworkElement control) => new()
    {
        Children = { Ui.T(label, 10, Theme.Muted).With(t => t.Margin = new Thickness(0, 0, 0, 3)), control },
    };

    private static TextBlock Note(string text) => Ui.T(text, 11, Theme.Muted, head: false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 5, 0, 0); });

    private static Border Section(string title, string? right, UIElement content, bool last = false)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        if (right != null) head.Children.Add(Ui.T(right, 10, Theme.Muted).With(t => { SetDock(t, Dock.Right); t.VerticalAlignment = VerticalAlignment.Center; }));
        head.Children.Add(Ui.T(title, 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        return Ui.Box(new Thickness(0, 0, 0, last ? 0 : 1), new Thickness(14, 11, 14, 11), new StackPanel { Children = { head, content } });
    }

    private static DockPanel Rule(string icon, string text)
    {
        var (i, _) = Icons.Make(icon, 12, Theme.Muted);
        i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 2, 7, 0);
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        SetDock(i, Dock.Left); d.Children.Add(i);
        d.Children.Add(Ui.T(text, 11.5, head: false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; }));
        return d;
    }

    private static Border PointCard(string key, string temp, string unit, string pct, bool sel, Action? click)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
        var k = Ui.T(key, 10, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); });
        Grid.SetRowSpan(k, 2); g.Children.Add(k);
        var tv = new TextBlock { FontFamily = AppFonts.HeadSemiBold, FontSize = 14 };
        tv.Inlines.Add(Rn(temp, Theme.Text, 600, 14)); tv.Inlines.Add(Rn(unit, Theme.Muted, 400, 10));
        Grid.SetColumn(tv, 1); g.Children.Add(tv);
        var pv = Ui.T(pct, 14, weight: 600, tabular: true);
        Grid.SetColumn(pv, 1); Grid.SetRow(pv, 1); g.Children.Add(pv);
        var b = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), MinWidth = 76, Child = g, Background = Brushes.Transparent };
        b.SetResourceReference(Border.BorderBrushProperty, sel ? Theme.DStroke : Theme.Line);
        if (sel) b.SetResourceReference(Border.BackgroundProperty, Theme.Hover);
        if (click != null) { b.Cursor = Cursors.Hand; b.MouseLeftButtonDown += (_, e) => { e.Handled = true; click(); }; }
        SetDock(b, Dock.Left);
        return b;
    }

    private static Border Cmp(string label, string value, string small)
    {
        var v = new TextBlock { FontFamily = AppFonts.HeadSemiBold, FontSize = 16 };
        v.Inlines.Add(Rn(value, Theme.Text, 600, 16));
        if (small.Length > 0) v.Inlines.Add(Rn(" " + small, Theme.Muted, 400, 11));
        return Ui.Box(new Thickness(0, 0, 1, 0), new Thickness(12, 5, 12, 5), new StackPanel { Children = { Ui.T(label, 10, Theme.Muted), v } });
    }

    private static FrameworkElement Swatch(string kind, string text)
    {
        FrameworkElement s = kind switch
        {
            "line" => new Rectangle { Width = 16, Height = 2 }.With(r => r.SetResourceReference(Shape.FillProperty, Theme.DStroke)),
            "dash" => new Line { X1 = 0, X2 = 16, Y1 = 1, Y2 = 1, StrokeThickness = 1.5, StrokeDashArray = [2, 2], Width = 16, Height = 2 }.With(r => r.SetResourceReference(Shape.StrokeProperty, Theme.Muted)),
            "now" => new Rectangle { Width = 1, Height = 11 }.With(r => r.SetResourceReference(Shape.FillProperty, Theme.Text)),
            _ => new Border { Width = 12, Height = 10, Background = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, SpreadMethod = GradientSpreadMethod.Repeat, StartPoint = new Point(0, 0), EndPoint = new Point(4, 4),
                GradientStops = { new GradientStop(Color.FromArgb(110, 163, 163, 168), 0), new GradientStop(Color.FromArgb(110, 163, 163, 168), 0.25), new GradientStop(Colors.Transparent, 0.25), new GradientStop(Colors.Transparent, 1) } } },
        };
        s.VerticalAlignment = VerticalAlignment.Center; s.Margin = new Thickness(0, 0, 6, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), Children = { s, Ui.T(text, 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center) } };
    }
}
