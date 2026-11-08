using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Theming;
using MonitorTray.Core.Detail;
using MonitorTray.Core.Format;
using MonitorTray.Core.Model;
using MonitorTray.Gpu;
using MonitorTray.Sensors;

namespace MonitorTray.App.Views;

/// <summary>Content of the Details window (docs/04 §C). Rebuilds only what is needed on each tick.</summary>
public sealed class DetailsContent : DockPanel
{
    private readonly AppState app;
    private readonly SensorService service;
    public Border TitleBar { get; }
    private readonly TextBlock sub, footerLeft, footerRight;
    private readonly TextBox search;
    private readonly TextBlock placeholder;
    private readonly FlatButton chipZero, chipLimits;
    public FlatButton MaxButton { get; }
    public FlatButton CloseButton { get; }
    private readonly StackPanel list;
    private readonly ScrollViewer listScroll, panelScroll;
    private readonly StackPanel panel;
    private Filters filters = Filters.Default;
    private string? selectedId;
    private Snapshot? last;
    private string panelKey = "";
    private readonly Dictionary<string, DeviceRow> rows = new();
    private PanelView? view;
    public event Action? Changed;

    private readonly GpuFanService? fans;
    private FanPanel? fanPanel;

    public DetailsContent(AppState app, SensorService service, GpuFanService? fans)
    {
        this.app = app; this.service = service; this.fans = fans;
        if (fans != null) fans.Changed += s => Dispatcher.BeginInvoke(() => fanPanel?.Update(s));
        LastChildFill = true;
        // ---- C1 title bar
        var bar = new DockPanel { LastChildFill = true };
        TitleBar = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(12, 6, 6, 6), bar);
        TitleBar.Background = Brushes.Transparent;
        DockPanel.SetDock(TitleBar, Dock.Top);
        var leftTitle = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftTitle.Children.Add(Ui.Glyph(7).With(g => { g.Margin = new Thickness(0, 0, 8, 0); g.VerticalAlignment = VerticalAlignment.Center; }));
        leftTitle.Children.Add(Ui.T("SYSTEM DETAILS", 13, weight: 600).With(t => { t.Margin = new Thickness(0, 0, 8, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        DockPanel.SetDock(leftTitle, Dock.Left);
        bar.Children.Add(leftTitle);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        // search
        var (sIcon, _) = Icons.Make(Icons.Search, 12, Theme.Muted);
        sIcon.Margin = new Thickness(0, 0, 6, 0);
        search = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = AppFonts.Body, FontSize = 12, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null };
        search.SetResourceReference(TextBox.ForegroundProperty, Theme.Text);
        search.SetResourceReference(TextBox.CaretBrushProperty, Theme.Text);
        search.SetResourceReference(TextBox.SelectionBrushProperty, Theme.Fill);
        placeholder = Ui.T("Filter sensors", 12, Theme.Muted, head: false).With(t => { t.IsHitTestVisible = false; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(2, 0, 0, 0); });
        search.TextChanged += (_, _) => { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; filters = filters with { Query = search.Text }; Rebuild(true); };
        var sg = new Grid { Children = { placeholder, search } };
        var sd = new DockPanel { LastChildFill = true, Children = { sIcon, sg } };
        DockPanel.SetDock(sIcon, Dock.Left);
        var searchBox = Ui.Box(new Thickness(1), new Thickness(7, 0, 7, 0), sd).With(b => { b.Width = 220; b.Height = 24; b.Margin = new Thickness(0, 0, 8, 0); b.Cursor = Cursors.IBeam; });
        searchBox.MouseLeftButtonDown += (_, _) => search.Focus();
        right.Children.Add(searchBox);
        chipZero = Chip("ZEROS", "Show sensors that are always zero", () => filters = filters with { ShowZero = !filters.ShowZero });
        chipLimits = Chip("LIMITS", "Show limits and SPD metadata", () => filters = filters with { ShowLimits = !filters.ShowLimits });
        chipLimits.Margin = new Thickness(8, 0, 8, 0);
        right.Children.Add(chipZero); right.Children.Add(chipLimits);
        MaxButton = FlatButton.Icon(Icons.Maximize, 24, 13, "Maximize");
        CloseButton = FlatButton.Icon(Icons.X, 24, 12, "Close");
        right.Children.Add(MaxButton); right.Children.Add(CloseButton);
        bar.Children.Add(right);
        sub = Ui.T("Loading sensors…", 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        bar.Children.Add(sub);
        Children.Add(TitleBar);

        // ---- C3 footer
        var fd = new DockPanel();
        footerLeft = Ui.T("", 10, Theme.Muted, tabular: true);
        footerRight = Ui.T("", 10, Theme.Muted, tabular: true);
        DockPanel.SetDock(footerRight, Dock.Right);
        fd.Children.Add(footerRight); fd.Children.Add(footerLeft);
        var footer = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(12, 4, 12, 4), fd);
        DockPanel.SetDock(footer, Dock.Bottom);
        Children.Add(footer);

        // ---- C2 body
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        list = new StackPanel();
        listScroll = Scroll(list);
        var listBox = Ui.Box(new Thickness(0, 0, 1, 0), new Thickness(0), listScroll);
        body.Children.Add(listBox);
        panel = new StackPanel { Margin = new Thickness(16, 14, 16, 18) };
        panelScroll = Scroll(panel);
        Grid.SetColumn(panelScroll, 1);
        body.Children.Add(panelScroll);
        Children.Add(body);

        Focusable = true;
        app.Changed += (_, s) => footerRight.Text = "refreshes every " + Formatter.RefreshLabel(s.RefreshMs);
        footerRight.Text = "refreshes every " + Formatter.RefreshLabel(app.Settings.RefreshMs);
    }

    /// <summary>Used by --preview: selection and filters without interaction.</summary>
    internal void PreviewSet(string? id, Filters f)
    {
        selectedId = id; filters = f; search.Text = f.Query;
        filters = f;
        void Chip(FlatButton b, bool on) { b.Active = on; if (b.Child is TextBlock t) t.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.Bg : Theme.Muted); }
        Chip(chipZero, f.ShowZero); Chip(chipLimits, f.ShowLimits);
    }

    public void FocusSearch() { search.Focus(); search.SelectAll(); }

    /// <summary>
    /// Opens on the sensor (click on a popup temperature card): selects its device and filters by name.
    /// Without the sensor (GPU via NVML, LHM with no reading yet) it selects the first device of the category.
    /// </summary>
    public void FocusSensor(string? sensorId, string category)
    {
        var s = last ?? service.Latest;
        if (s == null) return;
        last = s;
        var hit = sensorId == null ? default : s.Devices.SelectMany(d => d.Sensors.Where(x => x.Id == sensorId).Select(x => (Device: d, Sensor: x))).FirstOrDefault();
        var dev = hit.Device ?? s.Devices.FirstOrDefault(d => d.Category == category);
        if (dev == null) return;
        ResetFilters();
        selectedId = dev.Id; panelKey = "";
        if (hit.Sensor != null) search.Text = hit.Sensor.Name; // TextChanged applies the filter and rebuilds
        Rebuild(true);
        panelScroll.ScrollToTop();
    }

    private static ScrollViewer Scroll(UIElement c)
    {
        var s = new ScrollViewer { Content = c, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false };
        return s;
    }

    private FlatButton Chip(string text, string tip, Action toggle)
    {
        var t = Ui.T(text, 11, Theme.Muted).With(x => x.VerticalAlignment = VerticalAlignment.Center);
        var b = new FlatButton { Height = 24, Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1), ToolTip = tip, ActiveBg = Theme.Fill }.Content(t);
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        b.Click += () =>
        {
            toggle();
            b.Active = !b.Active;
            t.SetResourceReference(TextBlock.ForegroundProperty, b.Active ? Theme.Bg : Theme.Muted);
            Rebuild(true);
        };
        return b;
    }

    public void ResetFilters()
    {
        filters = Filters.Default; search.Text = "";
        foreach (var c in new[] { chipZero, chipLimits }) { c.Active = false; if (c.Child is TextBlock t) t.SetResourceReference(TextBlock.ForegroundProperty, Theme.Muted); }
    }

    public void Update(Snapshot s) { last = s; Rebuild(false); }

    private void Rebuild(bool structural)
    {
        var s = last;
        if (s == null) return;
        if (s.Devices.Count == 0)
        {
            sub.Text = s.Status.HardwareReady ? "0 devices · 0 entries" : "Loading sensors…";
            return;
        }
        sub.Text = $"{s.Devices.Count} devices · {s.EntryCount} entries";
        var v = service.WithTracker(t => DetailModel.Build(s.Devices, t, filters, selectedId, app.Settings.CardPicks));
        footerLeft.Text = DetailModel.Footer(v);

        // Device list
        var ids = v.Devices.Select(d => d.Device.Id).ToList();
        if (structural || !ids.SequenceEqual(list.Children.OfType<DeviceRow>().Select(r => r.Id)))
        {
            list.Children.Clear();
            foreach (var d in v.Devices)
            {
                if (!rows.TryGetValue(d.Device.Id, out var row))
                {
                    row = new DeviceRow(d.Device.Id);
                    row.Click += () => { selectedId = row.Id; panelKey = ""; Rebuild(false); panelScroll.ScrollToTop(); };
                    rows[d.Device.Id] = row;
                }
                list.Children.Add(row);
            }
            if (v.Devices.Count == 0)
                list.Children.Add(Ui.T("No sensor matches the filter.", 12, Theme.Muted, head: false).With(t => { t.Margin = new Thickness(12, 16, 12, 16); t.TextWrapping = TextWrapping.Wrap; }));
        }
        var selId = v.Panel?.Device.Id;
        foreach (var d in v.Devices) rows[d.Device.Id].Set(d, d.Device.Id == selId);

        // Panel
        if (v.Panel == null)
        {
            panel.Children.Clear(); view = null; panelKey = "";
            if (v.Devices.Count > 0) panel.Children.Add(EmptyFilter());
            return;
        }
        // STRUCTURE-only key (which sections/sensors/cards exist). Values, the card's main sensor and
        // visual type change every tick and are updated on the existing elements, without recreating the panel.
        var key = selId + "|" + string.Join(",", v.Panel.Sections.Select(x => x.KindRaw + ":" + x.IsGrid + ":" + (x.IsGrid ? x.Cells.Count : x.Tiles.Count) + ":" + string.Join(";", x.Tiles.Select(t => t.Sensor.Id))))
                  + "|" + string.Join(",", v.Panel.Cards.Select(c => c.Label)) + (s.Status.IsAdmin || s.Status.Simulated ? "" : "|admin");
        if (key != panelKey || view == null)
        {
            panelKey = key;
            panel.Children.Clear();
            if (!s.Status.IsAdmin && !s.Status.Simulated) panel.Children.Add(AdminBanner());
            view = new PanelView(v.Panel, PickCard);
            panel.Children.Add(view);
            if (fans != null && v.Panel.Device.Family == "GpuNvidia")
            {
                fanPanel ??= new FanPanel(fans);
                (fanPanel.Parent as Panel)?.Children.Remove(fanPanel);
                view.InsertFans(fanPanel);
                fanPanel.Update(fans.State);
            }
            if (v.Panel.VisibleCount == 0) panel.Children.Add(EmptyFilter());
        }
        view.Update(v.Panel);
        Changed?.Invoke();
    }

    /// <summary>Card choice (Average/Max/core): saved to settings.json and redrawn immediately.</summary>
    private void PickCard(string key, string value)
    {
        var d = new Dictionary<string, string>(app.Settings.CardPicks) { [key] = value };
        app.Update(s => s with { CardPicks = d });
        Rebuild(false);
    }

    private static TextBlock EmptyFilter() => Ui.T("No visible readings with the current filters.", 12, Theme.Muted, head: false).With(t => t.Margin = new Thickness(0, 24, 0, 24));

    private static Border AdminBanner()
    {
        var text = Ui.T("Some readings (CPU temperature and power, motherboard, RAM SPD) require running as administrator.", 12, head: false);
        text.TextWrapping = TextWrapping.Wrap; text.TextTrimming = TextTrimming.None; text.VerticalAlignment = VerticalAlignment.Center;
        var btnText = Ui.T("Restart as admin", 12, head: false);
        var btn = new FlatButton(Theme.Text) { Padding = new Thickness(8, 3, 8, 3), BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0) }.Content(btnText);
        btn.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        btn.Click += () => { if (Platform.Autostart.RestartAsAdmin("--details --restarted")) Application.Current.Shutdown(); };
        var d = new DockPanel();
        DockPanel.SetDock(btn, Dock.Right);
        d.Children.Add(btn); d.Children.Add(text);
        return Ui.Box(new Thickness(1), new Thickness(10, 6, 10, 6), d).With(b => b.Margin = new Thickness(0, 0, 0, 12));
    }

    /// <summary>Device list item.</summary>
    private sealed class DeviceRow : Border
    {
        public string Id { get; }
        private readonly TextBlock cat, headline, name; private readonly Border isNew; private readonly AreaChart spark;
        private bool selected, hover;
        public event Action? Click;
        public DeviceRow(string id)
        {
            Id = id;
            BorderThickness = new Thickness(0, 0, 0, 1); Padding = new Thickness(12, 8, 10, 6); Cursor = Cursors.Hand;
            SetResourceReference(BorderBrushProperty, Theme.Line);
            var top = new DockPanel();
            cat = Ui.T("", 10, Theme.Muted);
            headline = Ui.T("", 13, weight: 600, tabular: true);
            isNew = new Border { Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(6, 0, 6, 0), Child = Ui.T("NEW", 10).With(t => t.Foreground = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11))), VerticalAlignment = VerticalAlignment.Center };
            isNew.SetResourceReference(BackgroundProperty, Theme.PB);
            DockPanel.SetDock(headline, Dock.Right); DockPanel.SetDock(isNew, Dock.Right);
            top.Children.Add(headline); top.Children.Add(isNew); top.Children.Add(cat);
            cat.VerticalAlignment = VerticalAlignment.Center;
            name = Ui.T("", 12, head: false, weight: 500).With(t => t.Margin = new Thickness(0, 3, 0, 0));
            spark = new AreaChart { Height = 18, Margin = new Thickness(0, 3, 0, 0), Slots = 40 };
            Child = new StackPanel { Children = { top, name, spark } };
            Background = Brushes.Transparent;
            MouseEnter += (_, _) => { hover = true; Paint(); };
            MouseLeave += (_, _) => { hover = false; Paint(); };
            MouseLeftButtonUp += (_, _) => Click?.Invoke();
        }
        public void Set(DeviceListItem d, bool sel)
        {
            cat.Text = d.Device.Category.ToUpperInvariant();
            headline.Text = d.Headline; name.Text = d.Device.DisplayName; name.ToolTip = d.Device.DisplayName;
            isNew.Visibility = d.Device.IsNew ? Visibility.Visible : Visibility.Collapsed;
            spark.Visibility = d.Spark.Length > 1 && d.Spark.Any(x => x != null) ? Visibility.Visible : Visibility.Collapsed;
            spark.Set(d.Spark, null, false);
            selected = sel; Paint();
        }
        private void Paint()
        {
            if (selected) SetResourceReference(BackgroundProperty, Theme.Sel);
            else if (hover) SetResourceReference(BackgroundProperty, Theme.Hover);
            else Background = Brushes.Transparent;
        }
    }

    /// <summary>Header + cards + sections of a device. Elements are reused across ticks.</summary>
    private sealed class PanelView : StackPanel
    {
        private readonly TextBlock cat, name, meta;
        private readonly List<CardView> cards = [];
        private readonly List<SectionView> sections = [];
        public PanelView(DevicePanel p, Action<string, string> pick)
        {
            var head = new WrapPanel { Orientation = Orientation.Horizontal };
            cat = Ui.T("", 11, Theme.Muted).With(t => { t.Margin = new Thickness(0, 0, 10, 4); t.VerticalAlignment = VerticalAlignment.Bottom; });
            name = Ui.T("", 26, weight: 600).With(t => { t.Margin = new Thickness(0, 0, 10, 0); t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; });
            meta = Ui.T("", 11, Theme.Muted).With(t => { t.Margin = new Thickness(0, 0, 0, 4); t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; t.TextWrapping = TextWrapping.Wrap; });
            head.Children.Add(cat); head.Children.Add(name); head.Children.Add(meta);
            Children.Add(head);
            if (p.Cards.Count > 0)
            {
                var g = new FillGrid { MinItemWidth = 200, Gap = 8, Margin = new Thickness(0, 12, 0, 0) };
                foreach (var c in p.Cards) { var cv = new CardView(c.Alt, pick); cards.Add(cv); g.Children.Add(cv); }
                Children.Add(g);
            }
            foreach (var s in p.Sections) { var sv = new SectionView(s); sections.Add(sv); Children.Add(sv); }
        }
        /// <summary>Fans right below the device header, before the cards.</summary>
        public void InsertFans(UIElement fans) => Children.Insert(1, fans);

        public void Update(DevicePanel p)
        {
            cat.Text = p.Device.Category.ToUpperInvariant(); name.Text = p.Device.DisplayName; meta.Text = p.Meta;
            for (int i = 0; i < cards.Count && i < p.Cards.Count; i++) cards[i].Set(p.Cards[i]);
            for (int i = 0; i < sections.Count && i < p.Sections.Count; i++) sections[i].Set(p.Sections[i]);
        }
    }

    private sealed class CardView : Border
    {
        private readonly TextBlock label, sensor, value, unit, lo, hi;
        private readonly Gauge gauge; private readonly AreaChart chart;
        private readonly Action<string, string> pick;
        private CardModel? model;
        public CardView(bool alt, Action<string, string> pick)
        {
            this.pick = pick;
            BorderThickness = new Thickness(1); Padding = new Thickness(10, 8, 10, 6);
            SetResourceReference(BorderBrushProperty, Theme.Line);
            var top = new DockPanel();
            label = Ui.T("", 11, Theme.Muted).With(t => t.Margin = new Thickness(0, 0, 6, 0));
            sensor = Ui.T("", 11, Theme.Muted).With(t => t.TextAlignment = TextAlignment.Right);
            DockPanel.SetDock(label, Dock.Left);
            // The sensor name is a selector when there are options (e.g. Clock: Average, Max, P-Core #1...).
            chevron = Ui.T("▾", 10, Theme.Muted).With(t => { t.Margin = new Thickness(3, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; });
            var sel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { sensor, chevron } };
            selector = new FlatButton { Padding = new Thickness(4, 0, 2, 0), Margin = new Thickness(0, -2, -4, -2), HorizontalAlignment = HorizontalAlignment.Right }.Content(sel);
            selector.Click += OpenMenu;
            top.Children.Add(label); top.Children.Add(selector);
            var mid = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            gauge = new Gauge { VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(gauge, Dock.Right);
            value = Ui.T("", 30, weight: 600, tabular: true).With(t => { t.LineHeight = 30; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.TextTrimming = TextTrimming.None; });
            unit = Ui.T("", 13, Theme.Muted).With(t => { t.Margin = new Thickness(4, 0, 0, 2); t.VerticalAlignment = VerticalAlignment.Bottom; });
            mid.Children.Add(gauge);
            mid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { value, unit } });
            mid.MinHeight = 38;
            chart = new AreaChart { Height = 58, Margin = new Thickness(0, 4, 0, 0), BottomLine = true, Slots = 119, Thickness = 1.5, StrokeKey = alt ? Theme.UStroke : Theme.DStroke, FillKey = alt ? Theme.UFill : Theme.DFill };
            gauge.ColorKey = alt ? Theme.UStroke : Theme.DStroke;
            var foot = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            lo = Ui.T("", 10, Theme.Muted, tabular: true); hi = Ui.T("", 10, Theme.Muted, tabular: true);
            DockPanel.SetDock(hi, Dock.Right);
            foot.Children.Add(hi); foot.Children.Add(lo);
            Child = new StackPanel { Children = { top, mid, chart, foot } };
            altKey = alt ? Theme.UStroke : Theme.DStroke;
        }
        private readonly string altKey;
        private AnimatedValue? anim;
        private readonly FlatButton selector = null!;
        private readonly TextBlock chevron = null!;

        private void OpenMenu()
        {
            if (model == null || model.Options.Count < 2) return;
            var m = new ContextMenu { PlacementTarget = selector, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 420 };
            foreach (var o in model.Options)
            {
                var key = o.Key; var pk = model.PickKey;
                var item = new MenuItem { Header = o.Label, IsCheckable = true, IsChecked = o.Key == model.Selected };
                item.Click += (_, _) => pick(pk, key);
                m.Items.Add(item);
                if (o.Key == DetailModel.PickMax) m.Items.Add(new Separator());
            }
            m.IsOpen = true;
        }
        public void Set(CardModel c)
        {
            model = c;
            label.Text = c.Label.ToUpperInvariant(); sensor.Text = c.SensorName;
            bool choosable = c.Options.Count > 1;
            selector.IsHitTestVisible = choosable; chevron.Visibility = choosable ? Visibility.Visible : Visibility.Collapsed;
            selector.ToolTip = choosable ? "Choose what this card shows (saved)" : c.SensorName;
            anim ??= new AnimatedValue(value, unit);
            anim.Set(c.Raw, c.Unit);
            gauge.Visibility = c.IsGauge ? Visibility.Visible : Visibility.Collapsed;
            gauge.SetFraction(c.Fraction, false); gauge.ColorKey = altKey; gauge.InvalidateVisual();
            chart.Set(c.History, c.Fixed, c.ZeroBase);
            lo.Text = c.Lo; hi.Text = c.Hi;
        }
    }

    /// <summary>Spreads the delays over up to ~300 ms, with a little jitter so it does not look like a ruler.</summary>
    internal static int Stagger(int i, int n) => n <= 1 ? 0 : (int)(i * 300.0 / n + (i * 37 % 11) * 4);

    private sealed class SectionView : StackPanel
    {
        private readonly TextBlock meta;
        private readonly List<Cell> cells = [];
        private readonly List<TileView> tiles = [];
        public SectionView(SectionModel s)
        {
            var head = new DockPanel();
            var title = Ui.T(s.Label.ToUpperInvariant(), 13, weight: 600);
            meta = Ui.T("", 11, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Bottom);
            DockPanel.SetDock(meta, Dock.Right);
            head.Children.Add(meta); head.Children.Add(title);
            Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0, 0, 0, 4), head).With(b => b.Margin = new Thickness(0, 18, 0, 0)));
            if (s.IsGrid)
            {
                var g = new FillGrid { MinItemWidth = 36, Gap = 3, Margin = new Thickness(0, 8, 0, 0) };
                // Per-cell delay: each core moves on its own time (a wave from left to right).
                for (int i = 0; i < s.Cells.Count; i++) { var c = new Cell { StaggerMs = Stagger(i, s.Cells.Count) }; cells.Add(c); g.Children.Add(c); }
                Children.Add(g);
            }
            else
            {
                var g = new FillGrid { MinItemWidth = 210, Gap = 6, Margin = new Thickness(0, 8, 0, 0) };
                for (int i = 0; i < s.Tiles.Count; i++) { var tv = new TileView(s.Tiles[i].Visual, Stagger(i, s.Tiles.Count)); tiles.Add(tv); g.Children.Add(tv); }
                Children.Add(g);
            }
        }
        public void Set(SectionModel s)
        {
            meta.Text = s.Meta;
            for (int i = 0; i < cells.Count && i < s.Cells.Count; i++)
            {
                var c = s.Cells[i];
                cells[i].SetFraction(c.Fraction, c.Emphasis); cells[i].SetValue(c.Raw, c.Unit, c.Text);
                if (!Equals(cells[i].ToolTip, c.Tooltip)) cells[i].ToolTip = c.Tooltip;
            }
            for (int i = 0; i < tiles.Count && i < s.Tiles.Count; i++) tiles[i].Set(s.Tiles[i]);
        }
    }

    private sealed class TileView : Border
    {
        private readonly TextBlock name, value, unit, range; private readonly Border tag; private readonly TextBlock tagText;
        private readonly Grid visualHost = new();
        private Bar? bar; private AreaChart? spark;
        private TileVisual? visual;
        private readonly AnimatedValue anim;
        private readonly int stagger;
        public TileView(TileVisual visual, int stagger)
        {
            this.stagger = stagger;
            BorderThickness = new Thickness(1); Padding = new Thickness(8, 6, 8, 6);
            SetResourceReference(BorderBrushProperty, Theme.Line);
            var top = new DockPanel();
            name = Ui.T("", 12, head: false);
            tagText = Ui.T("", 9, Theme.Muted);
            tag = Ui.Box(new Thickness(1), new Thickness(3, 0, 3, 0), tagText).With(b => { b.Margin = new Thickness(6, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center; });
            DockPanel.SetDock(tag, Dock.Right);
            top.Children.Add(tag); top.Children.Add(name);
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            value = Ui.T("", 17, weight: 600, tabular: true).With(t => t.TextTrimming = TextTrimming.None);
            unit = Ui.T("", 11, Theme.Muted).With(t => { t.Margin = new Thickness(3, 0, 0, 2); t.VerticalAlignment = VerticalAlignment.Bottom; });
            var vp = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 74, Margin = new Thickness(0, 0, 8, 0), Children = { value, unit } };
            DockPanel.SetDock(vp, Dock.Left);
            row.Children.Add(vp);
            range = Ui.T("", 11, Theme.Muted, tabular: true).With(t => { t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
            visualHost.MinHeight = 24;
            row.Children.Add(visualHost);
            Child = new StackPanel { Children = { top, row } };
            anim = new AnimatedValue(value, unit) { StaggerMs = stagger };
            SetVisual(visual);
        }

        /// <summary>Swaps the visual inside the same tile (e.g. a sensor that started to vary) without recreating the tile.</summary>
        private void SetVisual(TileVisual v)
        {
            if (visual == v) return;
            visual = v;
            visualHost.Children.Clear();
            if (v == TileVisual.Bar) { bar ??= new Bar { Height = 6, VerticalAlignment = VerticalAlignment.Center, StaggerMs = stagger }; visualHost.Children.Add(bar); }
            else if (v == TileVisual.Spark) { spark ??= new AreaChart { Height = 24, StrokeKey = Theme.UStroke, FillKey = Theme.UFill, Slots = 60 }; visualHost.Children.Add(spark); }
            else visualHost.Children.Add(range);
        }

        public void Set(TileModel t)
        {
            SetVisual(t.Visual);
            name.Text = t.Name; if (!Equals(ToolTip, t.Tooltip)) ToolTip = t.Tooltip;
            tagText.Text = t.Tag.ToUpperInvariant();
            tag.Visibility = t.Tag.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            anim.Set(t.Sensor.Value, t.Sensor.Unit);
            if (t.Visual == TileVisual.Bar) bar!.SetFraction(t.Fraction, t.Emphasis);
            if (t.Visual == TileVisual.Spark) spark!.Set(t.Spark, null, t.ZeroBase);
            range.Text = t.Range;
        }
    }
}
