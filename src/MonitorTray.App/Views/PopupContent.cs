using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Theming;
using MonitorTray.Core.Fans;
using MonitorTray.Core.Format;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Gpu;
using MonitorTray.Oled;
using RyujinService = MonitorTray.Ryujin.RyujinService;
using CoolerConfig = MonitorTray.Ryujin.CoolerConfig;
using RyujinCurves = MonitorTray.Ryujin.RyujinCurves;
using CoolerFanSetting = MonitorTray.Ryujin.CoolerFanSetting;
using CoolerFanMode = MonitorTray.Ryujin.FanMode;

namespace MonitorTray.App.Views;

/// <summary>Popup content (344 px): header + Summary or Settings (docs/04 §B).</summary>
public sealed class PopupContent : StackPanel
{
    private readonly AppState app;
    private readonly TextBlock title, refresh;
    private readonly FrameworkElement glyph;
    private readonly FlatButton back, details, gear, pin;
    private readonly FrameworkElement pinIcon;
    private readonly StackPanel main, settings;
    private readonly Metric cpu, gpu, mem;
    private readonly TextBlock rd, wr, down, downU, up, upU, scaleDown, scaleUp;
    private readonly MirrorChart chart;
    private readonly TextBlock paletteLabel;
    private readonly List<(string Id, Border Box)> swatches = [];
    private readonly Segmented segTheme, segRefresh, segHistory, segScale, segNetUnit;
    private readonly MiniSlider opacity; private readonly TextBlock opacityLabel;
    private readonly Check15 autostart, autohide;
    private readonly Border fanRow;
    private readonly TextBlock gpuName, fanSpeeds, fanBadge;
    private readonly System.Windows.Shapes.Path fanPath;
    private readonly UniformGrid tempsGrid;
    private readonly Border tempsBox;
    private readonly List<(TempCards.Def Def, TempCard Card)> tempCards = [];
    private string tempsLayout = "";
    private IReadOnlyDictionary<string, float> lhmTemps = new Dictionary<string, float>();
    private readonly RotateTransform fanSpin = new();
    private double spinSeconds;
    private readonly UniformGrid volumesPanel;
    private readonly Border volumesBox;
    private readonly TextBlock storageLabel;
    private readonly Dictionary<string, VolumeChip> volumeRows = new(StringComparer.OrdinalIgnoreCase);
    private GpuFanService? fanService;
    private readonly FlatButton game;
    private bool gameOn;
    private DateTime gameAt;
    private readonly AutoGame autoGame = new();
    private IReadOnlyList<string> lastRunning = [];
    /// <summary>Session min/max per role (App passes the same one that feeds the tray).</summary>
    public TempSession Session { get; set; } = new();
    private IReadOnlyDictionary<string, string> tempSources = new Dictionary<string, string>();
    /// <summary>Click on a temperature card: LHM sensor id (null without LHM) and the Details category to fall back to without it.</summary>
    public event Action<string?, string>? SensorRequested;
    private readonly Border strip;
    private readonly TextBlock stripText;

    /// <summary>Board fans included in game mode (Settings; default = all).</summary>
    private List<string> GameBoardFans() => ac == null ? [] : (app.Settings.GameBoardFans ?? ac.Fans).Where(ac.Fans.Contains).ToList();

    /// <summary>
    /// Game mode (configurable): GPU at the chosen %, board at FULL, cooler pump/radiator/embedded fan at a fixed %.
    /// Turning it off returns each one to what it was (GPU auto, board CURVES, cooler on the previous config).
    /// </summary>
    /// <summary>Toggles game mode from outside (single-instance "game" message: `MonitorTray.exe --game`).</summary>
    public void ToggleGameExternal() => ToggleGame();
    public bool GameOn => gameOn;

    /// <summary>Game mode state on disk (survives app restart/update): on + the cooler config from before.</summary>
    public static string GameStatePath => System.IO.Path.Combine(AppState.DataDir, "state", "game-mode.json");

    private void SaveGameState()
    {
        try
        {
            if (!gameOn) { System.IO.File.Delete(GameStatePath); return; }
            var o = new System.Text.Json.Nodes.JsonObject { ["on"] = true, ["since"] = DateTimeOffset.Now.ToString("O"), ["auto"] = autoGame.Owned };
            if (autoGame.Game != null) o["by"] = autoGame.Game;
            if (coolerBeforeGame != null) o["coolerBefore"] = coolerBeforeGame.ToJson();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(GameStatePath)!);
            MonitorTray.Core.AtomicFile.WriteAllText(GameStatePath, o.ToJsonString());
        }
        catch (Exception) { }
    }

    /// <summary>
    /// On startup: if game mode was on, it comes back on. Reapplies GPU and board (the GPU returns to automatic when the app closes)
    /// and recovers the cooler config from before, so the second click returns everything to normal.
    /// </summary>
    public void RestoreGame()
    {
        try
        {
            if (!System.IO.File.Exists(GameStatePath)) return;
            var o = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(GameStatePath)) as System.Text.Json.Nodes.JsonObject;
            if (o?["on"]?.GetValue<bool>() != true) return;
            if (o["coolerBefore"]?.GetValue<string>() is string cb) coolerBeforeGame = CoolerConfig.Parse(cb);
            autoGame.Restore(o["auto"]?.GetValue<bool>() == true, o["by"]?.GetValue<string>()); // turned on by the game: turns off when it closes
            gameOn = true; gameAt = DateTime.UtcNow;
            var s = app.Settings; var t = s.GameTargets;
            // Armoury Crate (COM) takes a while to come up with the app: the board goes to FULL when it becomes available.
            if (t.Contains("board")) { pendingBoardRestore = true; TryRestoreBoard(); }
            if (t.Contains("gpu") && fanService != null) _ = Task.Run(async () => { try { await Task.Delay(3000); await fanService.CommandAsync((uint)s.GameGpuPercent); } catch (Exception) { } });
            // The cooler is already saved at FULL/fixed % in ryujin-fans.json: the service reapplies it on its own.
            RenderCooler(); RenderGameIcon();
        }
        catch (Exception) { }
    }

    private bool pendingBoardRestore;

    // ---- Saved fan choices (FanChoices): reapplied on startup and when leaving game mode.
    private FanChoices.Data? pendingChoices;
    private bool restoreStartup;
    private IReadOnlyCollection<string>? pendingChoicesFans;
    private List<string> pendingOff = [];
    private DateTime pendingOffUntil;

    /// <summary>On app startup (outside game mode): board, OFF/MANUAL and GPU as you left them.</summary>
    public void RestoreChoices() { if (!gameOn) BeginRestoreChoices(gpuDelayMs: 3000, boardFans: null, gpu: true, startup: true); }

    /// <param name="boardFans">Fans that go back to the saved mode (null = the ones from Settings).</param>
    /// <param name="startup">On startup: nothing chosen = leave as is (the app already starts on automatic/curves).</param>
    private void BeginRestoreChoices(int gpuDelayMs, IReadOnlyCollection<string>? boardFans, bool gpu, bool startup = false)
    {
        var d = FanChoices.Load();
        restoreStartup = startup;
        pendingChoices = d; pendingChoicesFans = boardFans;
        TryRestoreChoices();
        if (gpu && fanService != null && !(startup && d.Gpu == null))
        {
            var g = d.Gpu;
            _ = Task.Run(async () => { try { if (gpuDelayMs > 0) await Task.Delay(gpuDelayMs); await fanService.CommandAsync(g); } catch (Exception) { } });
        }
    }

    /// <summary>Board: waits for Armoury Crate (COM) to become available. OFF waits for the first CPU reading (safety rule).</summary>
    private void TryRestoreChoices()
    {
        if (pendingChoices is not { } d || ac is not { Available: true }) return;
        pendingChoices = null;
        var fans = pendingChoicesFans ?? app.Settings.AcFans.Where(ac.Fans.Contains).ToList();
        if (fans.Count > 0 && !(restoreStartup && d.BoardMode is null or "curves")) ac.Apply(d.BoardMode ?? "curves", fans, remember: false);
        foreach (var (f, pct) in d.Manual) if (ac.Fans.Contains(f)) ac.SetManual(f, pct);
        pendingOff = d.Off.Where(f => ac.Fans.Contains(f) && !d.Manual.ContainsKey(f)).ToList();
        pendingOffUntil = DateTime.UtcNow.AddMinutes(2);
        RestoringOffChanged?.Invoke();
        TryRestoreOff();
    }

    /// <summary>Latest CPU/GPU temperature, also while the popup is hidden (called on every snapshot).</summary>
    public void SetTemps(float? cpu, float? gpu)
    {
        cpuTemp = cpu; gpuTemp = gpu;
        TryRestoreOff();
    }

    /// <summary>Saved OFF fans still waiting to be turned off (the app keeps reading sensors meanwhile).</summary>
    public bool RestoringOff => pendingOff.Count > 0;
    public event Action? RestoringOffChanged;

    private void TryRestoreOff()
    {
        if (pendingOff.Count == 0 || ac is not { Available: true } || !app.Settings.BoardFanOff) { if (!app.Settings.BoardFanOff) { pendingOff.Clear(); RestoringOffChanged?.Invoke(); } return; }
        if (cpuTemp == null && DateTime.UtcNow < pendingOffUntil) return; // without a CPU reading OFF is refused: wait
        foreach (var f in pendingOff) ac.TurnOff(f, cpuTemp, gpuTemp);
        { pendingOff.Clear(); RestoringOffChanged?.Invoke(); }
    }
    private bool shuttingDown;
    /// <summary>App exit: the GPU returns to automatic and the icon would go dark on its own; the saved game mode state must not be deleted.</summary>
    public void BeginShutdown() => shuttingDown = true;

    private void TryRestoreBoard()
    {
        if (!pendingBoardRestore || ac is not { Available: true }) return;
        var fans = GameBoardFans();
        if (fans.Count > 0) ac.Apply("full", fans, remember: false);
        pendingBoardRestore = false; gameAt = DateTime.UtcNow; // new grace period for FULL to confirm
    }

    /// <summary>Button click (or `--game`): the state now belongs to the user; automatic does not turn off what the user turned on.</summary>
    private void ToggleGame()
    {
        bool on = !gameOn;
        autoGame.ManualToggle(on, AutoGame.Match(lastRunning, app.Settings.GameExes) != null);
        SetGame(on);
    }

    /// <summary>Automatic game mode: App passes the running processes every ~3 s (only with games in the list).</summary>
    public void AutoGameStep(IReadOnlyList<string> running)
    {
        lastRunning = running;
        var act = autoGame.Step(running, app.Settings.GameExes, gameOn);
        if (act == AutoGameAction.TurnOn) SetGame(true);
        else if (act == AutoGameAction.TurnOff) SetGame(false);
        RenderStrip();
    }

    /// <summary>Automatic turned game mode on and will still turn it off (App keeps polling even with an empty list).</summary>
    public bool AutoGameOwned => autoGame.Owned;

    private void SetGame(bool on)
    {
        if (on == gameOn) return;
        gameOn = on; gameAt = DateTime.UtcNow; pendingBoardRestore = false;
        var s = app.Settings; var t = s.GameTargets;
        if (t.Contains("board") && ac is { Available: true })
        {
            var fans = GameBoardFans();
            if (fans.Count > 0 && gameOn) ac.Apply("full", fans, remember: false); // FULL also turns the stopped fans back on
        }
        if (gameOn && ac is { Available: true } && ac.Off.Any) ac.TurnOn(ac.Off.Off, "game mode", remember: false); // game turns all back on, even without the board in game mode
        if (t.Contains("gpu") && fanService != null && gameOn) _ = Task.Run(async () => { try { await fanService.CommandAsync((uint)s.GameGpuPercent); } catch (Exception) { } });
        if (!gameOn) BeginRestoreChoices(gpuDelayMs: 0, boardFans: t.Contains("board") ? GameBoardFans() : null, gpu: t.Contains("gpu")); // back to what you had chosen
        bool anyCooler = t.Contains("pump") || t.Contains("radiator") || t.Contains("embedded");
        if (anyCooler && cooler is { DeviceFound: true })
        {
            var c = cooler.FanConfig;
            if (gameOn)
            {
                coolerBeforeGame = c;
                // 100% = FULL preset (the WC RYUJIN II row lights FULL); 70/85% = fixed %.
                MonitorTray.Ryujin.CoolerFanSetting G(MonitorTray.Ryujin.CoolerFanSetting x) => s.GameCoolerPercent >= 100
                    ? x with { Mode = MonitorTray.Ryujin.FanMode.Smart, Preset = RyujinCurves.Preset.FullSpeed, Custom = [] }
                    : x with { Mode = MonitorTray.Ryujin.FanMode.Fixed, FixedPercent = s.GameCoolerPercent };
                cooler.SaveFans(c with
                {
                    Enabled = true,
                    Pump = t.Contains("pump") ? G(c.Pump) : c.Pump,
                    Radiator = t.Contains("radiator") ? G(c.Radiator) : c.Radiator,
                    Embedded = t.Contains("embedded") ? G(c.Embedded) : c.Embedded,
                });
                cooler.ResumeControl();
            }
            else if (coolerBeforeGame != null) { cooler.SaveFans(MonitorTray.Ryujin.CoolerModes.UndoGame(c, coolerBeforeGame, s.GameCoolerPercent) with { Enabled = coolerBeforeGame.Enabled }); coolerBeforeGame = null; }
            RenderCooler();
        }
        SaveGameState();
        RenderGameIcon();
        RenderStrip();
    }

    /// <summary>
    /// Thin top strip: protections that kicked in (fans turned back on, GPU/cooler at 100%) and active game mode.
    /// Hidden when there is nothing. A click dismisses the fans-turned-back-on notice.
    /// </summary>
    private void RenderStrip()
    {
        var s = app.Settings;
        var fs = fanService?.State;
        var cs = cooler?.FanStatus;
        var state = new StripState
        {
            GameOn = gameOn, GameBy = autoGame.Owned ? autoGame.Game : null,
            GameTargets = s.GameTargets, GameGpuPercent = s.GameGpuPercent, GameCoolerPercent = s.GameCoolerPercent,
            BoardSafety = ac?.LastSafety,
            GpuProtection = fs?.SafetyNotice is string n && n.StartsWith("PROTECTION", StringComparison.Ordinal) ? n : null,
            CoolerPanicC = cs is { Controlling: true, Notice: string cn } && cn.StartsWith("water", StringComparison.Ordinal) ? cooler?.Latest?.Pump?.WaterTempC : null,
        };
        var items = StatusStrip.Items(state, DateTimeOffset.Now, s.BoardFanName);
        var text = string.Join("   |   ", items.Select(i => i.Text));
        if (stripText.Text != text) stripText.Text = text;
        strip.ToolTip = items.Count == 0 ? null : string.Join("\n", items.Select(i => i.Tip));
        strip.Cursor = state.BoardSafety != null ? System.Windows.Input.Cursors.Hand : null;
        var vis = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (strip.Visibility != vis) { strip.Visibility = vis; LayoutChanged?.Invoke(); }
    }

    // ---- Water cooler (ROG RYUJIN II): pump + radiator presets and speeds, in the row below the MOTHERBOARD.
    private RyujinService? cooler;
    private CoolerConfig? coolerBeforeGame;
    private Border coolerRow = null!;
    private StackPanel coolerButtons = null!;
    private TextBlock coolerStatus = null!;
    private UniformGrid coolerGrid = null!;
    private readonly List<FanCard> coolerCards = [];
    private string coolerLayout = "";
    public event Action? ThermalRequested;

    public void AttachCooler(RyujinService r)
    {
        cooler = r;
        // Embedded fan stuck at FULL outside game mode (old game mode that did not return the cooler): back to STD once.
        if (!gameOn && !System.IO.File.Exists(GameStatePath) && r.UnstickEmbedded()) r.ResumeControl();
        r.FanChanged += _ => Dispatcher.BeginInvoke(RenderCooler);
        RenderCooler(); RenderFanNames();
    }

    /// <summary>null = CURVES (the cooler's own config: curves/fixed per part).</summary>
    private static readonly (RyujinCurves.Preset? P, string Label)[] CoolerPresets =
        [(null, "CURVES"), (RyujinCurves.Preset.Silent, "SILENT"), (RyujinCurves.Preset.Standard, "STD"), (RyujinCurves.Preset.Turbo, "TURBO"), (RyujinCurves.Preset.FullSpeed, "FULL")];

    private void RenderCooler()
    {
        if (cooler == null || !cooler.DeviceFound) { if (coolerRow.Visibility != Visibility.Collapsed) { coolerRow.Visibility = Visibility.Collapsed; LayoutChanged?.Invoke(); } return; }
        if (coolerRow.Visibility != Visibility.Visible) { coolerRow.Visibility = Visibility.Visible; LayoutChanged?.Invoke(); }
        var c = cooler.FanConfig;
        var fs = cooler.FanStatus;
        RyujinCurves.Preset? cur = c.Enabled ? MonitorTray.Ryujin.CoolerModes.UniformPreset(c) : null;
        coolerButtons.Children.Clear();
        foreach (var (p, label) in CoolerPresets)
        {
            bool on = c.Enabled && (p == null ? cur == null : cur == p);
            var (b, _) = ModeButton(label, on, p == RyujinCurves.Preset.FullSpeed);
            var pp = p;
            // CURVES applies the cooler's own config (the one from before the preset, or the default curves); the thermal control window opens from the title.
            b.ToolTip = pp is RyujinCurves.Preset ? $"Water cooler pump and radiator at {label}" : "Pump and radiator on the cooler curves (the ones from before the preset)";
            if (!c.Enabled) b.ToolTip += "\n(turns on cooler control by Marz)";
            b.Click += () =>
            {
                if (pp is RyujinCurves.Preset preset) cooler.ApplyPreset(preset, enable: true);
                else cooler.ApplyCurves();
                cooler.ResumeControl();
                RenderCooler();
            };
            coolerButtons.Children.Add(b);
        }
        coolerStatus.Text = !c.Enabled ? "read-only" : fs.TuneProgress is double tp ? $"auto tuning {(int)Math.Round(tp * 11) + 1}/11" : fs.Notice != null ? "warning" : "";
        coolerStatus.ToolTip = fs.Notice;
        RenderCoolerCards();
        RenderStrip();
    }

    /// <summary>Cooler cards chosen in Settings: pump, one per radiator fan, embedded fan.</summary>
    private void RenderCoolerCards()
    {
        if (cooler == null) return;
        var r = cooler.Latest;
        var s = app.Settings;
        var sel = s.PopupCoolerFans;
        // The user's name (Settings.CoolerNames) replaces PUMP/RAD/EMB.; the original goes to the tooltip.
        string? Custom(string part) => FanNames.Custom(s.CoolerNames, part)?.ToUpperInvariant();
        string Tip(string part, string what) => FanNames.Custom(s.CoolerNames, part) != null ? s.CoolerTip(part) + "\n" + what : what;
        var items = new List<(string Name, string Tip, int? Rpm, int? Pct, string Id)>();
        if (sel.Contains("pump")) items.Add((Custom("pump") ?? "PUMP", Tip("pump", "Water cooler pump"), r?.Pump?.PumpRpm, r?.PumpPercent, ThermalMap.PumpId));
        if (sel.Contains("radiator"))
        {
            var rpm = r?.Fans?.Rpm ?? [];
            var rad = Custom("radiator");
            if (rpm.Length == 0) items.Add((rad ?? "RADIATOR", Tip("radiator", "Radiator fans"), null, r?.Fans?.DutyPercent, ThermalMap.RadiatorId));
            for (int i = 0; i < rpm.Length; i++) items.Add((rad != null ? $"{rad} {i + 1}" : $"RAD {i + 1}", Tip("radiator", $"Radiator · fan {i + 1}"), rpm[i], r!.Fans!.DutyPercent, ThermalMap.RadiatorId));
        }
        if (sel.Contains("embedded")) items.Add((Custom("embedded") ?? "EMB.", Tip("embedded", "Fan embedded in the pump"), r?.Pump?.EmbeddedFanRpm, r?.EmbeddedPercent, ThermalMap.EmbeddedId));
        var layout = string.Join("|", items.Select(x => x.Name + "=" + x.Tip));
        if (layout != coolerLayout)
        {
            coolerLayout = layout; coolerCards.Clear(); coolerGrid.Children.Clear();
            coolerGrid.Columns = FanCols;
            coolerGrid.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (name, tip, _, _, id) in items)
            {
                var card = new FanCard(name, locked: false) { ToolTip = tip + "\nRight click: quick adjust", Margin = CardMargin(coolerCards.Count) };
                var aid = id;
                card.RightClick += () => OpenQuick(card, "cooler", aid);
                coolerCards.Add(card); coolerGrid.Children.Add(card);
            }
            LayoutChanged?.Invoke();
        }
        var cfg = cooler.FanConfig;
        for (int i = 0; i < items.Count; i++) { coolerCards[i].Set(items[i].Rpm, items[i].Pct, isOff: false); coolerCards[i].SetManual(cfg.Enabled && CoolerPart(cfg, items[i].Id).Mode == CoolerFanMode.Fixed); }
        quickRefresh?.Invoke();
    }

    /// <summary>Fan cards (board and cooler) in the same 5-column grid: same size in both sections; more than 5 wraps the row.</summary>
    private const int FanCols = 5;
    private static Thickness CardMargin(int i) => new(i % FanCols == 0 ? 0 : 3, i >= FanCols ? 3 : 0, 0, 0);

    /// <summary>Mode/preset button (board, cooler and GPU FAN SPEED): 9 px SemiBold, height 18, padding 4, natural width.</summary>
    private static (FlatButton B, TextBlock T) ModeButton(string label, bool on, bool full)
    {
        var t = Ui.T(label, 9, Theme.Muted, weight: 600, tabular: true).With(x => { x.HorizontalAlignment = HorizontalAlignment.Center; x.VerticalAlignment = VerticalAlignment.Center; x.TextTrimming = TextTrimming.None; });
        var b = new FlatButton(Theme.Muted) { Height = 18, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 2, 0), ActiveBg = full ? Theme.UStroke : Theme.Fill }.Content(t);
        SetModeActive(b, t, on);
        return (b, t);
    }

    private static void SetModeActive(FlatButton b, TextBlock t, bool on)
    {
        b.Active = on; t.SetResourceReference(TextBlock.ForegroundProperty, on ? Theme.Bg : Theme.Muted);
    }

    private void RenderGameIcon()
    {
        // Active while the request is on; if the user changed the fans elsewhere (AUTO, CURVES), the icon goes dark.
        var gpuFull = !app.Settings.GameTargets.Contains("gpu") || fanService?.State is { Mode: FanMode.Manual } gs && gs.RequestedPercent == (uint)app.Settings.GameGpuPercent;
        var acFull = !app.Settings.GameTargets.Contains("board") || ac is not { Available: true } || (ac.Pending ?? ac.Mode) == "full";
        TryRestoreBoard();
        bool settled = !pendingBoardRestore && (DateTime.UtcNow - gameAt).TotalSeconds > 8; // gives the GPU and Armoury Crate time to confirm
        if (gameOn && settled && fanService?.State is { Busy: false } st && st.Mode != FanMode.Unknown && !gpuFull && !(ac?.Pending != null)) gameOn = false;
        if (gameOn && settled && ac is { Available: true, Pending: null } && !acFull) gameOn = false;
        if (gameOn && settled && coolerBeforeGame != null && cooler?.FanConfig is { } cc && !cc.Enabled) gameOn = false;
        // Game mode went off on its own (GPU/board changed elsewhere): the cooler also goes back to what it was, otherwise it stays at FULL.
        if (!gameOn && coolerBeforeGame != null && !shuttingDown && cooler != null)
        {
            var cur = cooler.FanConfig;
            cooler.SaveFans(MonitorTray.Ryujin.CoolerModes.UndoGame(cur, coolerBeforeGame, app.Settings.GameCoolerPercent) with { Enabled = cur.Enabled });
            coolerBeforeGame = null;
        }
        if (game.Active && !gameOn && !shuttingDown) SaveGameState();
        game.Active = gameOn;
        game.ToolTip = gameOn ? "Game mode ON" + (autoGame.Owned ? $" (automatic: {autoGame.Game})" : "") + " — click to go back to normal"
            : "Game mode: " + GameSummary() + "\n(configurable in Settings)";
        RenderStrip();
    }
    private AsusFanModes? ac;
    private Border acRow = null!;
    private StackPanel acButtons = null!;
    private System.Windows.Controls.Primitives.UniformGrid acRpmGrid = null!;
    private TextBlock acStatus = null!;
    private WrapPanel acModeChecks = null!, acFanChecks = null!, boardCardChecks = null!;
    private Border acModesSetting = null!, acFansSetting = null!, boardCardsSetting = null!;
    private readonly List<(uint? Value, FlatButton Button, TextBlock Text)> fanButtons = [];
    private static readonly uint?[] PopupPresets = [null, 30, 50, 75, 100];
    /// <summary>Header: dragging moves the popup; double click puts it back above the clock.</summary>
    public Border Header { get; }
    public event Action? CloseRequested, DetailsRequested, LayoutChanged, AboutRequested, QuitRequested;
    private readonly Check15 startHidden, closeToTray;

    public PopupContent(AppState app)
    {
        this.app = app;
        Width = 344;
        // ---- B1 header
        var header = new DockPanel { LastChildFill = false };
        var hb = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 4, 6, 4), header);
        hb.Background = Brushes.Transparent; // drag area
        Header = hb;
        back = FlatButton.Icon(Icons.ChevronLeft, 20, 13, "Back");
        back.Margin = new Thickness(-4, 0, 6, 0);
        back.Click += () => ShowSettings(false);
        glyph = Ui.Glyph(6).With(g => g.Margin = new Thickness(0, 0, 6, 0));
        title = Ui.T("MARZ MONITOR", 12, weight: 600);
        title.VerticalAlignment = VerticalAlignment.Center;
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { back, glyph, title } };
        DockPanel.SetDock(left, Dock.Left);
        header.Children.Add(left);
        refresh = Ui.T("1s", 11, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        var refreshBtn = new FlatButton { Height = 22, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 2, 0), ToolTip = "Refresh rate (click to switch: 0.5s · 1s · 2s)" }.Content(refresh);
        refreshBtn.Click += () => app.Update(s => s with { RefreshMs = s.RefreshMs switch { 500 => 1000, 1000 => 2000, _ => 500 } });
        var (dIcon, _) = Icons.Make(Icons.ExternalLink, 12, Theme.Muted);
        dIcon.HorizontalAlignment = HorizontalAlignment.Center; dIcon.VerticalAlignment = VerticalAlignment.Center;
        details = new FlatButton { Width = 22, Height = 22, ToolTip = "Open details" }.Content(dIcon);
        details.Click += () => DetailsRequested?.Invoke();
        gear = FlatButton.Icon(Icons.Settings, 22, 13, "Settings");
        gear.Margin = new Thickness(2, 0, 0, 0);
        gear.Click += () => ShowSettings(settings!.Visibility != Visibility.Visible);
        // Pin: pinned = popup stays open when clicking outside; unpinned = hides on focus loss. Same setting as "Close on focus loss".
        pin = FlatButton.Icon(Icons.Pin, 22, 12, "");
        pin.Margin = new Thickness(0, 0, 2, 0);
        pinIcon = (FrameworkElement)pin.Child;
        pin.Click += () => app.Update(s => s with { Autohide = !s.Autohide });
        var close = FlatButton.Icon(Icons.X, 22, 12, "Close");
        close.Margin = new Thickness(2, 0, 0, 0);
        close.Click += () => { if (app.Settings.CloseToTray) CloseRequested?.Invoke(); else QuitRequested?.Invoke(); };
        // Game mode: all fans at 100% (GPU + board via Armoury Crate). Click again to go back to automatic/curves.
        game = FlatButton.Icon(Icons.Gamepad, 22, 14, "Game mode: all fans at 100%");
        game.Margin = new Thickness(0, 0, 2, 0); game.ActiveBg = Theme.UStroke;
        game.Click += ToggleGame;
        var thermal = FlatButton.Icon(Icons.Thermometer, 22, 14, "Thermal control: water cooler, board fans and curves");
        thermal.Margin = new Thickness(0, 0, 2, 0);
        thermal.Click += () => ThermalRequested?.Invoke();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { thermal, game, refreshBtn, pin, details, gear, close } };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        Children.Add(hb);

        // ---- Automatic actions strip (protections that kicked in, game mode): thin, hidden when there is nothing.
        stripText = Ui.T("", 10, weight: 600, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis; });
        var stripTint = new Border { Opacity = 0.16 };
        stripTint.SetResourceReference(Border.BackgroundProperty, Theme.UStroke);
        var stripEdge = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left };
        stripEdge.SetResourceReference(Border.BackgroundProperty, Theme.UStroke);
        strip = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), new Grid { Children = { stripTint, stripEdge, new Border { Padding = new Thickness(10, 2, 10, 3), Child = stripText } } });
        strip.Visibility = Visibility.Collapsed;
        strip.MouseLeftButtonUp += (_, _) => { if (ac?.LastSafety != null) { ac.LastSafety = null; RenderStrip(); } };
        Children.Add(strip);

        // ---- B2 metrics
        main = new StackPanel();
        var metrics = new Grid();
        for (int i = 0; i < 3; i++) metrics.ColumnDefinitions.Add(new ColumnDefinition());
        cpu = new Metric("CPU", true); gpu = new Metric("GPU", true); mem = new Metric("MEM", false);
        var cells = new[] { cpu, gpu, mem };
        for (int i = 0; i < 3; i++)
        {
            var b = Ui.Box(new Thickness(0, 0, i < 2 ? 1 : 0, 0), new Thickness(9, 4, 9, 5), cells[i]);
            Grid.SetColumn(b, i); metrics.Children.Add(b);
        }
        main.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), metrics));

        // ---- B3 disk
        (rd, _) = (Ui.T("0.0", 16, weight: 600, tabular: true), 0);
        (wr, _) = (Ui.T("0.0", 16, weight: 600, tabular: true), 0);
        // Read and write in a single box: "R/W  2.1 / 0.8 MB/s".
        var slash = Ui.T("/", 13, Theme.Muted).With(t => { t.Margin = new Thickness(4, 0, 4, 0); t.VerticalAlignment = VerticalAlignment.Center; });
        rd.VerticalAlignment = VerticalAlignment.Center; wr.VerticalAlignment = VerticalAlignment.Center;
        var le = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            Ui.T("R/W", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); }),
            rd, slash, wr,
            Ui.T("MB/s", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(4, 3, 0, 0); }),
        } };
        _ = le; // the R/W total left the popup: each drive shows its own (the STORAGE tooltip has the total)
        // Small STORAGE on the left and the drives on the same row (letter, %, R/W and usage bar).
        storageLabel = Ui.T("STORAGE", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); t.Opacity = 0.85; t.Background = Brushes.Transparent; });
        volumesPanel = new UniformGrid { Rows = 1 };
        var sd = new DockPanel();
        DockPanel.SetDock(storageLabel, Dock.Left); sd.Children.Add(storageLabel); sd.Children.Add(volumesPanel);
        volumesBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(9, 4, 5, 4), sd);
        volumesBox.Visibility = Visibility.Collapsed;
        main.Children.Add(volumesBox);

        // ---- B4 network
        down = Ui.T("0", 16, weight: 600, tabular: true); downU = Ui.T("KB/s", 11, Theme.Muted);
        up = Ui.T("0", 16, weight: 600, tabular: true); upU = Ui.T("KB/s", 11, Theme.Muted);
        var (ai, _) = Icons.Make(Icons.ArrowDown, 10, Theme.DStroke);
        var (ui, _) = Icons.Make(Icons.ArrowUp, 10, Theme.UStroke);
        _ = (ai, ui); // the NET row is gone: the current value sits in the chart corner

        // ---- B5 mirrored chart
        chart = new MirrorChart { Height = 58 };
        // Own row below the chart (nothing covers the curve): NET, ↓ and ↑ side by side on the left (current highlighted | scale).
        scaleDown = Ui.T("", 10, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; });
        scaleUp = Ui.T("", 10, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(12, 0, 0, 0); });
        var netLabel = Ui.T("NET", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 1, 8, 0); t.Opacity = 0.85; });
        var netNow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0), Children = { netLabel, scaleDown, scaleUp } };
        var chartBox = Ui.Box(new Thickness(0, 1, 0, 1), new Thickness(0), chart);
        chartBox.Height = 58 + 2;
        main.Children.Add(new StackPanel { Margin = new Thickness(9, 5, 9, 5), Children = { chartBox, netNow } });

        // ---- Temperatures: cards chosen and ordered in Settings; sensor without a reading = card omitted.
        tempsGrid = new UniformGrid { Rows = 1 };
        tempsBox = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 5), tempsGrid);
        tempsBox.Visibility = Visibility.Collapsed;
        main.Children.Add(tempsBox);

        // ---- GPU: title, FAN SPEED (same button as the modes) and, on the right, the fan icon with the mode (A or %) and each fan's %.
        var fd = new DockPanel { LastChildFill = false };
        gpuName = Ui.T("GPU", 12, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); t.Background = Brushes.Transparent; });
        var (fanIcon, fanPath) = Icons.Make(Icons.Fan, 15, Theme.Fill);
        this.fanPath = fanPath;
        fanSpin.CenterX = 12; fanSpin.CenterY = 12; fanPath.RenderTransform = fanSpin;
        fanBadge = Ui.T("A", 8, Theme.Text, weight: 600).With(t => { t.HorizontalAlignment = HorizontalAlignment.Left; t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; });
        var badgeBox = new Border { Child = fanBadge, Padding = new Thickness(1, 0, 1, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(-4, 0, 0, -2) };
        badgeBox.SetResourceReference(Border.BackgroundProperty, Theme.Bg);
        var fanGlyph = new Grid { Width = 15, Height = 15, VerticalAlignment = VerticalAlignment.Center, Children = { fanIcon, badgeBox } };
        fanSpeeds = Ui.T("", 10, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(5, 0, 0, 0); });
        var fanBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { fanGlyph, fanSpeeds } };
        // FAN SPEED on the title row: presets with the same button as the modes; blocked ones are dimmed with the reason.
        var presets = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        presets.Children.Add(Ui.T("FAN SPEED", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); t.Opacity = 0.75; }));
        foreach (var v in PopupPresets)
        {
            var (b, t) = ModeButton(v is uint pv ? pv.ToString(CultureInfo.InvariantCulture) : "A", false, v == 100);
            var value = v;
            b.Click += async () =>
            {
                if (fanService == null || !b.IsEnabled) return;
                FanChoices.Update(d => d with { Gpu = value });
                try { await fanService.CommandAsync(value); } catch (Exception) { }
            };
            fanButtons.Add((v, b, t));
            presets.Children.Add(b);
        }
        DockPanel.SetDock(gpuName, Dock.Left); DockPanel.SetDock(presets, Dock.Left); DockPanel.SetDock(fanBox, Dock.Right);
        fd.Children.Add(gpuName); fd.Children.Add(presets); fd.Children.Add(fanBox);
        gpuName.Cursor = System.Windows.Input.Cursors.Hand; gpuName.ToolTip = "Open the GPU in Details";
        gpuName.MouseLeftButtonUp += (_, _) => DetailsRequested?.Invoke();
        fanRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 4), fd);
        fanRow.Visibility = Visibility.Collapsed;
        main.Children.Add(fanRow);

        // ---- Board (via Armoury Crate): modes chosen in Settings + cards of the chosen fans (click = OFF, experimental).
        var ad = new DockPanel { LastChildFill = true };
        var al = Ui.T("BOARD", 12, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); });
        acButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        acStatus = Ui.T("", 9, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; t.TextTrimming = TextTrimming.CharacterEllipsis; t.Background = Brushes.Transparent; });
        DockPanel.SetDock(al, Dock.Left); DockPanel.SetDock(acButtons, Dock.Left);
        ad.Children.Add(al); ad.Children.Add(acButtons); ad.Children.Add(acStatus);
        acRpmGrid = new UniformGrid { Columns = FanCols, Margin = new Thickness(0, 4, 0, 0) };
        acRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 5), new StackPanel { Children = { ad, acRpmGrid } });
        acRow.Visibility = Visibility.Collapsed;
        main.Children.Add(acRow);

        // ---- Water cooler (shown only with the RYUJIN II connected): presets + cards chosen in Settings.
        var cd = new DockPanel { LastChildFill = true };
        var cl = Ui.T("WC RYUJIN II", 12, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); t.Cursor = System.Windows.Input.Cursors.Hand; t.ToolTip = "Open thermal control"; t.Background = Brushes.Transparent; });
        cl.MouseLeftButtonUp += (_, _) => ThermalRequested?.Invoke();
        coolerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        coolerStatus = Ui.T("", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; t.Background = Brushes.Transparent; });
        DockPanel.SetDock(cl, Dock.Left); DockPanel.SetDock(coolerButtons, Dock.Left);
        cd.Children.Add(cl); cd.Children.Add(coolerButtons); cd.Children.Add(coolerStatus);
        coolerGrid = new UniformGrid { Columns = FanCols, Margin = new Thickness(0, 4, 0, 0) };
        coolerRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 5), new StackPanel { Children = { cd, coolerGrid } });
        coolerRow.Visibility = Visibility.Collapsed;
        main.Children.Add(coolerRow);

        // ---- Keyboard (OLED): current screen, pin, next, edit screens.
        var kd = new DockPanel { LastChildFill = true };
        var kl = Ui.T("KEYBOARD", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); });
        kbPin = FlatButton.Icon(Icons.Pin, 20, 11, "Pin/unpin the keyboard screen");
        kbPin.Click += () => KeyboardAction?.Invoke("pin");
        var next = FlatButton.Icon(Icons.ChevronRight, 20, 12, "Next keyboard screen");
        next.Click += () => KeyboardAction?.Invoke("next");
        var edit = FlatButton.Icon(Icons.Pencil, 20, 11, "Edit the keyboard screens");
        edit.Click += () => KeyboardRequested?.Invoke();
        var kr = new StackPanel { Orientation = Orientation.Horizontal, Children = { kbPin, next, edit } };
        kbText = Ui.T("", 11, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        DockPanel.SetDock(kl, Dock.Left); DockPanel.SetDock(kr, Dock.Right);
        kd.Children.Add(kl); kd.Children.Add(kr); kd.Children.Add(kbText);
        kbRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 3, 7, 3), kd);
        kbRow.Visibility = Visibility.Collapsed;
        main.Children.Add(kbRow);
        Children.Add(main);

        // ---- B6 settings
        settings = new StackPanel { Visibility = Visibility.Collapsed };
        paletteLabel = Label("COLOR · ELECTRIC");
        var sw = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in Theme.Palettes)
        {
            var inner = new Border { Width = 20, Height = 20, Background = Theme.Diagonal(p.A, p.B), ToolTip = p.Name, Cursor = System.Windows.Input.Cursors.Hand };
            var outline = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(1), Margin = new Thickness(swatches.Count == 0 ? 0 : 3, -3, -3, -3), Child = inner, SnapsToDevicePixels = true };
            if (swatches.Count > 0) outline.Margin = new Thickness(7 - 6 + 3, -3, -3, -3);
            var id = p.Id;
            inner.MouseLeftButtonUp += (_, e) => { e.Handled = true; app.Update(s => s with { Palette = id }); };
            swatches.Add((p.Id, outline));
            sw.Children.Add(outline);
        }
        settings.Children.Add(SettingRow(paletteLabel, sw, new Thickness(10, 7, 10, 7)));
        segTheme = Seg(("light", "Light"), ("dark", "Dark"), ("system", "System"));
        segTheme.Changed += v => app.Update(s => s with { Theme = (string)v });
        settings.Children.Add(SettingRow(Label("THEME"), segTheme));
        segRefresh = Seg((500, "0.5s"), (1000, "1s"), (2000, "2s"));
        segRefresh.Changed += v => app.Update(s => s with { RefreshMs = (int)v });
        settings.Children.Add(SettingRow(Label("REFRESH"), segRefresh));
        segHistory = Seg((60, "1m"), (300, "5m"), (600, "10m"), (1800, "30m"), (3600, "1h"), (7200, "2h"));
        segHistory.Changed += v => app.Update(s => s with { History = (int)v });
        settings.Children.Add(SettingRow(Label("HISTORY"), segHistory));
        segScale = Seg(("independent", "Separate"), ("shared", "Shared"));
        segScale.Changed += v => app.Update(s => s with { Scale = (string)v });
        settings.Children.Add(SettingRow(Label("NET SCALE"), segScale));
        segNetUnit = Seg(("bytes", "MB/s"), ("bits", "Mbps"));
        segNetUnit.Changed += v => app.Update(s => s with { NetUnit = (string)v });
        settings.Children.Add(SettingRow(Label("NET UNIT"), segNetUnit));
        acModeChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        acModesSetting = SettingRow(Label("BOARD MODES"), acModeChecks); acModesSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(acModesSetting);
        acFanChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        acFansSetting = SettingRow(Label("FANS IN MODES"), acFanChecks); acFansSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(acFansSetting);
        // Popup: temperature cards (which and order), board and cooler fan cards, turn off fan (experimental).
        tempChecks = new StackPanel();
        settings.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 6, 6, 4), new StackPanel { Children =
        {
            new DockPanel { Margin = new Thickness(0, 0, 4, 3), Children = { Label("POPUP TEMPERATURES"), Ui.T($"max {TempCards.MaxCards} · order ▲▼", 10, Theme.Muted).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }) } },
            tempChecks,
        } }));
        RenderTempSettings();
        boardCardChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        boardCardsSetting = SettingRow(Label("BOARD FANS"), boardCardChecks); boardCardsSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(boardCardsSetting);
        coolerCardChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        settings.Children.Add(SettingRow(Label("COOLER FANS"), coolerCardChecks));
        RenderCoolerSettings();
        // Fan names: the original header on the left, the user's name on the right (empty = original).
        fanNameRows = new StackPanel();
        fanNamesSetting = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 6, 6, 6), new StackPanel { Children =
        {
            new DockPanel { Margin = new Thickness(0, 0, 4, 0), Children = { Label("FAN NAMES"), Ui.T($"empty = original · max {FanNames.MaxLength}", 10, Theme.Muted).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }) } },
            fanNameRows,
        } }).With(b => { b.Visibility = Visibility.Collapsed; b.ToolTip = "Your name for each fan, shown in the popup, the quick card and thermal control.\nEnter or clicking away saves, Esc undoes. Empty = the name printed on the board."; });
        settings.Children.Add(fanNamesSetting);
        boardOff = new Check15(); boardOff.Changed += v => app.Update(s => s with { BoardFanOff = v });
        var offRow = SettingRow(Label("TURN OFF BOARD FAN"), boardOff);
        offRow.ToolTip = "Experimental: clicking a board fan card turns it off/back on.\nOn the chip it becomes the protection curve: 0% up to 80 °C, 100% from 85 °C.\nCPU and pumps do not turn off; everything turns back on with CPU > 85 °C, GPU > 80 °C, FULL or game mode; not kept after quitting.";
        settings.Children.Add(offRow);
        gameChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        settings.Children.Add(SettingRow(Label("GAME MODE"), gameChecks));
        gameFanChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        gameFansSetting = SettingRow(Label("BOARD IN GAME"), gameFanChecks); gameFansSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(gameFansSetting);
        var segGpuG = Seg(Settings.GamePercents.Select(p => ((object)p, $"{p}%")), app.Settings.GameGpuPercent);
        segGpuG.Changed += v => app.Update(x => x with { GameGpuPercent = (int)v });
        settings.Children.Add(SettingRow(Label("GPU IN GAME"), segGpuG));
        var segCoolG = Seg(Settings.GamePercents.Select(p => ((object)p, $"{p}%")), app.Settings.GameCoolerPercent);
        segCoolG.Changed += v => app.Update(x => x with { GameCoolerPercent = (int)v });
        settings.Children.Add(SettingRow(Label("COOLER IN GAME"), segCoolG));
        RenderGameSettings();
        // Games that turn game mode on by themselves (.exe name); detection via the process list every ~3 s, no admin.
        gameExeList = new WrapPanel();
        gameExeInput = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = AppFonts.Body, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null, MaxLength = 64 };
        gameExeInput.SetResourceReference(TextBox.ForegroundProperty, Theme.Text);
        gameExeInput.SetResourceReference(TextBox.CaretBrushProperty, Theme.Text);
        gameExeInput.SetResourceReference(TextBox.SelectionBrushProperty, Theme.Fill);
        var exeHint = Ui.T("game.exe", 11, Theme.Muted, head: false).With(t => { t.IsHitTestVisible = false; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(1, 0, 0, 0); });
        gameExeInput.TextChanged += (_, _) => exeHint.Visibility = gameExeInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        gameExeInput.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { AddGameExe(); e.Handled = true; } };
        var exeBox = Ui.Box(new Thickness(1), new Thickness(6, 0, 6, 0), new Grid { Children = { exeHint, gameExeInput } }).With(b => { b.Height = 20; b.Cursor = System.Windows.Input.Cursors.IBeam; });
        exeBox.MouseLeftButtonDown += (_, _) => gameExeInput.Focus();
        var addExe = new FlatButton { Height = 20, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(1), ToolTip = "Add the executable to the list" }.Content(Ui.T("ADD", 10, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        addExe.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        addExe.Click += AddGameExe;
        var addRow = new DockPanel { Margin = new Thickness(0, 4, 4, 0) };
        DockPanel.SetDock(addExe, Dock.Right); addRow.Children.Add(addExe); addRow.Children.Add(exeBox);
        settings.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 6, 6, 6), new StackPanel { Children =
        {
            new DockPanel { Margin = new Thickness(0, 0, 4, 2), Children = { Label("AUTOMATIC GAME MODE"), Ui.T("on while the game runs", 10, Theme.Muted).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }) } },
            gameExeList, addRow,
        } }).With(b => b.ToolTip = "With one of these executables running, game mode turns on; when all of them close, it turns off.\nIf you turned game mode on with the button, automatic does not turn it off. Turned off with the button while the game is open, it only comes back with the next game."));
        RenderGameExes();
        // Tray icon: highest temperature among the enabled cards and/or alert color near the limit.
        trayTemp = new Check15 { VerticalAlignment = VerticalAlignment.Center }; trayTemp.Changed += v => app.Update(s => s with { TrayTemp = v });
        trayAlert = new Check15 { VerticalAlignment = VerticalAlignment.Center }; trayAlert.Changed += v => app.Update(s => s with { TrayAlert = v });
        StackPanel Opt(Check15 c, string label) => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 2), Background = Brushes.Transparent, Children = { c, Ui.T(label, 10, Theme.Muted).With(t => { t.Margin = new Thickness(4, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }) } };
        var trayRow = SettingRow(Label("TRAY ICON"), new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230, Children = { Opt(trayTemp, "TEMPERATURE"), Opt(trayAlert, "ALERT COLOR") } });
        trayRow.ToolTip = "Temperature: the icon shows the ones chosen in ICON TEMPERATURE (or the highest among the cards).\nAlert color: amber with any card within 5 °C of its limit, red at the limit.\nWith either one on, sensors keep being read with the popup closed.";
        settings.Children.Add(trayRow);
        segTrayFont = Seg(Settings.TrayFontSizes.Select(x => ((object)x, x.ToUpperInvariant())), app.Settings.TrayFontSize);
        segTrayFont.Changed += v => app.Update(s => s with { TrayFontSize = (string)v });
        trayTempPicks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        settings.Children.Add(SettingRow(Label("ICON TEMPERATURE"), trayTempPicks).With(b => b.ToolTip = "Which temperatures the icon shows, in the order you turn them on.\nNone = the hottest among the popup cards.\nMore than one = they alternate at the interval chosen below.\nThe icon tooltip tells which one is showing."));
        segTrayRotate = Seg(Settings.TrayRotateSeconds.Select(x => ((object)x, x + "s")), app.Settings.TrayRotateSec);
        segTrayRotate.Changed += v => app.Update(s => s with { TrayRotateSec = (int)v });
        settings.Children.Add(SettingRow(Label("SWITCH EVERY"), segTrayRotate).With(b => b.ToolTip = "Time each temperature stays on the icon when more than one is chosen."));
        settings.Children.Add(SettingRow(Label("FONT SIZE"), segTrayFont).With(b => b.ToolTip = "Temperature number on the tray icon: XS (default), S, M or L (fills the whole icon)."));
        opacity = new MiniSlider();
        opacityLabel = Ui.T("90%", 13, tabular: true).With(t => { t.Width = 32; t.TextAlignment = TextAlignment.Right; t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; });
        opacity.Changed += v => app.Update(s => s with { Opacity = v });
        settings.Children.Add(SettingRow(Label("OPACITY"), new StackPanel { Orientation = Orientation.Horizontal, Children = { opacity, opacityLabel } }));
        autostart = new Check15(); autostart.Changed += v => app.Update(s => s with { Autostart = v });
        settings.Children.Add(SettingRow(Label("START WITH WINDOWS"), autostart));
        autohide = new Check15(); autohide.Changed += v => app.Update(s => s with { Autohide = v });
        settings.Children.Add(SettingRow(Label("CLOSE ON FOCUS LOSS"), autohide));
        startHidden = new Check15(); startHidden.Changed += v => app.Update(s => s with { StartHidden = v });
        settings.Children.Add(SettingRow(Label("START HIDDEN IN TRAY"), startHidden));
        closeToTray = new Check15(); closeToTray.Changed += v => app.Update(s => s with { CloseToTray = v });
        settings.Children.Add(SettingRow(Label("CLOSE HIDES TO TRAY"), closeToTray));
        // Footer: version + About
        var ver = Ui.T($"v{AppInfo.Version}", 10, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        var about = new FlatButton { Padding = new Thickness(6, 1, 6, 1), ToolTip = "About Marz Monitor" }.Content(Ui.T("ABOUT", 11, Theme.Muted));
        about.Click += () => AboutRequested?.Invoke();
        var quit = new FlatButton { Padding = new Thickness(6, 1, 6, 1), ToolTip = "Quit (fans return to automatic)" }.Content(Ui.T("QUIT", 11, Theme.Muted));
        quit.Click += () => QuitRequested?.Invoke();
        var fdock = new DockPanel();
        DockPanel.SetDock(ver, Dock.Left); fdock.Children.Add(ver);
        var fr = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { about, quit } };
        fdock.Children.Add(fr);
        settings.Children.Add(Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(10, 4, 6, 4), fdock));
        // With the popup lists the page gets taller than a 1080p screen: it scrolls inside the popup.
        settingsScroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false, MaxHeight = SettingsMaxHeight };
        Children.Add(settingsScroll);

        ShowSettings(false);
        ApplySettings(app.Settings);
    }

    public bool InSettings => settings.Visibility == Visibility.Visible;

    private readonly ScrollViewer settingsScroll;
    /// <summary>Max height of the Settings page (scrolls beyond it); the preview uses infinity so the PNG comes out whole.</summary>
    internal static double SettingsMaxHeight { get; set; } = 760;

    public void ShowSettings(bool on)
    {
        settings.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        settingsScroll.Visibility = settings.Visibility;
        main.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        back.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        glyph.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        title.Text = on ? "SETTINGS" : "MARZ MONITOR";
        gear.Active = on;
        LayoutChanged?.Invoke();
    }

    public void SetDetailsOpen(bool open) => details.Active = open;

    private static TextBlock Label(string t) => Ui.T(t, 12, Theme.Muted).With(x => x.VerticalAlignment = VerticalAlignment.Center);

    private static Segmented Seg(params (object, string)[] opts) => new(opts);

    private static Border SettingRow(TextBlock label, FrameworkElement control, Thickness? pad = null, bool last = false)
    {
        var d = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(label, Dock.Left); d.Children.Add(label);
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right); d.Children.Add(control);
        return Ui.Box(new Thickness(0, 0, 0, last ? 0 : 1), pad ?? new Thickness(10, 6, 10, 6), d);
    }

    /// <summary>Network row below the chart: arrow + current speed highlighted and the small scale.</summary>
    private static void NetCorner(TextBlock t, string arrow, string current, string scale, string color)
    {
        t.Inlines.Clear();
        var a = new System.Windows.Documents.Run(arrow + " ") { FontSize = 12 }; a.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, color);
        var v = new System.Windows.Documents.Run(current) { FontSize = 14, FontFamily = AppFonts.For(true, 600) }; v.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Theme.Text);
        var sc = new System.Windows.Documents.Run(" | " + scale) { FontSize = 9 }; sc.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Theme.Muted);
        t.Inlines.Add(a); t.Inlines.Add(v); t.Inlines.Add(sc);
    }

    private static Grid RowGrid(string label, FrameworkElement a, FrameworkElement b)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = Ui.Box(new Thickness(0, 0, 1, 0), new Thickness(9, 4, 9, 4), Ui.T(label, 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        var ca = Ui.Box(new Thickness(0, 0, 1, 0), new Thickness(9, 4, 9, 4), a);
        var cb = Ui.Box(new Thickness(0), new Thickness(9, 4, 9, 4), b);
        Grid.SetColumn(ca, 1); Grid.SetColumn(cb, 2);
        g.Children.Add(l); g.Children.Add(ca); g.Children.Add(cb);
        return g;
    }

    private static StackPanel ValueCell(FrameworkElement prefix, TextBlock value, TextBlock unit)
    {
        prefix.VerticalAlignment = VerticalAlignment.Center;
        prefix.Margin = new Thickness(0, 0, 4, 0);
        value.VerticalAlignment = VerticalAlignment.Center;
        unit.Margin = new Thickness(4, 3, 0, 0); unit.VerticalAlignment = VerticalAlignment.Center;
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { prefix, value, unit } };
    }

    private void UpdateVolumes(IReadOnlyList<VolumeInfo> vols)
    {
        var letters = vols.Select(v => v.Letter).ToList();
        if (!letters.SequenceEqual(volumesPanel.Children.OfType<VolumeChip>().Select(r => r.Letter), StringComparer.OrdinalIgnoreCase))
        {
            volumesPanel.Children.Clear();
            var cols = Math.Clamp(vols.Count, 1, 6);
            volumesPanel.Columns = cols; volumesPanel.Rows = (vols.Count + cols - 1) / cols;
            foreach (var v in vols)
            {
                if (!volumeRows.TryGetValue(v.Letter, out var row)) volumeRows[v.Letter] = row = new VolumeChip(v.Letter);
                volumesPanel.Children.Add(row);
            }
            var vis = vols.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (volumesBox.Visibility != vis) volumesBox.Visibility = vis;
            LayoutChanged?.Invoke();
        }
        foreach (var v in vols) volumeRows[v.Letter].Set(v);
    }

    /// <summary>One small box per drive on a single row: "C:  R 12M W 850K  40%" (R/W blank when idle) + usage bar.</summary>
    private sealed class VolumeChip : Border
    {
        /// <summary>Short rate: "850K", "12M", "1.2G" (bytes/s, base 1024); empty below 1 KB/s.</summary>
        internal static string Rate(double bps)
        {
            const double K = 1024, M = K * K, G = M * K;
            return bps >= G ? (bps / G).ToString("0.0", CultureInfo.InvariantCulture) + "G"
                 : bps >= 100 * M ? (bps / M).ToString("0", CultureInfo.InvariantCulture) + "M"
                 : bps >= M ? (bps / M).ToString("0.#", CultureInfo.InvariantCulture) + "M"
                 : bps >= K ? (bps / K).ToString("0", CultureInfo.InvariantCulture) + "K" : "";
        }

        public string Letter { get; }
        private readonly TextBlock pct, rd, wr; private readonly Bar bar;
        public VolumeChip(string letter)
        {
            Letter = letter;
            BorderThickness = new Thickness(1); Padding = new Thickness(5, 0, 5, 0); Margin = new Thickness(0, 0, 4, 0);
            SetResourceReference(BorderBrushProperty, Theme.Line);
            Background = Brushes.Transparent;
            var top = new DockPanel();
            var l = Ui.T(letter, 11, weight: 600);
            pct = Ui.T("", 10, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(4, 0, 0, 0); });
            rd = Ui.T("", 9, Theme.DStroke, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; });
            wr = Ui.T("", 9, Theme.UStroke, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(3, 0, 0, 0); });
            var rates = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { rd, wr } };
            DockPanel.SetDock(l, Dock.Left); DockPanel.SetDock(pct, Dock.Right);
            top.Children.Add(l); top.Children.Add(pct); top.Children.Add(rates);
            bar = new Bar { Height = 2, Margin = new Thickness(-5, 1, -5, 1) };
            Child = new StackPanel { Children = { top, bar } };
        }
        public void Set(VolumeInfo v)
        {
            pct.Text = Math.Round(v.Fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            double r = v.ReadBps ?? 0, w = v.WriteBps ?? 0;
            rd.Text = Rate(r) is { Length: > 0 } rs ? "R " + rs : ""; wr.Text = Rate(w) is { Length: > 0 } ws ? "W " + ws : "";
            var hw = v.Hardware.Length > 0 ? v.Hardware : "disk";
            var tip = $"{v.Letter} {hw}" + (v.Label.Length > 0 ? $" · {v.Label}" : "") + $"\n{Formatter.UsedTotal(v.Used, v.Total)} used"
                + (v.ReadBps != null ? $"\nRead {Formatter.DiskMBps(r)} MB/s · Write {Formatter.DiskMBps(w)} MB/s" : "");
            if (!Equals(ToolTip, tip)) ToolTip = tip;
            bar.SetFraction(v.Fraction, v.Fraction >= 0.85);
        }
    }

    private Border kbRow = null!;
    private TextBlock kbText = null!;
    private FlatButton kbPin = null!;
    public event Action? KeyboardRequested;
    public event Action<string>? KeyboardAction;

    public void UpdateKeyboard(KeyboardState s)
    {
        var was = kbRow.Visibility;
        kbRow.Visibility = Visibility.Visible;
        var d = s.Delivery;
        kbText.Text = s.GgStatus ?? (!s.Enabled ? "off" : d.Error != null ? "GG not responding" : string.Join("  ", d.Lines.Select(l => l.Trim())).Trim() is { Length: > 0 } t ? $"{s.Page + 1}/{s.PageCount}  {t}" : $"screen {s.Page + 1}/{s.PageCount}");
        kbText.ToolTip = d.Error ?? (d.Lines.Length > 0 ? string.Join("\n", d.Lines) : null);
        kbPin.Active = s.Pinned;
        if (was != Visibility.Visible) LayoutChanged?.Invoke();
    }

    public void AttachFans(GpuFanService s) { fanService = s; UpdateFans(s.State); }

    public void AttachAsus(AsusFanModes m)
    {
        ac = m;
        m.Changed += () => Dispatcher.BeginInvoke(() => { TryRestoreBoard(); TryRestoreChoices(); RenderAc(); RenderGameIcon(); });
        m.OffRejected += _ => Dispatcher.BeginInvoke(RenderAcRpm); // protection curve did not verify: card goes back to on (error in the status)
        RenderAc();
    }

    private IReadOnlyDictionary<int, float> mbRpm = new Dictionary<int, float>(), mbCtl = new Dictionary<int, float>();
    private float? cpuTemp, gpuTemp;

    private readonly List<(string Fan, FanCard Card)> boardCards = [];
    private string boardLayout = "";

    /// <summary>The user's name for the fan (Settings.AcFanNames) or the header printed on the board (CHA_FAN1...).</summary>
    private string FanName(string acName) => app.Settings.BoardFanName(acName);

    /// <summary>Cards of the board fans chosen in Settings (rpm, %, mini bar). Click toggles OFF.</summary>
    private void RenderAcRpm()
    {
        if (ac == null) return;
        bool blocked = offBlocked is { } ob && (DateTime.UtcNow - ob.At).TotalSeconds < 5;
        acStatus.Text = ac.Error != null ? "error" : ac.Pending != null ? "applying…" : blocked ? "off refused" : "";
        acStatus.ToolTip = ac.Error ?? (blocked ? offBlocked!.Value.Why : null);
        var s = app.Settings;
        var fans = ac.Fans.Where(s.PopupBoardFans.Contains).ToList();
        var layout = string.Join("|", fans.Select(f => f + "=" + FanName(f)));
        if (layout != boardLayout)
        {
            boardLayout = layout; boardCards.Clear(); acRpmGrid.Children.Clear();
            acRpmGrid.Visibility = fans.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var f in fans)
            {
                var card = new FanCard(FanName(f).ToUpperInvariant(), BoardFanOff.Locked(f)) { Margin = CardMargin(boardCards.Count) };
                var fan = f;
                card.Click += () => ToggleBoardFan(fan);
                card.RightClick += () => OpenQuick(card, "board", fan);
                boardCards.Add((f, card)); acRpmGrid.Children.Add(card);
            }
            LayoutChanged?.Invoke();
        }
        foreach (var (f, card) in boardCards)
        {
            var rd = BoardFanMap.Read(f, mbRpm, mbCtl); // rpm only from verified connectors; never computed from the %
            bool off = ac.Off.IsOff(f);
            card.Set(rd.Rpm, rd.Percent, off);
            var man = ac.Off.ManualPercent(f);
            card.SetManual(man != null);
            card.Clickable = BoardFanOff.Clickable(f, s.BoardFanOff);
            var head = s.BoardFanTip(f)
                + (rd.RpmMeasured ? "" : $"\nrpm not measured (chip output of this connector not confirmed); control % of output {rd.ChipIndex?.ToString(CultureInfo.InvariantCulture) ?? "—"} (estimated).");
            // Option disabled: read-only card (no hand cursor, no turn-off hint).
            card.ToolTip = !s.BoardFanOff ? head
                : BoardFanOff.Locked(f) ? head + "\nCPU and pumps do not turn off."
                : off ? head + "\nOFF (0% up to 80 °C, 100% from 85 °C). Click to turn back on."
                : head + (BoardFanOff.BlockReason(f, cpuTemp, gpuTemp) is string why ? $"\nCannot turn off now: {why}." : "\nClick to turn off (turns back on by itself with CPU > 85 °C or GPU > 80 °C).");
            card.ToolTip = card.ToolTip + (man is int mp ? $"\nMANUAL {mp}% (100% from 85 °C)" : "") + "\nRight click: quick adjust";
        }
        quickRefresh?.Invoke();
    }

    /// <summary>Card click: turns off/back on without a dialog (only with the experimental option on; never CPU/pumps).</summary>
    private void ToggleBoardFan(string fan)
    {
        if (ac is not { Available: true }) return;
        switch (ac.Off.ClickAction(fan, app.Settings.BoardFanOff, cpuTemp, gpuTemp))
        {
            case BoardFanClick.TurnOn: ac.TurnOn([fan], "card click"); break;
            case BoardFanClick.TurnOff: if (!ac.TurnOff(fan, cpuTemp, gpuTemp)) offBlocked = (DateTime.UtcNow, "Did not turn off."); break;
            case BoardFanClick.Blocked: offBlocked = (DateTime.UtcNow, $"{FanName(fan)} cannot turn off now: {BoardFanOff.BlockReason(fan, cpuTemp, gpuTemp)}."); break;
            default: return;
        }
        RenderAcRpm();
    }

    /// <summary>Last click that did not turn off (protection): the board row status shows it for a few seconds.</summary>
    private (DateTime At, string Why)? offBlocked;

    /// <summary>Preview only: game mode "on" (automatic by <paramref name="by"/> or by hand) without commanding GPU/board/cooler.</summary>
    internal void PreviewGame(string? by) { gameOn = true; gameAt = DateTime.UtcNow; autoGame.Restore(by != null, by); game.Active = true; RenderStrip(); }

    /// <summary>Preview only: notice of fans turned back on by the protection.</summary>
    internal void PreviewSafety(SafetyEvent e) { if (ac != null) { ac.LastSafety = e; RenderStrip(); } }

    /// <summary>Preview only: temperature card tooltip (session min/max).</summary>
    internal string? TempTip(string id) => tempCards.FirstOrDefault(c => c.Def.Id == id).Card?.ToolTip as string;

    /// <summary>Preview only: marks a fan as off without touching the hardware (AsusFanModes.Fake).</summary>
    internal void PreviewBoardOff(string fan) { ac?.Off.TurnOff(fan, 50, 50); RenderAcRpm(); }

    // ---- Quick card (right click on a fan card): CURVE / MANUAL / OFF and quick %.
    public event Action<string>? ThermalActuatorRequested;
    private Popup? quick;
    private Action? quickRefresh;

    private static CoolerFanSetting CoolerPart(CoolerConfig c, string id) => id == ThermalMap.PumpId ? c.Pump : id == ThermalMap.RadiatorId ? c.Radiator : c.Embedded;

    private void SetCoolerPart(string id, Func<CoolerFanSetting, CoolerFanSetting> f)
    {
        if (cooler == null) return;
        var c = cooler.FanConfig;
        var next = id == ThermalMap.PumpId ? c with { Pump = f(c.Pump) } : id == ThermalMap.RadiatorId ? c with { Radiator = f(c.Radiator) } : c with { Embedded = f(c.Embedded) };
        cooler.SaveFans(next with { Enabled = true });
        cooler.ResumeControl();
    }

    private void OpenQuick(FrameworkElement anchor, string kind, string id)
    {
        if (quick == null)
        {
            quick = new Popup { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.None, VerticalOffset = 2 };
            quick.Closed += (_, _) => quickRefresh = null;
        }
        quick.IsOpen = false;
        quick.PlacementTarget = anchor;
        quick.Child = QuickCard(kind, id);
        quick.IsOpen = true;
    }

    /// <summary>
    /// Quick card content (also used by the preview). Saves only on click/on slider release.
    /// Title = the fan's name; "Rename" turns it into a text box (Enter saves, Esc cancels, empty = original name).
    /// </summary>
    internal Border QuickCard(string kind, string id, bool rename = false)
    {
        var body = new StackPanel { Width = 200 };
        var box = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 8), Child = body, SnapsToDevicePixels = true };
        box.SetResourceReference(Border.BackgroundProperty, Theme.Bg);
        box.SetResourceReference(Border.BorderBrushProperty, Theme.DStroke);
        bool board = kind == "board";
        int min = board ? BoardFanOff.ManualMinPercent : id == ThermalMap.PumpId ? MonitorTray.Ryujin.CoolerSafety.PumpMinPercent : 0;
        int? sliderValue = null;
        bool renaming = rename;
        var part = board ? null : FanNames.CoolerPart(id);
        string Original() => board ? FanNames.BoardHeader(id) : FanNames.CoolerHeader(part!);
        string? Custom() => board ? FanNames.Custom(app.Settings.AcFanNames, id) : FanNames.Custom(app.Settings.CoolerNames, part!);
        void SaveName(string v) => app.Update(x => board ? x with { AcFanNames = FanNames.Rename(x.AcFanNames, id, v, Original()) }
                                                          : x with { CoolerNames = FanNames.Rename(x.CoolerNames, part!, v, Original()) });

        void Manual(int v)
        {
            v = Math.Clamp(v, min, 100);
            if (board) { if (ac == null || !ac.SetManual(id, v)) offBlocked = (DateTime.UtcNow, ac?.Error ?? "Not applied."); }
            else SetCoolerPart(id, s => s with { Mode = CoolerFanMode.Fixed, FixedPercent = v });
            sliderValue = null;
            RenderAfterQuick();
        }
        void Curve()
        {
            if (board) { if (ac == null) return; if (ac.Off.IsOff(id)) ac.TurnOn([id], "quick card"); else ac.ClearManual(id); }
            else SetCoolerPart(id, s => s with { Mode = CoolerFanMode.Smart });
            RenderAfterQuick();
        }
        void Off()
        {
            if (ac == null) return;
            if (!ac.TurnOff(id, cpuTemp, gpuTemp)) offBlocked = (DateTime.UtcNow, $"{FanName(id)} cannot turn off now: {BoardFanOff.BlockReason(id, cpuTemp, gpuTemp) ?? "refused"}.");
            RenderAfterQuick();
        }

        void Render()
        {
            body.Children.Clear();
            int? rpm, pct, manual; bool isOff = false, canOff = false; string? block; string note;
            if (board)
            {
                var rd = BoardFanMap.Read(id, mbRpm, mbCtl);
                rpm = rd.Rpm; pct = rd.Percent; manual = ac?.Off.ManualPercent(id); isOff = ac?.Off.IsOff(id) == true;
                canOff = app.Settings.BoardFanOff && !BoardFanOff.Locked(id);
                block = ac is not { Available: true } ? "Armoury Crate unavailable" : ac.Pending != null ? "applying a mode…" : null;
                note = $"MANUAL: stays at the % up to {BoardFanOff.CurveOffUntilC} °C and goes to 100% from {BoardFanOff.CurveFullFromC} °C (on the chip). Back to the curve when Marz quits.";
            }
            else
            {
                var r = cooler?.Latest; var c = cooler!.FanConfig; var part = CoolerPart(c, id);
                rpm = id == ThermalMap.PumpId ? r?.Pump?.PumpRpm : id == ThermalMap.EmbeddedId ? r?.Pump?.EmbeddedFanRpm : r?.Fans?.Rpm is { Length: > 0 } rr ? (int)rr.Average() : null;
                pct = id == ThermalMap.PumpId ? r?.PumpPercent : id == ThermalMap.EmbeddedId ? r?.EmbeddedPercent : r?.Fans?.DutyPercent;
                manual = c.Enabled && part.Mode == CoolerFanMode.Fixed ? part.FixedPercent : null;
                block = cooler is not { DeviceFound: true } ? "cooler not found" : cooler.Tuning ? "auto tuning in progress" : null;
                note = id == ThermalMap.RadiatorId ? "The 3 radiator fans run together (cooler limitation)."
                    : id == ThermalMap.PumpId ? $"Pump never below {min}%. Liquid ≥ 50 °C puts everything at 100%."
                    : "Fan embedded in the pump.";
            }
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var live = Ui.T(isOff ? "OFF" : $"{(rpm is int x ? x + " rpm · " : "")}{(pct is int y ? y + "%" : "—")}", 10, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
            DockPanel.SetDock(live, Dock.Right); head.Children.Add(live);
            if (renaming)
            {
                bool done = false;
                var input = NameBox(Custom() ?? "", Original(), out var nameBox);
                nameBox.Margin = new Thickness(0, 0, 6, 0);
                void Finish(bool save) { if (done) return; done = true; renaming = false; if (save) SaveName(input.Text); Render(); }
                input.KeyDown += (_, e) =>
                {
                    if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Finish(true); }
                    else if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Finish(false); }
                };
                input.LostKeyboardFocus += (_, _) => Finish(true);
                head.Children.Add(nameBox);
                Dispatcher.BeginInvoke(() => { input.Focus(); input.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
            }
            else head.Children.Add(Ui.T(Custom() ?? Original(), 11, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); t.ToolTip = board ? app.Settings.BoardFanTip(id) : app.Settings.CoolerTip(part!); }));
            body.Children.Add(head);

            bool enabled = block == null;
            var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var (bc, _) = ModeButton("CURVE", manual == null && !isOff, false); bc.Click += () => { if (enabled) Curve(); };
            var (bm, _) = ModeButton("MANUAL", manual != null, false); bm.Click += () => { if (enabled) Manual(manual ?? pct ?? 50); };
            modes.Children.Add(bc); modes.Children.Add(bm);
            if (canOff) { var (bo, _) = ModeButton("OFF", isOff, false); bo.Click += () => { if (enabled && !isOff) Off(); }; modes.Children.Add(bo); }
            body.Children.Add(modes);

            var val = sliderValue ?? manual ?? Math.Max(pct ?? 50, min);
            var lbl = Ui.T(val + "%", 12, weight: 600, tabular: true).With(t => { t.Width = 38; t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
            var sl = new MiniSlider { Min = min, Max = 100, Step = 5, Width = 156, Value = val, IsEnabled = enabled, Opacity = enabled ? 1 : 0.45 };
            sl.Changed += v => { sliderValue = v; lbl.Text = v + "%"; };
            sl.Committed += v => { if (enabled) Manual(v); };
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { sl, lbl } });

            var quickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var v in new[] { 30, 50, 75, 100 })
            {
                if (v < min) continue;
                var vv = v;
                var (b, _) = ModeButton(v + "%", manual == v, v == 100); b.Click += () => { if (enabled) Manual(vv); };
                quickRow.Children.Add(b);
            }
            body.Children.Add(quickRow);
            body.Children.Add(Ui.T(block ?? note, 10, Theme.Muted, head: false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 6, 0, 0); }));
            var edit = Ui.T("Edit curve…", 10.5, Theme.Text).With(t => { t.Cursor = System.Windows.Input.Cursors.Hand; t.TextDecorations = TextDecorations.Underline; });
            edit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (quick != null) quick.IsOpen = false; ThermalActuatorRequested?.Invoke(board ? ThermalMap.BoardId(id) : id); };
            var ren = Ui.T("Rename", 10.5, Theme.Muted).With(t => { t.Margin = new Thickness(12, 0, 0, 0); t.Cursor = System.Windows.Input.Cursors.Hand; t.TextDecorations = TextDecorations.Underline; t.ToolTip = $"Rename {Original()} (empty = original name)"; });
            ren.MouseLeftButtonUp += (_, e) => { e.Handled = true; renaming = true; Render(); };
            body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { edit, ren } });
        }
        quickRefresh = () => { if (sliderValue == null && !renaming) Render(); };
        Render();
        return box;
    }

    private void RenderAfterQuick() { RenderAcRpm(); RenderCooler(); quickRefresh?.Invoke(); }

    /// <summary>Preview only: MANUAL without touching the hardware.</summary>
    internal void PreviewBoardManual(string fan, int pct) { ac?.Off.SetManual(fan, pct); RenderAcRpm(); }

    /// <summary>BOARD row (buttons) and the two settings rows; rebuilt from Settings.</summary>
    private void RenderAc()
    {
        if (ac == null || !ac.Available)
        {
            acRow.Visibility = acModesSetting.Visibility = acFansSetting.Visibility = boardCardsSetting.Visibility = Visibility.Collapsed;
            return;
        }
        var s = app.Settings;
        var was = acRow.Visibility;
        acRow.Visibility = s.AcModes.Count > 0 || s.PopupBoardFans.Any(ac.Fans.Contains) ? Visibility.Visible : Visibility.Collapsed;
        acModesSetting.Visibility = acFansSetting.Visibility = boardCardsSetting.Visibility = Visibility.Visible;
        acButtons.Children.Clear();
        var current = ac.Pending ?? ac.Mode ?? "curves";
        foreach (var mode in AsusFanModes.AllModes.Where(s.AcModes.Contains))
        {
            var (b, _) = ModeButton(AsusFanModes.Label(mode), mode == current, mode == "full");
            var fans = s.AcFans.Where(ac.Fans.Contains).ToList();
            b.ToolTip = (mode == "curves" ? "Back to your Armoury Crate curves (User profile)" : mode == "full" ? "Fans at 100% (turns the stopped ones back on)" : $"Armoury Crate {AsusFanModes.Label(mode)} profile")
                + "\nFans: " + (fans.Count == 0 ? "none (choose in Settings)" : string.Join(", ", fans.Select(FanName)))
                + (mode != "full" && ac.Off.Any ? "\nThe stopped ones stay off." : "");
            var m = mode;
            b.Click += () => { if (fans.Count > 0) ac.Apply(m, fans); };
            acButtons.Children.Add(b);
        }
        RenderAcRpm();

        acModeChecks.Children.Clear();
        foreach (var mode in AsusFanModes.AllModes)
            acModeChecks.Children.Add(Toggle(AsusFanModes.Label(mode), s.AcModes.Contains(mode), v =>
            {
                var l = AsusFanModes.AllModes.Where(x => x == mode ? v : app.Settings.AcModes.Contains(x)).ToList();
                app.Update(x => x with { AcModes = l });
            }));
        acFanChecks.Children.Clear();
        foreach (var fan in ac.Fans)
            acFanChecks.Children.Add(Toggle(FanName(fan), s.AcFans.Contains(fan), v =>
            {
                var l = ac.Fans.Where(x => x == fan ? v : app.Settings.AcFans.Contains(x)).ToList();
                app.Update(x => x with { AcFans = l });
            }, s.BoardFanTip(fan) + (fan.Contains("Pump") || fan.StartsWith("CPU") ? "\nCareful: pump/CPU" : "")));
        boardCardChecks.Children.Clear();
        foreach (var fan in ac.Fans)
            boardCardChecks.Children.Add(Toggle(FanName(fan), s.PopupBoardFans.Contains(fan), v =>
            {
                var l = ac.Fans.Where(x => x == fan ? v : app.Settings.PopupBoardFans.Contains(x)).ToList();
                app.Update(x => x with { PopupBoardFans = l });
            }, s.BoardFanTip(fan)));
        RenderGameSettings();
        RenderFanNames();
        if (was != acRow.Visibility) LayoutChanged?.Invoke();
    }

    private StackPanel tempChecks = null!;
    private WrapPanel coolerCardChecks = null!;
    private Check15 boardOff = null!;
    private string tempSettingsKey = "";

    /// <summary>Temperature list: enabled ones in the chosen order (▲▼ move), then the disabled ones in catalog order.</summary>
    private void RenderTempSettings()
    {
        var on = app.Settings.PopupTemps;
        var key = string.Join("|", on);
        if (key == tempSettingsKey) return;
        tempSettingsKey = key;
        tempChecks.Children.Clear();
        foreach (var id in PickList.Display(on, TempCards.Ids))
        {
            var def = TempCards.Find(id)!;
            bool enabled = on.Contains(id);
            var c = new Check15 { Value = enabled, VerticalAlignment = VerticalAlignment.Center };
            c.Changed += v =>
            {
                app.Update(x => x with { PopupTemps = PickList.Toggle(x.PopupTemps, id, v, TempCards.MaxCards) });
                if (v && !app.Settings.PopupTemps.Contains(id)) c.Value = false; // already 8: does not turn on
            };
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, ToolTip = def.Tip };
            var moveUp = FlatButton.Icon(Icons.ChevronUp, 18, 11, "Move up"); var moveDown = FlatButton.Icon(Icons.ChevronDown, 18, 11, "Move down");
            int idx = on.ToList().IndexOf(id);
            moveUp.Visibility = enabled && idx > 0 ? Visibility.Visible : Visibility.Hidden;
            moveDown.Visibility = enabled && idx < on.Count - 1 ? Visibility.Visible : Visibility.Hidden;
            moveUp.Click += () => app.Update(x => x with { PopupTemps = PickList.Move(x.PopupTemps, id, -1) });
            moveDown.Click += () => app.Update(x => x with { PopupTemps = PickList.Move(x.PopupTemps, id, +1) });
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, Children = { moveUp, moveDown } };
            DockPanel.SetDock(c, Dock.Left); DockPanel.SetDock(arrows, Dock.Right);
            var name = Ui.T(def.Label, 11, enabled ? Theme.Text : Theme.Muted, weight: 600).With(t => { t.Width = 36; t.Margin = new Thickness(6, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; });
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(c); row.Children.Add(arrows); row.Children.Add(name);
            row.Children.Add(Ui.T(def.Tip, 10, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            tempChecks.Children.Add(row);
        }
    }

    private static readonly (string Id, string Label)[] CoolerCardLabels = [("pump", "PUMP"), ("radiator", "RADIATOR"), ("embedded", "EMB. FAN")];

    private void RenderCoolerSettings()
    {
        coolerCardChecks.Children.Clear();
        foreach (var (id, label) in CoolerCardLabels)
            coolerCardChecks.Children.Add(Toggle(FanNames.Custom(app.Settings.CoolerNames, id)?.ToUpperInvariant() ?? label, app.Settings.PopupCoolerFans.Contains(id), v =>
                app.Update(x => x with { PopupCoolerFans = Settings.CoolerCardIds.Where(y => y == id ? v : x.PopupCoolerFans.Contains(y)).ToList() }), app.Settings.CoolerTip(id)));
    }

    private StackPanel fanNameRows = null!;
    private Border fanNamesSetting = null!;
    private string fanNamesKey = "\0";

    /// <summary>
    /// FAN NAMES list: one row per board fan Armoury Crate exposes (and per cooler part with the RYUJIN II connected).
    /// Original name on the left, the user's name on the right; not rebuilt while a name is being typed.
    /// </summary>
    private void RenderFanNames()
    {
        var s = app.Settings;
        IReadOnlyList<string> board = ac is { Available: true } ? ac.Fans : [];
        IReadOnlyList<string> parts = cooler is { DeviceFound: true } ? FanNames.CoolerParts : [];
        var key = string.Join("|", board.Select(f => f + "=" + FanNames.Custom(s.AcFanNames, f))) + "#" + string.Join("|", parts.Select(p => p + "=" + FanNames.Custom(s.CoolerNames, p)));
        if (key == fanNamesKey || fanNameRows.IsKeyboardFocusWithin) return;
        fanNamesKey = key;
        fanNameRows.Children.Clear();
        foreach (var f in board)
        {
            var fan = f; var original = FanNames.BoardHeader(f);
            fanNameRows.Children.Add(NameRow(original, FanNames.Custom(s.AcFanNames, f), v => app.Update(x => x with { AcFanNames = FanNames.Rename(x.AcFanNames, fan, v, original) })));
        }
        foreach (var p in parts)
        {
            var part = p; var original = FanNames.CoolerHeader(p);
            fanNameRows.Children.Add(NameRow(original, FanNames.Custom(s.CoolerNames, p), v => app.Update(x => x with { CoolerNames = FanNames.Rename(x.CoolerNames, part, v, original) })));
        }
        var vis = fanNameRows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (fanNamesSetting.Visibility != vis) fanNamesSetting.Visibility = vis;
        LayoutChanged?.Invoke();
    }

    /// <summary>A FAN NAMES row: saves on Enter or when the box loses focus; Esc puts the saved name back.</summary>
    private FrameworkElement NameRow(string original, string? custom, Action<string> save)
    {
        var input = NameBox(custom ?? "", original, out var box);
        box.Width = 150;
        input.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) input.Text = custom ?? "";
            if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Escape) { e.Handled = true; System.Windows.Input.Keyboard.ClearFocus(); }
        };
        input.LostKeyboardFocus += (_, _) => { if (FanNames.Clean(input.Text) != (custom ?? "")) save(input.Text); };
        var label = Ui.T(original, 10, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        var row = new DockPanel { Margin = new Thickness(0, 3, 4, 0) };
        DockPanel.SetDock(box, Dock.Right); row.Children.Add(box); row.Children.Add(label);
        return row;
    }

    /// <summary>Name text box in the style of the game .exe box: placeholder = the original name, max 16 characters.</summary>
    private static TextBox NameBox(string text, string placeholder, out Border box)
    {
        var input = new TextBox { Text = text, BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = AppFonts.Body, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null, MaxLength = FanNames.MaxLength };
        input.SetResourceReference(TextBox.ForegroundProperty, Theme.Text);
        input.SetResourceReference(TextBox.CaretBrushProperty, Theme.Text);
        input.SetResourceReference(TextBox.SelectionBrushProperty, Theme.Fill);
        var hint = Ui.T(placeholder, 11, Theme.Muted, head: false).With(t => { t.IsHitTestVisible = false; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(1, 0, 0, 0); });
        void Hint() => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => Hint(); Hint();
        box = Ui.Box(new Thickness(1), new Thickness(6, 0, 6, 0), new Grid { Children = { hint, input } }).With(b => { b.Height = 20; b.Cursor = System.Windows.Input.Cursors.IBeam; });
        box.MouseLeftButtonDown += (_, _) => input.Focus();
        return input;
    }

    private WrapPanel gameChecks = null!, gameFanChecks = null!, gameExeList = null!;
    private Border gameFansSetting = null!;
    private TextBox gameExeInput = null!;
    private Check15 trayTemp = null!, trayAlert = null!;
    private Segmented segTrayFont = null!, segTrayRotate = null!;
    private WrapPanel trayTempPicks = null!;
    private string? trayPicksKey;

    /// <summary>Icon temperature buttons: lit in rotation order (number = position); none lit = the hottest.</summary>
    private void RenderTrayTempPicks()
    {
        var on = app.Settings.TrayTemps;
        var key = string.Join("|", on);
        if (key == trayPicksKey) return;
        trayPicksKey = key;
        trayTempPicks.Children.Clear();
        foreach (var d in TempCards.All)
        {
            var id = d.Id; int pos = on.ToList().IndexOf(id); bool act = pos >= 0;
            var t = Ui.T(act && on.Count > 1 ? $"{pos + 1}·{d.Label}" : d.Label, 9, act ? Theme.Bg : Theme.Muted, weight: 600).With(x => { x.HorizontalAlignment = HorizontalAlignment.Center; x.VerticalAlignment = VerticalAlignment.Center; });
            var b = new FlatButton(Theme.Muted) { Height = 18, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(2, 1, 0, 1), ActiveBg = Theme.Fill, ToolTip = d.Tip }.Content(t);
            b.Active = act;
            b.Click += () => app.Update(x => x with { TrayTemps = PickList.Toggle(x.TrayTemps, id, !x.TrayTemps.Contains(id)) });
            trayTempPicks.Children.Add(b);
        }
    }
    private string gameExesKey = "\0";

    private void AddGameExe()
    {
        if (AutoGame.Clean(gameExeInput.Text) is not string exe) { gameExeInput.SelectAll(); return; }
        if (!app.Settings.GameExes.Contains(exe) && app.Settings.GameExes.Count < 16) app.Update(s => s with { GameExes = [.. s.GameExes, exe] });
        gameExeInput.Text = "";
    }

    /// <summary>Game list: one chip per executable with × to remove.</summary>
    private void RenderGameExes()
    {
        var exes = app.Settings.GameExes;
        var key = string.Join("|", exes);
        if (key == gameExesKey) return;
        gameExesKey = key;
        gameExeList.Children.Clear();
        if (exes.Count == 0)
            gameExeList.Children.Add(Ui.T("no games in the list", 10, Theme.Muted, head: false).With(t => t.Margin = new Thickness(0, 2, 0, 2)));
        foreach (var exe in exes)
        {
            var (x, _) = Icons.Make(Icons.X, 9, Theme.Muted);
            x.Margin = new Thickness(4, 0, 0, 0); x.VerticalAlignment = VerticalAlignment.Center;
            var chip = new FlatButton { Height = 18, Padding = new Thickness(6, 0, 4, 0), Margin = new Thickness(0, 2, 4, 2), BorderThickness = new Thickness(1), ToolTip = $"Remove {exe}" }
                .Content(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.T(exe, 10, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center), x } });
            chip.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            var e = exe;
            chip.Click += () => app.Update(s => s with { GameExes = s.GameExes.Where(y => y != e).ToList() });
            gameExeList.Children.Add(chip);
        }
        LayoutChanged?.Invoke();
    }
    private static readonly (string Id, string Label)[] GameTargetLabels = [("gpu", "GPU"), ("board", "BOARD"), ("pump", "PUMP"), ("radiator", "RADIATOR"), ("embedded", "EMB. FAN")];

    private static Segmented Seg(IEnumerable<(object, string)> o, object sel) { var s = new Segmented(o); s.Select(sel); return s; }

    private string GameSummary()
    {
        var s = app.Settings; var parts = new List<string>();
        if (s.GameTargets.Contains("gpu")) parts.Add($"GPU {s.GameGpuPercent}%");
        if (s.GameTargets.Contains("board")) parts.Add("board FULL");
        var c = GameTargetLabels.Skip(2).Where(x => s.GameTargets.Contains(x.Id)).Select(x => x.Label.ToLowerInvariant()).ToList();
        if (c.Count > 0) parts.Add($"{string.Join("/", c)} {s.GameCoolerPercent}%");
        return parts.Count == 0 ? "nothing selected" : string.Join(" · ", parts);
    }

    /// <summary>What game mode turns on and which board fans are included (Settings).</summary>
    private void RenderGameSettings()
    {
        var s = app.Settings;
        gameChecks.Children.Clear();
        foreach (var (id, label) in GameTargetLabels)
            gameChecks.Children.Add(Toggle(label, s.GameTargets.Contains(id), v =>
            {
                var l = Settings.GameTargetIds.Where(x => x == id ? v : app.Settings.GameTargets.Contains(x)).ToList();
                app.Update(x => x with { GameTargets = l });
                RenderGameSettings();
            }));
        gameFanChecks.Children.Clear();
        gameFansSetting.Visibility = ac is { Available: true } && s.GameTargets.Contains("board") ? Visibility.Visible : Visibility.Collapsed;
        if (ac is { Available: true })
        {
            var chosen = GameBoardFans();
            foreach (var fan in ac.Fans)
                gameFanChecks.Children.Add(Toggle(FanName(fan), chosen.Contains(fan), v =>
                {
                    var cur = GameBoardFans();
                    var l = ac.Fans.Where(x => x == fan ? v : cur.Contains(x)).ToList();
                    app.Update(x => x with { GameBoardFans = l });
                }, s.BoardFanTip(fan) + (fan.Contains("Pump") || fan.StartsWith("CPU") ? "\nPump/CPU at FULL is safe (just more noise)" : "")));
        }
    }

    private static StackPanel Toggle(string label, bool value, Action<bool> changed, string? tip = null)
    {
        var c = new Check15 { Value = value, VerticalAlignment = VerticalAlignment.Center };
        c.Changed += changed;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 2), ToolTip = tip, Background = Brushes.Transparent,
            Children = { c, Ui.T(label, 10, Theme.Muted).With(t => { t.Margin = new Thickness(4, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }) },
        };
    }

    public void UpdateFans(FanState? s)
    {
        RenderGameIcon();
        if (s == null || (s.Reading == null && s.Error != null && s.Error.Contains("NVML") == false && s.Error.Contains("NVIDIA")))
        {
            fanRow.Visibility = Visibility.Collapsed; return;
        }
        var was = fanRow.Visibility;
        fanRow.Visibility = Visibility.Visible;
        var r = s.Reading;
        // Fixed "GPU" title (like BOARD and WC); the model goes in the tooltip. Temperatures are in the card strip.
        if (r != null) gpuName.ToolTip = r.Identity.Name + " · open in Details";
        // Icon: "A" = driver automatic; number = manual preset; "?" = mixed/unknown.
        fanBadge.Text = s.Mode switch { FanMode.Automatic => "A", FanMode.Manual => s.RequestedPercent?.ToString(CultureInfo.InvariantCulture) ?? "M", _ => "?" };
        fanPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, s.Mode == FanMode.Manual ? Theme.UStroke : s.Mode == FanMode.Automatic ? Theme.Fill : Theme.Muted);
        fanSpeeds.Text = s.Error != null && r == null ? "no reading"
            : r == null ? "—" : Formatter.GpuFans(r.Fans.Select(f => (f.SpeedPercent, f.SpeedRpm)));
        var tip = r == null ? s.Error : string.Join("\n", r.Fans.Select(f => $"Fan {f.Index + 1}: {(f.SpeedPercent is uint v ? v + "%" : "—")} · {(f.SpeedRpm is uint rr ? rr + " RPM" : "— RPM")}"))
            + "\n" + Views.FanText.Mode(s);
        fanSpeeds.ToolTip = tip;
        // Spins while the fans spin; faster with a higher %. Still at 0%.
        var speed = r?.Fans.Max(f => (double?)f.SpeedPercent) ?? 0;
        var secs = speed <= 0 ? 0 : Math.Round(Math.Clamp(3.0 - speed / 40.0, 0.5, 3.0), 1);
        if (secs != spinSeconds)
        {
            spinSeconds = secs;
            if (secs == 0 || !Controls.ScrollChart.AnimationsEnabled) { fanSpin.BeginAnimation(RotateTransform.AngleProperty, null); fanSpin.Angle = 0; }
            else fanSpin.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(secs)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var (v, b, t) in fanButtons)
        {
            string? block = v is uint pv ? s.BlockFor(pv, now)
                : !s.CanControl ? s.ReadOnlyReason ?? "Control unavailable." : s.Busy ? "Command in progress." : s.Reading == null ? "No reading." : null;
            b.IsEnabled = block == null; b.Opacity = block == null ? 1 : 0.45;
            b.ToolTip = block ?? s.Error ?? (v is null ? "Hand back to driver automatic" : $"Lock the fans at {v}%");
            bool active = v is null ? s.Mode == FanMode.Automatic : s.Mode == FanMode.Manual && s.RequestedPercent == v;
            b.Active = active;
            t.SetResourceReference(TextBlock.ForegroundProperty, active ? Theme.Bg : Theme.Muted);
        }
        if (was != Visibility.Visible) LayoutChanged?.Invoke();
    }

    /// <summary>Short GPU name (see Formatter.ShortGpuName).</summary>
    internal static string ShortGpuName(string name) => Formatter.ShortGpuName(name);

    public void ApplySettings(Settings s)
    {
        if (ac != null) Dispatcher.BeginInvoke(RenderAc);
        refresh.Text = Formatter.RefreshLabel(s.RefreshMs);
        paletteLabel.Text = "COLOR · " + Theme.Palette(s.Palette).Name.ToUpperInvariant();
        foreach (var (id, box) in swatches) box.SetResourceReference(Border.BorderBrushProperty, id == s.Palette ? Theme.Text : "Transparent");
        segTheme.Select(s.Theme); segRefresh.Select(s.RefreshMs); segHistory.Select(s.History); segScale.Select(s.Scale); segNetUnit.Select(s.NetUnit);
        opacity.Value = s.Opacity; opacityLabel.Text = s.Opacity + "%";
        autostart.Value = s.Autostart; autohide.Value = s.Autohide;
        startHidden.Value = s.StartHidden; closeToTray.Value = s.CloseToTray;
        boardOff.Value = s.BoardFanOff;
        trayTemp.Value = s.TrayTemp; trayAlert.Value = s.TrayAlert; segTrayFont.Select(s.TrayFontSize); segTrayRotate.Select(s.TrayRotateSec); RenderTrayTempPicks();
        RenderGameExes();
        RenderTempSettings(); RenderTemps();
        if (cooler != null) RenderCoolerCards();
        RenderCoolerSettings(); RenderFanNames();
        bool pinned = !s.Autohide;
        pin.Active = pinned;
        pin.ToolTip = pinned ? "Pinned: stays open when clicking outside (click to unpin)" : "Unpinned: hides when clicking outside (click to pin)";
        pinIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        pinIcon.RenderTransform = new RotateTransform(pinned ? 0 : 45);
    }

    public void Update(Snapshot snap)
    {
        var s = snap.Summary;
        cpu.Set(s.CpuLoad, s.CpuPower);
        gpu.Set(s.GpuLoad, s.GpuPower);
        mem.SetMem(s.MemUsed, s.MemTotal);
        rd.Text = Formatter.DiskMBps(s.DiskReadBps); wr.Text = Formatter.DiskMBps(s.DiskWriteBps);
        storageLabel.ToolTip = $"Total R/W: {rd.Text} / {wr.Text} MB/s";
        UpdateVolumes(snap.Volumes);
        cpuTemp = s.CpuTemp; gpuTemp = s.GpuTemp ?? (fanService?.State.Reading?.TemperatureC is uint g ? (float?)g : null);
        TryRestoreOff();
        mbRpm = snap.MbFanRpm; mbCtl = snap.MbFanControl; RenderAcRpm(); RenderGameIcon(); RenderCooler();
        lhmTemps = snap.Temps; tempSources = snap.TempSources; RenderTemps();
        var d = Formatter.Net(s.NetDownBps); var u = Formatter.Net(s.NetUpBps);
        down.Text = d.Value; downU.Text = d.Unit; up.Text = u.Value; upU.Text = u.Unit;
        // The service already delivers the bucketed window (peak per bucket) with 1 extra point on the left for scrolling.
        var n = snap.HistoryPoints;
        var (sd, su) = NiceScale.NetScales(snap.NetDownHistory, snap.NetUpHistory, app.Settings.Scale == "shared");
        chart.Set(snap.NetDownHistory, snap.NetUpHistory, n, sd, su);
        chart.ToolTip = "Network · last " + Formatter.Window(app.Settings.History) + (n < app.Settings.History ? " (peak every " + RateHistoryLabel(app.Settings.History) + ")" : "");
        NetCorner(scaleDown, "↓", $"{Formatter.Net(s.NetDownBps)}", $"{Formatter.Net(sd)}", Theme.DStroke);
        NetCorner(scaleUp, "↑", $"{Formatter.Net(s.NetUpBps)}", $"{Formatter.Net(su)}", Theme.UStroke);
        scaleDown.ToolTip = scaleUp.ToolTip = "current  |  chart scale";
    }

    /// <summary>
    /// Temperature strip: roles chosen in Settings, in order; no reading = card omitted.
    /// LHM first; GPU via NVML when LHM has none; liquid from the cooler.
    /// </summary>
    private void RenderTemps()
    {
        var r = fanService?.State.Reading;
        var values = TempCards.Merge(lhmTemps, cooler?.Latest?.Pump?.WaterTempC, r?.TemperatureC, r?.Telemetry.HotspotC, r?.Telemetry.MemoryTemperatureC);
        Session.Push(values);
        var ids = app.Settings.PopupTemps.Where(values.ContainsKey).ToList();
        var layout = string.Join("|", ids);
        if (layout != tempsLayout)
        {
            tempsLayout = layout; tempCards.Clear(); tempsGrid.Children.Clear();
            tempsGrid.Columns = Math.Max(1, ids.Count);
            foreach (var id in ids)
            {
                var card = new TempCard(TempCards.Find(id)!) { Margin = new Thickness(tempCards.Count == 0 ? 0 : 3, 0, 0, 0) };
                card.Click += () => OpenTemp(card.Def.Id);
                tempCards.Add((card.Def, card)); tempsGrid.Children.Add(card);
            }
            var vis = ids.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (tempsBox.Visibility != vis) tempsBox.Visibility = vis;
            LayoutChanged?.Invoke();
        }
        foreach (var (def, card) in tempCards) card.Set(values[def.Id], Session.Range(def.Id), Target(def.Id));
    }

    /// <summary>Where the click leads: LHM sensor in Details; GPU via NVML = GPU device; liquid = thermal control.</summary>
    private string Target(string id) => id == "liq" ? "thermal control" : tempSources.ContainsKey(id) ? "Details" : id is "gpu" or "hot" or "vram" ? "GPU in Details" : "Details";

    private void OpenTemp(string id)
    {
        if (id == "liq") { ThermalRequested?.Invoke(); return; }
        var category = id is "gpu" or "hot" or "vram" ? "GPU" : id is "cpu" or "coremax" ? "CPU" : id == "ram" ? "Memory" : "Motherboard";
        SensorRequested?.Invoke(tempSources.TryGetValue(id, out var sid) ? sid : null, category);
    }

    private static string RateHistoryLabel(int window) { var b = MonitorTray.Core.History.RateHistory.BucketSeconds(window); return b >= 60 ? b / 60 + " min" : b + " s"; }

    /// <summary>Metric cell (CPU/GPU/MEM): load highlighted; W (or memory %) in the corner. Temperatures live in the cards.</summary>
    private sealed class Metric : StackPanel
    {
        private readonly TextBlock value, unit, secondary, secUnit;
        private readonly Bar bar;
        public Metric(string label, bool cpuGpu)
        {
            var top = new DockPanel();
            var l = Ui.T(label, 11, Theme.Muted);
            secondary = Ui.T("—", 12, weight: 500, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Bottom);
            secUnit = Ui.T(cpuGpu ? " W" : "%", 11, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Bottom);
            var sec = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, Children = { secondary, secUnit } };
            DockPanel.SetDock(sec, Dock.Right);
            top.Children.Add(sec); top.Children.Add(l);
            Children.Add(top);
            value = Ui.T("—", 22, weight: 600, tabular: true);
            value.LineHeight = 22; value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            unit = Ui.T(cpuGpu ? "%" : "/— GB", 12, Theme.Muted).With(t => { t.Margin = new Thickness(cpuGpu ? 2 : 3, 0, 0, 1); t.VerticalAlignment = VerticalAlignment.Bottom; });
            Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0), Children = { value, unit } });
            bar = new Bar { Height = 2, Margin = new Thickness(0, 3, 0, 0) };
            Children.Add(bar);
        }

        public void Set(float? load, float? power)
        {
            value.Text = load is float l ? Math.Round(l, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : Formatter.Dash;
            secondary.Text = power is float p ? Math.Round(p, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) : Formatter.Dash;
            secUnit.Visibility = power is null ? Visibility.Collapsed : Visibility.Visible;
            bar.SetFraction(load is float lf ? lf / 100.0 : double.NaN, false);
        }

        public void SetMem(ulong used, ulong total)
        {
            const double GiB = 1024d * 1024 * 1024;
            if (total == 0) { value.Text = Formatter.Dash; unit.Text = ""; secondary.Text = Formatter.Dash; secUnit.Visibility = Visibility.Collapsed; bar.SetFraction(0, false); return; }
            value.Text = (used / GiB).ToString("0.0", CultureInfo.InvariantCulture);
            unit.Text = "/" + Math.Round(total / GiB).ToString("0", CultureInfo.InvariantCulture) + " GB";
            var f = (double)used / total;
            secondary.Text = Math.Round(f * 100).ToString("0", CultureInfo.InvariantCulture); secUnit.Visibility = Visibility.Visible;
            bar.SetFraction(f, false);
        }
    }

    /// <summary>
    /// Temperature card (smaller than the mock's): label, value, mini bar (full at the limit). Alert color from the protection
    /// limits (TempLimits): near (≥ limit − 5 °C) the value takes color B; at the limit the outline too. Click opens the sensor.
    /// </summary>
    private sealed class TempCard : Border
    {
        public TempCards.Def Def { get; }
        private readonly TextBlock value;
        private readonly Bar bar = new() { Height = 2, Margin = new Thickness(-4, 1, -4, 0) };
        private TempLevel? level;
        private bool pressed;
        public event Action? Click;
        public TempCard(TempCards.Def def)
        {
            Def = def;
            BorderThickness = new Thickness(1); Padding = new Thickness(4, 1, 4, 0); Background = Brushes.Transparent; SnapsToDevicePixels = true;
            Cursor = System.Windows.Input.Cursors.Hand;
            ToolTipService.SetShowDuration(this, 20000);
            var l = Ui.T(def.Label, 9, Theme.Muted).With(t => { t.LineHeight = 10; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.TextTrimming = TextTrimming.None; });
            value = Ui.T("—", 14, weight: 600, tabular: true).With(t => { t.LineHeight = 16; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.TextTrimming = TextTrimming.None; });
            Child = new StackPanel { Children = { l, value, bar } };
            SetLevel(TempLevel.Normal);
        }

        /// <param name="range">Min and max since the app started.</param>
        /// <param name="target">Where the click leads (tooltip text).</param>
        public void Set(float v, (float Min, float Max)? range, string target)
        {
            var text = Def.Id == "liq" ? v.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
            if (value.Inlines.FirstInline is not System.Windows.Documents.Run n || n.Text != text)
            {
                value.Inlines.Clear();
                value.Inlines.Add(new System.Windows.Documents.Run(text));
                var deg = new System.Windows.Documents.Run("°") { FontSize = 10, FontFamily = AppFonts.For(true, 400) };
                deg.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, Theme.Muted);
                value.Inlines.Add(deg);
            }
            var tip = Tip(Def, v, range, target);
            if (!Equals(ToolTip, tip)) ToolTip = tip;
            var lv = TempCards.Level(Def, v);
            SetLevel(lv);
            bar.SetFraction(v / Def.LimitC, lv != TempLevel.Normal);
        }

        internal static string Tip(TempCards.Def d, float v, (float Min, float Max)? range, string target)
        {
            static string F(float x) => x.ToString("0.0", CultureInfo.InvariantCulture);
            return $"{d.Tip}: {F(v)} °C"
                + (range is { } r ? $"\nSession: min {F(r.Min)} · max {F(r.Max)} °C" : "")
                + $"\nAlert: near from {d.LimitC - TempLimits.NearMarginC:0} °C, limit {d.LimitC:0} °C"
                + $"\nClick: open in {target}";
        }

        private void SetLevel(TempLevel l)
        {
            if (level == l) return;
            level = l;
            SetResourceReference(BorderBrushProperty, l == TempLevel.Over ? Theme.UStroke : Theme.Line);
            value.SetResourceReference(TextBlock.ForegroundProperty, l == TempLevel.Normal ? Theme.Text : Theme.UStroke);
        }

        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e) { pressed = true; CaptureMouse(); e.Handled = true; }
        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!pressed) return;
            var was = IsMouseOver; pressed = false; ReleaseMouseCapture(); e.Handled = true;
            if (was) Click?.Invoke();
        }
    }

    /// <summary>
    /// Standard-size fan card (board and cooler): short name (+ lock) with the % on the right; rpm below, mini bar.
    /// No measured rpm: "—" instead of the rpm (the % stays; rpm is never estimated). Off: gray, "OFF" and the read rpm/%.
    /// Clickable only when it can be turned off.
    /// </summary>
    private sealed class FanCard : Border
    {
        private readonly TextBlock value, unit, pct;
        private readonly Bar bar = new() { Height = 2, Margin = new Thickness(-4, 1, -4, 0) };
        private bool clickable, pressed;
        private bool? off;
        private bool manual;
        public event Action? Click;
        /// <summary>Right click: opens the quick card (quick adjust).</summary>
        public event Action? RightClick;

        /// <summary>MANUAL: outline in the accent color.</summary>
        public void SetManual(bool m)
        {
            if (manual == m) return;
            manual = m;
            SetResourceReference(BorderBrushProperty, m ? Theme.DStroke : Theme.Line);
        }

        protected override void OnMouseRightButtonUp(System.Windows.Input.MouseButtonEventArgs e) { if (RightClick == null) return; e.Handled = true; RightClick(); }

        public FanCard(string name, bool locked)
        {
            BorderThickness = new Thickness(1); Padding = new Thickness(4, 1, 4, 0); Background = Brushes.Transparent; SnapsToDevicePixels = true;
            var n = Ui.T(name, 9, Theme.Muted).With(t => { t.LineHeight = 11; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.VerticalAlignment = VerticalAlignment.Center; });
            pct = Ui.T("", 9, Theme.Text, weight: 600, tabular: true).With(t => { t.LineHeight = 11; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(4, 0, 0, 0); t.TextTrimming = TextTrimming.None; });
            var head = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(pct, Dock.Right); head.Children.Add(pct);
            if (locked)
            {
                var (lk, _) = Icons.Make(Icons.Lock, 8, Theme.Muted);
                lk.Margin = new Thickness(2, 0, 0, 1); lk.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(lk, Dock.Right); head.Children.Add(lk);
            }
            head.Children.Add(n); // a long name is cut with an ellipsis (full name in the card tooltip)
            value = Ui.T("—", 12, weight: 600, tabular: true).With(t => { t.LineHeight = 14; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.TextTrimming = TextTrimming.None; t.VerticalAlignment = VerticalAlignment.Bottom; });
            unit = Ui.T(" rpm", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(0, 0, 0, 1); t.TextTrimming = TextTrimming.None; });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { value, unit } };
            Child = new StackPanel { Children = { head, row, bar } };
            SetOff(false);
        }

        /// <summary>false = read-only: normal (inherited) cursor and the click passes through.</summary>
        public bool Clickable { get => clickable; set { clickable = value; Cursor = value ? System.Windows.Input.Cursors.Hand : null; } }

        /// <param name="rpm">measured rpm; null = not measured ("—", no unit).</param>
        public void Set(int? rpm, int? percent, bool isOff)
        {
            value.Text = isOff ? "OFF" : rpm is int r ? r.ToString(CultureInfo.InvariantCulture) : Formatter.Dash;
            unit.Text = rpm is int ru ? (isOff ? $" {ru} rpm" : " rpm") : "";
            pct.Text = percent is int p ? p + "%" : "";
            SetOff(isOff);
            bar.SetFraction(isOff ? 0 : percent is int q ? q / 100.0 : double.NaN, percent >= 85);
        }

        private void SetOff(bool o)
        {
            if (off == o) return;
            off = o;
            // Off: dimmed background and outline, texts in Muted.
            if (o) SetResourceReference(BackgroundProperty, Theme.Track); else Background = Brushes.Transparent;
            SetResourceReference(BorderBrushProperty, manual ? Theme.DStroke : Theme.Line);
            value.SetResourceReference(TextBlock.ForegroundProperty, o ? Theme.Muted : Theme.Text);
            pct.SetResourceReference(TextBlock.ForegroundProperty, o ? Theme.Muted : Theme.Text);
            bar.Opacity = o ? 0.4 : 1;
        }

        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e) { if (!clickable) return; pressed = true; CaptureMouse(); e.Handled = true; }
        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!pressed) return;
            var was = IsMouseOver; pressed = false; ReleaseMouseCapture(); e.Handled = true;
            if (was && clickable) Click?.Invoke();
        }
    }
}
