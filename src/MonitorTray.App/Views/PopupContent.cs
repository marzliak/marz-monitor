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

/// <summary>Conteúdo do popup (344 px): cabeçalho + Resumo ou Configurações (docs/04 §B).</summary>
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
    /// <summary>Mínimo/máximo da sessão por papel (o App passa o mesmo que alimenta a bandeja).</summary>
    public TempSession Session { get; set; } = new();
    private IReadOnlyDictionary<string, string> tempSources = new Dictionary<string, string>();
    /// <summary>Clique num card de temperatura: id do sensor do LHM (null sem LHM) e a categoria do Detalhes para cair na falta dele.</summary>
    public event Action<string?, string>? SensorRequested;
    private readonly Border strip;
    private readonly TextBlock stripText;

    /// <summary>Ventoinhas da placa que entram no modo game (Configurações; padrão = todas).</summary>
    private List<string> GameBoardFans() => ac == null ? [] : (app.Settings.GameBoardFans ?? ac.Fans).Where(ac.Fans.Contains).ToList();

    /// <summary>
    /// Modo game (configurável): GPU no % escolhido, placa em FULL, bomba/radiador/fan embutido do cooler em % fixo.
    /// Desligar devolve cada um ao que estava (GPU auto, placa CURVAS, cooler na config anterior).
    /// </summary>
    /// <summary>Liga/desliga o modo game de fora (mensagem "game" da instância única: `MonitorTray.exe --game`).</summary>
    public void ToggleGameExternal() => ToggleGame();
    public bool GameOn => gameOn;

    /// <summary>Estado do modo game em disco (sobrevive a reinício/atualização do app): ligado + config do cooler de antes.</summary>
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
    /// Ao abrir: se o modo game estava ligado, volta ligado. Reaplica GPU e placa (a GPU volta ao automático quando o app fecha)
    /// e recupera a config do cooler de antes, para o segundo clique devolver tudo ao normal.
    /// </summary>
    public void RestoreGame()
    {
        try
        {
            if (!System.IO.File.Exists(GameStatePath)) return;
            var o = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(GameStatePath)) as System.Text.Json.Nodes.JsonObject;
            if (o?["on"]?.GetValue<bool>() != true) return;
            if (o["coolerBefore"]?.GetValue<string>() is string cb) coolerBeforeGame = CoolerConfig.Parse(cb);
            autoGame.Restore(o["auto"]?.GetValue<bool>() == true, o["by"]?.GetValue<string>()); // ligado pelo jogo: desliga quando ele fechar
            gameOn = true; gameAt = DateTime.UtcNow;
            var s = app.Settings; var t = s.GameTargets;
            // O Armoury Crate (COM) demora a subir junto com o app: a placa vai a FULL quando ele ficar disponível.
            if (t.Contains("board")) { pendingBoardRestore = true; TryRestoreBoard(); }
            if (t.Contains("gpu") && fanService != null) _ = Task.Run(async () => { try { await Task.Delay(3000); await fanService.CommandAsync((uint)s.GameGpuPercent); } catch (Exception) { } });
            // O cooler já está salvo em FULL/% fixo no ryujin-fans.json: o serviço reaplica sozinho.
            RenderCooler(); RenderGameIcon();
        }
        catch (Exception) { }
    }

    private bool pendingBoardRestore;

    // ---- Escolhas de fan guardadas (FanChoices): reaplicadas ao abrir e ao sair do modo game.
    private FanChoices.Data? pendingChoices;
    private bool restoreStartup;
    private IReadOnlyCollection<string>? pendingChoicesFans;
    private List<string> pendingOff = [];
    private DateTime pendingOffUntil;

    /// <summary>Ao abrir o app (fora do modo game): placa, OFF/MANUAL e GPU como você deixou.</summary>
    public void RestoreChoices() { if (!gameOn) BeginRestoreChoices(gpuDelayMs: 3000, boardFans: null, gpu: true, startup: true); }

    /// <param name="boardFans">Fans que voltam ao modo guardado (null = os das Configurações).</param>
    /// <param name="startup">Ao abrir: nada escolhido = não mexe (o app já sobe no automático/curvas).</param>
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

    /// <summary>Placa: espera o Armoury Crate (COM) ficar disponível. OFF espera a primeira leitura da CPU (regra de segurança).</summary>
    private void TryRestoreChoices()
    {
        if (pendingChoices is not { } d || ac is not { Available: true }) return;
        pendingChoices = null;
        var fans = pendingChoicesFans ?? app.Settings.AcFans.Where(ac.Fans.Contains).ToList();
        if (fans.Count > 0 && !(restoreStartup && d.BoardMode is null or "curvas")) ac.Apply(d.BoardMode ?? "curvas", fans, remember: false);
        foreach (var (f, pct) in d.Manual) if (ac.Fans.Contains(f)) ac.SetManual(f, pct);
        pendingOff = d.Off.Where(f => ac.Fans.Contains(f) && !d.Manual.ContainsKey(f)).ToList();
        pendingOffUntil = DateTime.UtcNow.AddMinutes(2);
        TryRestoreOff();
    }

    private void TryRestoreOff()
    {
        if (pendingOff.Count == 0 || ac is not { Available: true } || !app.Settings.BoardFanOff) { if (!app.Settings.BoardFanOff) pendingOff.Clear(); return; }
        if (cpuTemp == null && DateTime.UtcNow < pendingOffUntil) return; // sem leitura da CPU o OFF é recusado: espera
        foreach (var f in pendingOff) ac.TurnOff(f, cpuTemp, gpuTemp);
        pendingOff.Clear();
    }
    private bool shuttingDown;
    /// <summary>Saída do app: a GPU volta ao automático e o ícone apagaria sozinho; o estado salvo do modo game não pode ser apagado.</summary>
    public void BeginShutdown() => shuttingDown = true;

    private void TryRestoreBoard()
    {
        if (!pendingBoardRestore || ac is not { Available: true }) return;
        var fans = GameBoardFans();
        if (fans.Count > 0) ac.Apply("full", fans, remember: false);
        pendingBoardRestore = false; gameAt = DateTime.UtcNow; // nova carência para o FULL confirmar
    }

    /// <summary>Clique no botão (ou `--game`): o estado passa a ser do usuário; o automático não desliga o que ele ligou.</summary>
    private void ToggleGame()
    {
        bool on = !gameOn;
        autoGame.ManualToggle(on, AutoGame.Match(lastRunning, app.Settings.GameExes) != null);
        SetGame(on);
    }

    /// <summary>Modo game automático: o App passa os processos rodando a cada ~3 s (só com jogos na lista).</summary>
    public void AutoGameStep(IReadOnlyList<string> running)
    {
        lastRunning = running;
        var act = autoGame.Step(running, app.Settings.GameExes, gameOn);
        if (act == AutoGameAction.TurnOn) SetGame(true);
        else if (act == AutoGameAction.TurnOff) SetGame(false);
        RenderStrip();
    }

    /// <summary>O automático ligou o modo game e ainda vai desligar (o App segue consultando mesmo com a lista vazia).</summary>
    public bool AutoGameOwned => autoGame.Owned;

    private void SetGame(bool on)
    {
        if (on == gameOn) return;
        gameOn = on; gameAt = DateTime.UtcNow; pendingBoardRestore = false;
        var s = app.Settings; var t = s.GameTargets;
        if (t.Contains("board") && ac is { Available: true })
        {
            var fans = GameBoardFans();
            if (fans.Count > 0 && gameOn) ac.Apply("full", fans, remember: false); // FULL também religa os fans desligados
        }
        if (gameOn && ac is { Available: true } && ac.Off.Any) ac.TurnOn(ac.Off.Off, "modo game", remember: false); // game religa todos, mesmo sem a placa no game
        if (t.Contains("gpu") && fanService != null && gameOn) _ = Task.Run(async () => { try { await fanService.CommandAsync((uint)s.GameGpuPercent); } catch (Exception) { } });
        if (!gameOn) BeginRestoreChoices(gpuDelayMs: 0, boardFans: t.Contains("board") ? GameBoardFans() : null, gpu: t.Contains("gpu")); // volta ao que você tinha escolhido
        bool anyCooler = t.Contains("pump") || t.Contains("radiator") || t.Contains("embedded");
        if (anyCooler && cooler is { DeviceFound: true })
        {
            var c = cooler.FanConfig;
            if (gameOn)
            {
                coolerBeforeGame = c;
                // 100% = preset FULL (a linha WC RYUJIN II acende FULL); 70/85% = % fixo.
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
    /// Faixa fina do topo: proteções que agiram (fans religados, GPU/cooler em 100%) e o modo game ativo.
    /// Some quando não há nada. Clique dispensa o aviso de fans religados.
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
            GpuProtection = fs?.SafetyNotice is string n && n.StartsWith("PROTECAO", StringComparison.Ordinal) ? n : null,
            CoolerPanicC = cs is { Controlling: true, Notice: string cn } && cn.StartsWith("água", StringComparison.Ordinal) ? cooler?.Latest?.Pump?.WaterTempC : null,
        };
        var items = StatusStrip.Items(state, DateTimeOffset.Now, FanShort);
        var text = string.Join("   |   ", items.Select(i => i.Text));
        if (stripText.Text != text) stripText.Text = text;
        strip.ToolTip = items.Count == 0 ? null : string.Join("\n", items.Select(i => i.Tip));
        strip.Cursor = state.BoardSafety != null ? System.Windows.Input.Cursors.Hand : null;
        var vis = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (strip.Visibility != vis) { strip.Visibility = vis; LayoutChanged?.Invoke(); }
    }

    // ---- Water cooler (ROG RYUJIN II): presets de bomba + radiador e rotações, na linha abaixo da MOTHERBOARD.
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
        // Fan embutido preso em FULL fora do modo game (game antigo que não devolveu o cooler): volta ao STD uma vez.
        if (!gameOn && !System.IO.File.Exists(GameStatePath) && r.UnstickEmbedded()) r.ResumeControl();
        r.FanChanged += _ => Dispatcher.BeginInvoke(RenderCooler);
        RenderCooler();
    }

    /// <summary>null = CURVAS (a config própria do cooler: curvas/fixo por parte).</summary>
    private static readonly (RyujinCurves.Preset? P, string Label)[] CoolerPresets =
        [(null, "CURVAS"), (RyujinCurves.Preset.Silent, "SILENT"), (RyujinCurves.Preset.Standard, "STD"), (RyujinCurves.Preset.Turbo, "TURBO"), (RyujinCurves.Preset.FullSpeed, "FULL")];

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
            // CURVAS aplica a config própria (a de antes do preset, ou as curvas padrão); a janela do controle térmico abre pelo título.
            b.ToolTip = pp is RyujinCurves.Preset ? $"Bomba e radiador do water cooler em {label}" : "Bomba e radiador nas curvas do cooler (as de antes do preset)";
            if (!c.Enabled) b.ToolTip += "\n(liga o controle do cooler pelo Marz)";
            b.Click += () =>
            {
                if (pp is RyujinCurves.Preset preset) cooler.ApplyPreset(preset, enable: true);
                else cooler.ApplyCurves();
                cooler.ResumeControl();
                RenderCooler();
            };
            coolerButtons.Children.Add(b);
        }
        coolerStatus.Text = !c.Enabled ? "só leitura" : fs.TuneProgress is double tp ? $"auto tuning {(int)Math.Round(tp * 11) + 1}/11" : fs.Notice != null ? "aviso" : "";
        coolerStatus.ToolTip = fs.Notice;
        RenderCoolerCards();
        RenderStrip();
    }

    /// <summary>Cards do cooler escolhidos nas Configurações: bomba, um por fan do radiador, fan embutido.</summary>
    private void RenderCoolerCards()
    {
        if (cooler == null) return;
        var r = cooler.Latest;
        var sel = app.Settings.PopupCoolerFans;
        var items = new List<(string Name, string Tip, int? Rpm, int? Pct, string Id)>();
        if (sel.Contains("pump")) items.Add(("BOMBA", "Bomba do water cooler", r?.Pump?.PumpRpm, r?.PumpPercent, ThermalMap.PumpId));
        if (sel.Contains("radiator"))
        {
            var rpm = r?.Fans?.Rpm ?? [];
            if (rpm.Length == 0) items.Add(("RADIADOR", "Fans do radiador", null, r?.Fans?.DutyPercent, ThermalMap.RadiatorId));
            for (int i = 0; i < rpm.Length; i++) items.Add(($"RAD {i + 1}", $"Radiador · fan {i + 1}", rpm[i], r!.Fans!.DutyPercent, ThermalMap.RadiatorId));
        }
        if (sel.Contains("embedded")) items.Add(("EMB.", "Fan embutido na bomba", r?.Pump?.EmbeddedFanRpm, r?.EmbeddedPercent, ThermalMap.EmbeddedId));
        var layout = string.Join("|", items.Select(x => x.Name));
        if (layout != coolerLayout)
        {
            coolerLayout = layout; coolerCards.Clear(); coolerGrid.Children.Clear();
            coolerGrid.Columns = FanCols;
            coolerGrid.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var (name, tip, _, _, id) in items)
            {
                var card = new FanCard(name, locked: false) { ToolTip = tip + "\nBotão direito: ajuste rápido", Margin = CardMargin(coolerCards.Count) };
                var aid = id; var title = tip;
                card.RightClick += () => OpenQuick(card, "cooler", aid, title);
                coolerCards.Add(card); coolerGrid.Children.Add(card);
            }
            LayoutChanged?.Invoke();
        }
        var cfg = cooler.FanConfig;
        for (int i = 0; i < items.Count; i++) { coolerCards[i].Set(items[i].Rpm, items[i].Pct, isOff: false); coolerCards[i].SetManual(cfg.Enabled && CoolerPart(cfg, items[i].Id).Mode == CoolerFanMode.Fixed); }
        quickRefresh?.Invoke();
    }

    /// <summary>Cards de fan (placa e cooler) no mesmo grid de 5 colunas: mesmo tamanho nas duas seções; mais de 5 quebra a linha.</summary>
    private const int FanCols = 5;
    private static Thickness CardMargin(int i) => new(i % FanCols == 0 ? 0 : 3, i >= FanCols ? 3 : 0, 0, 0);

    /// <summary>Botão de modo/preset (placa, cooler e FAN SPEED da GPU): 9 px SemiBold, altura 18, padding 4, largura natural.</summary>
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
        // Ativo se o pedido está ligado; se o usuário mudou as fans por fora (AUTO, CURVAS), o ícone apaga.
        var gpuFull = !app.Settings.GameTargets.Contains("gpu") || fanService?.State is { Mode: FanMode.Manual } gs && gs.RequestedPercent == (uint)app.Settings.GameGpuPercent;
        var acFull = !app.Settings.GameTargets.Contains("board") || ac is not { Available: true } || (ac.Pending ?? ac.Mode) == "full";
        TryRestoreBoard();
        bool settled = !pendingBoardRestore && (DateTime.UtcNow - gameAt).TotalSeconds > 8; // dá tempo à GPU e ao Armoury Crate confirmarem
        if (gameOn && settled && fanService?.State is { Busy: false } st && st.Mode != FanMode.Unknown && !gpuFull && !(ac?.Pending != null)) gameOn = false;
        if (gameOn && settled && ac is { Available: true, Pending: null } && !acFull) gameOn = false;
        if (gameOn && settled && coolerBeforeGame != null && cooler?.FanConfig is { } cc && !cc.Enabled) gameOn = false;
        // Game apagou sozinho (GPU/placa mudadas por fora): o cooler também volta ao que era antes, senão fica em FULL.
        if (!gameOn && coolerBeforeGame != null && !shuttingDown && cooler != null)
        {
            var cur = cooler.FanConfig;
            cooler.SaveFans(MonitorTray.Ryujin.CoolerModes.UndoGame(cur, coolerBeforeGame, app.Settings.GameCoolerPercent) with { Enabled = cur.Enabled });
            coolerBeforeGame = null;
        }
        if (game.Active && !gameOn && !shuttingDown) SaveGameState();
        game.Active = gameOn;
        game.ToolTip = gameOn ? "Modo game LIGADO" + (autoGame.Owned ? $" (automático: {autoGame.Game})" : "") + " — clique para voltar ao normal"
            : "Modo game: " + GameSummary() + "\n(configurável nas Configurações)";
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
    /// <summary>Cabeçalho: arrastar move o popup; duplo clique devolve para cima do relógio.</summary>
    public Border Header { get; }
    public event Action? CloseRequested, DetailsRequested, LayoutChanged, AboutRequested, QuitRequested;
    private readonly Check15 startHidden, closeToTray;

    public PopupContent(AppState app)
    {
        this.app = app;
        Width = 344;
        // ---- B1 cabeçalho
        var header = new DockPanel { LastChildFill = false };
        var hb = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 4, 6, 4), header);
        hb.Background = Brushes.Transparent; // área de arrasto
        Header = hb;
        back = FlatButton.Icon(Icons.ChevronLeft, 20, 13, "Voltar");
        back.Margin = new Thickness(-4, 0, 6, 0);
        back.Click += () => ShowSettings(false);
        glyph = Ui.Glyph(6).With(g => g.Margin = new Thickness(0, 0, 6, 0));
        title = Ui.T("MARZ MONITOR", 12, weight: 600);
        title.VerticalAlignment = VerticalAlignment.Center;
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { back, glyph, title } };
        DockPanel.SetDock(left, Dock.Left);
        header.Children.Add(left);
        refresh = Ui.T("1s", 11, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        var refreshBtn = new FlatButton { Height = 22, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 2, 0), ToolTip = "Taxa de atualização (clique para trocar: 0,5s · 1s · 2s)" }.Content(refresh);
        refreshBtn.Click += () => app.Update(s => s with { RefreshMs = s.RefreshMs switch { 500 => 1000, 1000 => 2000, _ => 500 } });
        var (dIcon, _) = Icons.Make(Icons.ExternalLink, 12, Theme.Muted);
        dIcon.HorizontalAlignment = HorizontalAlignment.Center; dIcon.VerticalAlignment = VerticalAlignment.Center;
        details = new FlatButton { Width = 22, Height = 22, ToolTip = "Abrir detalhes" }.Content(dIcon);
        details.Click += () => DetailsRequested?.Invoke();
        gear = FlatButton.Icon(Icons.Settings, 22, 13, "Configurações");
        gear.Margin = new Thickness(2, 0, 0, 0);
        gear.Click += () => ShowSettings(settings!.Visibility != Visibility.Visible);
        // Pin: fixado = popup fica aberto ao clicar fora; solto = some ao perder o foco. Mesmo ajuste de "Fechar ao perder foco".
        pin = FlatButton.Icon(Icons.Pin, 22, 12, "");
        pin.Margin = new Thickness(0, 0, 2, 0);
        pinIcon = (FrameworkElement)pin.Child;
        pin.Click += () => app.Update(s => s with { Autohide = !s.Autohide });
        var close = FlatButton.Icon(Icons.X, 22, 12, "Fechar");
        close.Margin = new Thickness(2, 0, 0, 0);
        close.Click += () => { if (app.Settings.CloseToTray) CloseRequested?.Invoke(); else QuitRequested?.Invoke(); };
        // Modo game: todas as ventoinhas a 100% (GPU + placa via Armoury Crate). Clique de novo volta ao automático/curvas.
        game = FlatButton.Icon(Icons.Gamepad, 22, 14, "Modo game: todas as ventoinhas a 100%");
        game.Margin = new Thickness(0, 0, 2, 0); game.ActiveBg = Theme.UStroke;
        game.Click += ToggleGame;
        var thermal = FlatButton.Icon(Icons.Thermometer, 22, 14, "Controle térmico: water cooler, fans da placa e curvas");
        thermal.Margin = new Thickness(0, 0, 2, 0);
        thermal.Click += () => ThermalRequested?.Invoke();
        var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { thermal, game, refreshBtn, pin, details, gear, close } };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        Children.Add(hb);

        // ---- Faixa de automáticos (proteções que agiram, modo game): fina, some quando não há nada.
        stripText = Ui.T("", 10, weight: 600, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis; });
        var stripTint = new Border { Opacity = 0.16 };
        stripTint.SetResourceReference(Border.BackgroundProperty, Theme.UStroke);
        var stripEdge = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left };
        stripEdge.SetResourceReference(Border.BackgroundProperty, Theme.UStroke);
        strip = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), new Grid { Children = { stripTint, stripEdge, new Border { Padding = new Thickness(10, 2, 10, 3), Child = stripText } } });
        strip.Visibility = Visibility.Collapsed;
        strip.MouseLeftButtonUp += (_, _) => { if (ac?.LastSafety != null) { ac.LastSafety = null; RenderStrip(); } };
        Children.Add(strip);

        // ---- B2 métricas
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

        // ---- B3 disco
        (rd, _) = (Ui.T("0.0", 16, weight: 600, tabular: true), 0);
        (wr, _) = (Ui.T("0.0", 16, weight: 600, tabular: true), 0);
        // Leitura e escrita numa caixinha só: "L/E  2.1 / 0.8 MB/s".
        var slash = Ui.T("/", 13, Theme.Muted).With(t => { t.Margin = new Thickness(4, 0, 4, 0); t.VerticalAlignment = VerticalAlignment.Center; });
        rd.VerticalAlignment = VerticalAlignment.Center; wr.VerticalAlignment = VerticalAlignment.Center;
        var le = new StackPanel { Orientation = Orientation.Horizontal, Children =
        {
            Ui.T("L/E", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); }),
            rd, slash, wr,
            Ui.T("MB/s", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(4, 3, 0, 0); }),
        } };
        _ = le; // total L/E saiu do popup: cada unidade mostra o seu (tooltip do STORAGE traz o total)
        // STORAGE pequeno à esquerda e as unidades na mesma linha (letra, %, L/E e barra de uso).
        storageLabel = Ui.T("STORAGE", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); t.Opacity = 0.85; t.Background = Brushes.Transparent; });
        volumesPanel = new UniformGrid { Rows = 1 };
        var sd = new DockPanel();
        DockPanel.SetDock(storageLabel, Dock.Left); sd.Children.Add(storageLabel); sd.Children.Add(volumesPanel);
        volumesBox = Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(9, 4, 5, 4), sd);
        volumesBox.Visibility = Visibility.Collapsed;
        main.Children.Add(volumesBox);

        // ---- B4 rede
        down = Ui.T("0", 16, weight: 600, tabular: true); downU = Ui.T("KB/s", 11, Theme.Muted);
        up = Ui.T("0", 16, weight: 600, tabular: true); upU = Ui.T("KB/s", 11, Theme.Muted);
        var (ai, _) = Icons.Make(Icons.ArrowDown, 10, Theme.DStroke);
        var (ui, _) = Icons.Make(Icons.ArrowUp, 10, Theme.UStroke);
        _ = (ai, ui); // a linha REDE saiu: o valor atual fica no canto do gráfico

        // ---- B5 gráfico espelhado
        chart = new MirrorChart { Height = 58 };
        // Linha própria embaixo do gráfico (nada cobre a curva): REDE, ↓ e ↑ lado a lado no canto esquerdo (atual em destaque | escala).
        scaleDown = Ui.T("", 10, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; });
        scaleUp = Ui.T("", 10, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(12, 0, 0, 0); });
        var netLabel = Ui.T("REDE", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 1, 8, 0); t.Opacity = 0.85; });
        var netNow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 0), Children = { netLabel, scaleDown, scaleUp } };
        var chartBox = Ui.Box(new Thickness(0, 1, 0, 1), new Thickness(0), chart);
        chartBox.Height = 58 + 2;
        main.Children.Add(new StackPanel { Margin = new Thickness(9, 5, 9, 5), Children = { chartBox, netNow } });

        // ---- Temperaturas: cards escolhidos e ordenados nas Configurações; sensor sem leitura = card omitido.
        tempsGrid = new UniformGrid { Rows = 1 };
        tempsBox = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 5), tempsGrid);
        tempsBox.Visibility = Visibility.Collapsed;
        main.Children.Add(tempsBox);

        // ---- GPU: título, FAN SPEED (mesmo botão dos modos) e, à direita, o ícone da fan com o modo (A ou %) e o % de cada fan.
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
        // FAN SPEED na linha do título: presets com o mesmo botão dos modos; bloqueados ficam apagados com o motivo.
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
        gpuName.Cursor = System.Windows.Input.Cursors.Hand; gpuName.ToolTip = "Abrir a GPU no Detalhes";
        gpuName.MouseLeftButtonUp += (_, _) => DetailsRequested?.Invoke();
        fanRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 4), fd);
        fanRow.Visibility = Visibility.Collapsed;
        main.Children.Add(fanRow);

        // ---- Placa (via Armoury Crate): modos escolhidos nas Configurações + cards dos fans escolhidos (clique = OFF, experimental).
        var ad = new DockPanel { LastChildFill = true };
        var al = Ui.T("PLACA-MÃE", 12, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); });
        acButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        acStatus = Ui.T("", 9, Theme.Muted, tabular: true).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; t.TextTrimming = TextTrimming.CharacterEllipsis; t.Background = Brushes.Transparent; });
        DockPanel.SetDock(al, Dock.Left); DockPanel.SetDock(acButtons, Dock.Left);
        ad.Children.Add(al); ad.Children.Add(acButtons); ad.Children.Add(acStatus);
        acRpmGrid = new UniformGrid { Columns = FanCols, Margin = new Thickness(0, 4, 0, 0) };
        acRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 5), new StackPanel { Children = { ad, acRpmGrid } });
        acRow.Visibility = Visibility.Collapsed;
        main.Children.Add(acRow);

        // ---- Water cooler (aparece só com o RYUJIN II conectado): presets + cards escolhidos nas Configurações.
        var cd = new DockPanel { LastChildFill = true };
        var cl = Ui.T("WC RYUJIN II", 12, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); t.Cursor = System.Windows.Input.Cursors.Hand; t.ToolTip = "Abrir o controle térmico"; t.Background = Brushes.Transparent; });
        cl.MouseLeftButtonUp += (_, _) => ThermalRequested?.Invoke();
        coolerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        coolerStatus = Ui.T("", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; t.Background = Brushes.Transparent; });
        DockPanel.SetDock(cl, Dock.Left); DockPanel.SetDock(coolerButtons, Dock.Left);
        cd.Children.Add(cl); cd.Children.Add(coolerButtons); cd.Children.Add(coolerStatus);
        coolerGrid = new UniformGrid { Columns = FanCols, Margin = new Thickness(0, 4, 0, 0) };
        coolerRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 4, 9, 5), new StackPanel { Children = { cd, coolerGrid } });
        coolerRow.Visibility = Visibility.Collapsed;
        main.Children.Add(coolerRow);

        // ---- Teclado (OLED): tela atual, fixar, próxima, editar telas.
        var kd = new DockPanel { LastChildFill = true };
        var kl = Ui.T("TECLADO", 11, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); });
        kbPin = FlatButton.Icon(Icons.Pin, 20, 11, "Fixar/soltar a tela do teclado");
        kbPin.Click += () => KeyboardAction?.Invoke("pin");
        var next = FlatButton.Icon(Icons.ChevronRight, 20, 12, "Próxima tela do teclado");
        next.Click += () => KeyboardAction?.Invoke("next");
        var edit = FlatButton.Icon(Icons.Pencil, 20, 11, "Editar as telas do teclado");
        edit.Click += () => KeyboardRequested?.Invoke();
        var kr = new StackPanel { Orientation = Orientation.Horizontal, Children = { kbPin, next, edit } };
        kbText = Ui.T("", 11, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        DockPanel.SetDock(kl, Dock.Left); DockPanel.SetDock(kr, Dock.Right);
        kd.Children.Add(kl); kd.Children.Add(kr); kd.Children.Add(kbText);
        kbRow = Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(9, 3, 7, 3), kd);
        kbRow.Visibility = Visibility.Collapsed;
        main.Children.Add(kbRow);
        Children.Add(main);

        // ---- B6 configurações
        settings = new StackPanel { Visibility = Visibility.Collapsed };
        paletteLabel = Label("COR · ELÉTRICO");
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
        segTheme = Seg(("claro", "Claro"), ("escuro", "Escuro"), ("sistema", "Sistema"));
        segTheme.Changed += v => app.Update(s => s with { Theme = (string)v });
        settings.Children.Add(SettingRow(Label("TEMA"), segTheme));
        segRefresh = Seg((500, "0,5s"), (1000, "1s"), (2000, "2s"));
        segRefresh.Changed += v => app.Update(s => s with { RefreshMs = (int)v });
        settings.Children.Add(SettingRow(Label("ATUALIZAÇÃO"), segRefresh));
        segHistory = Seg((60, "1m"), (300, "5m"), (600, "10m"), (1800, "30m"), (3600, "1h"), (7200, "2h"));
        segHistory.Changed += v => app.Update(s => s with { History = (int)v });
        settings.Children.Add(SettingRow(Label("HISTÓRICO"), segHistory));
        segScale = Seg(("independente", "Separada"), ("compartilhada", "Única"));
        segScale.Changed += v => app.Update(s => s with { Scale = (string)v });
        settings.Children.Add(SettingRow(Label("ESCALA REDE"), segScale));
        segNetUnit = Seg(("bytes", "MB/s"), ("bits", "Mbps"));
        segNetUnit.Changed += v => app.Update(s => s with { NetUnit = (string)v });
        settings.Children.Add(SettingRow(Label("UNIDADE REDE"), segNetUnit));
        acModeChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        acModesSetting = SettingRow(Label("MODOS DA PLACA"), acModeChecks); acModesSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(acModesSetting);
        acFanChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        acFansSetting = SettingRow(Label("VENTOINHAS NOS MODOS"), acFanChecks); acFansSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(acFansSetting);
        // Popup: cards de temperatura (quais e ordem), cards dos fans da placa e do cooler, desligar fan (experimental).
        tempChecks = new StackPanel();
        settings.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 6, 6, 4), new StackPanel { Children =
        {
            new DockPanel { Margin = new Thickness(0, 0, 4, 3), Children = { Label("TEMPERATURAS NO POPUP"), Ui.T($"máx. {TempCards.MaxCards} · ordem ▲▼", 10, Theme.Muted).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }) } },
            tempChecks,
        } }));
        RenderTempSettings();
        boardCardChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        boardCardsSetting = SettingRow(Label("FANS DA PLACA"), boardCardChecks); boardCardsSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(boardCardsSetting);
        coolerCardChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        settings.Children.Add(SettingRow(Label("FANS DO COOLER"), coolerCardChecks));
        RenderCoolerSettings();
        boardOff = new Check15(); boardOff.Changed += v => app.Update(s => s with { BoardFanOff = v });
        var offRow = SettingRow(Label("DESLIGAR FAN DA PLACA"), boardOff);
        offRow.ToolTip = "Experimental: clique no card do fan da placa desliga/religa.\nNo chip vira a curva de proteção: 0% até 80 °C, 100% a partir de 85 °C.\nCPU e bombas não desligam; religa tudo com CPU > 85 °C, GPU > 80 °C, FULL ou modo game; não fica salvo ao sair.";
        settings.Children.Add(offRow);
        gameChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        settings.Children.Add(SettingRow(Label("MODO GAME"), gameChecks));
        gameFanChecks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        gameFansSetting = SettingRow(Label("PLACA NO GAME"), gameFanChecks); gameFansSetting.Visibility = Visibility.Collapsed;
        settings.Children.Add(gameFansSetting);
        var segGpuG = Seg(Settings.GamePercents.Select(p => ((object)p, $"{p}%")), app.Settings.GameGpuPercent);
        segGpuG.Changed += v => app.Update(x => x with { GameGpuPercent = (int)v });
        settings.Children.Add(SettingRow(Label("GPU NO GAME"), segGpuG));
        var segCoolG = Seg(Settings.GamePercents.Select(p => ((object)p, $"{p}%")), app.Settings.GameCoolerPercent);
        segCoolG.Changed += v => app.Update(x => x with { GameCoolerPercent = (int)v });
        settings.Children.Add(SettingRow(Label("COOLER NO GAME"), segCoolG));
        RenderGameSettings();
        // Jogos que ligam o modo game sozinhos (nome do .exe); detecção por lista de processos a cada ~3 s, sem admin.
        gameExeList = new WrapPanel();
        gameExeInput = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FontFamily = AppFonts.Body, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null, MaxLength = 64 };
        gameExeInput.SetResourceReference(TextBox.ForegroundProperty, Theme.Text);
        gameExeInput.SetResourceReference(TextBox.CaretBrushProperty, Theme.Text);
        gameExeInput.SetResourceReference(TextBox.SelectionBrushProperty, Theme.Fill);
        var exeHint = Ui.T("jogo.exe", 11, Theme.Muted, head: false).With(t => { t.IsHitTestVisible = false; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(1, 0, 0, 0); });
        gameExeInput.TextChanged += (_, _) => exeHint.Visibility = gameExeInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        gameExeInput.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { AddGameExe(); e.Handled = true; } };
        var exeBox = Ui.Box(new Thickness(1), new Thickness(6, 0, 6, 0), new Grid { Children = { exeHint, gameExeInput } }).With(b => { b.Height = 20; b.Cursor = System.Windows.Input.Cursors.IBeam; });
        exeBox.MouseLeftButtonDown += (_, _) => gameExeInput.Focus();
        var addExe = new FlatButton { Height = 20, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), BorderThickness = new Thickness(1), ToolTip = "Adicionar o executável à lista" }.Content(Ui.T("ADICIONAR", 10, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        addExe.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        addExe.Click += AddGameExe;
        var addRow = new DockPanel { Margin = new Thickness(0, 4, 4, 0) };
        DockPanel.SetDock(addExe, Dock.Right); addRow.Children.Add(addExe); addRow.Children.Add(exeBox);
        settings.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(10, 6, 6, 6), new StackPanel { Children =
        {
            new DockPanel { Margin = new Thickness(0, 0, 4, 2), Children = { Label("MODO GAME AUTOMÁTICO"), Ui.T("liga com o jogo aberto", 10, Theme.Muted).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }) } },
            gameExeList, addRow,
        } }).With(b => b.ToolTip = "Com um destes executáveis rodando o modo game liga; quando todos fecham, desliga.\nSe você ligou o modo game no botão, o automático não desliga. Desligado no botão com o jogo aberto, só volta no próximo jogo."));
        RenderGameExes();
        // Ícone da bandeja: maior temperatura entre os cards habilitados e/ou cor de alerta perto do limite.
        trayTemp = new Check15 { VerticalAlignment = VerticalAlignment.Center }; trayTemp.Changed += v => app.Update(s => s with { TrayTemp = v });
        trayAlert = new Check15 { VerticalAlignment = VerticalAlignment.Center }; trayAlert.Changed += v => app.Update(s => s with { TrayAlert = v });
        StackPanel Opt(Check15 c, string label) => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 0, 2), Background = Brushes.Transparent, Children = { c, Ui.T(label, 10, Theme.Muted).With(t => { t.Margin = new Thickness(4, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }) } };
        var trayRow = SettingRow(Label("ÍCONE DA BANDEJA"), new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230, Children = { Opt(trayTemp, "TEMPERATURA"), Opt(trayAlert, "COR DE ALERTA") } });
        trayRow.ToolTip = "Temperatura: o ícone mostra as escolhidas em TEMPERATURA NO ÍCONE (ou a maior entre os cards).\nCor de alerta: âmbar com algum card a 5 °C do limite, vermelho no limite.\nCom qualquer um ligado, os sensores seguem sendo lidos com o popup fechado.";
        settings.Children.Add(trayRow);
        segTrayFont = Seg(Settings.TrayFontSizes.Select(x => ((object)x, x.ToUpperInvariant())), app.Settings.TrayFontSize);
        segTrayFont.Changed += v => app.Update(s => s with { TrayFontSize = (string)v });
        trayTempPicks = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 230 };
        settings.Children.Add(SettingRow(Label("TEMPERATURA NO ÍCONE"), trayTempPicks).With(b => b.ToolTip = "Quais temperaturas o ícone mostra, na ordem em que você liga.\nNenhuma = a mais quente entre os cards do popup.\nMais de uma = alternam no tempo escolhido abaixo.\nO tooltip do ícone diz qual está aparecendo."));
        segTrayRotate = Seg(Settings.TrayRotateSeconds.Select(x => ((object)x, x + "s")), app.Settings.TrayRotateSec);
        segTrayRotate.Changed += v => app.Update(s => s with { TrayRotateSec = (int)v });
        settings.Children.Add(SettingRow(Label("TROCAR A CADA"), segTrayRotate).With(b => b.ToolTip = "Tempo de cada temperatura no ícone quando há mais de uma escolhida."));
        settings.Children.Add(SettingRow(Label("TAMANHO DA FONTE"), segTrayFont).With(b => b.ToolTip = "Número da temperatura no ícone da bandeja: PP (padrão), P, M ou G (ocupa o ícone todo)."));
        opacity = new MiniSlider();
        opacityLabel = Ui.T("90%", 13, tabular: true).With(t => { t.Width = 32; t.TextAlignment = TextAlignment.Right; t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; });
        opacity.Changed += v => app.Update(s => s with { Opacity = v });
        settings.Children.Add(SettingRow(Label("OPACIDADE"), new StackPanel { Orientation = Orientation.Horizontal, Children = { opacity, opacityLabel } }));
        autostart = new Check15(); autostart.Changed += v => app.Update(s => s with { Autostart = v });
        settings.Children.Add(SettingRow(Label("INICIAR COM O WINDOWS"), autostart));
        autohide = new Check15(); autohide.Changed += v => app.Update(s => s with { Autohide = v });
        settings.Children.Add(SettingRow(Label("FECHAR AO PERDER FOCO"), autohide));
        startHidden = new Check15(); startHidden.Changed += v => app.Update(s => s with { StartHidden = v });
        settings.Children.Add(SettingRow(Label("INICIAR OCULTO NA BANDEJA"), startHidden));
        closeToTray = new Check15(); closeToTray.Changed += v => app.Update(s => s with { CloseToTray = v });
        settings.Children.Add(SettingRow(Label("FECHAR ESCONDE NA BANDEJA"), closeToTray));
        // Rodapé: versão + Sobre
        var ver = Ui.T($"v{AppInfo.Version}", 10, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        var about = new FlatButton { Padding = new Thickness(6, 1, 6, 1), ToolTip = "Sobre o Marz Monitor" }.Content(Ui.T("SOBRE", 11, Theme.Muted));
        about.Click += () => AboutRequested?.Invoke();
        var quit = new FlatButton { Padding = new Thickness(6, 1, 6, 1), ToolTip = "Sair (fans voltam ao automático)" }.Content(Ui.T("SAIR", 11, Theme.Muted));
        quit.Click += () => QuitRequested?.Invoke();
        var fdock = new DockPanel();
        DockPanel.SetDock(ver, Dock.Left); fdock.Children.Add(ver);
        var fr = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { about, quit } };
        fdock.Children.Add(fr);
        settings.Children.Add(Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(10, 4, 6, 4), fdock));
        // Com as listas do popup a página passa da altura de uma tela 1080p: rola dentro do popup.
        settingsScroll = new ScrollViewer { Content = settings, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, CanContentScroll = false, MaxHeight = SettingsMaxHeight };
        Children.Add(settingsScroll);

        ShowSettings(false);
        ApplySettings(app.Settings);
    }

    public bool InSettings => settings.Visibility == Visibility.Visible;

    private readonly ScrollViewer settingsScroll;
    /// <summary>Altura máxima da página de Configurações (rola acima disso); a prévia usa infinito para o PNG sair inteiro.</summary>
    internal static double SettingsMaxHeight { get; set; } = 760;

    public void ShowSettings(bool on)
    {
        settings.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        settingsScroll.Visibility = settings.Visibility;
        main.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        back.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        glyph.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        title.Text = on ? "CONFIGURAÇÕES" : "MARZ MONITOR";
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

    /// <summary>Linha da rede embaixo do gráfico: seta + velocidade atual em destaque e a escala pequena.</summary>
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

    /// <summary>Caixinha por unidade numa linha só: "C:  R 12M W 850K  40%" (L/E em branco quando parado) + barra de uso.</summary>
    private sealed class VolumeChip : Border
    {
        /// <summary>Taxa curta: "850K", "12M", "1.2G" (bytes/s, base 1024); vazio abaixo de 1 KB/s.</summary>
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
            var hw = v.Hardware.Length > 0 ? v.Hardware : "disco";
            var tip = $"{v.Letter} {hw}" + (v.Label.Length > 0 ? $" · {v.Label}" : "") + $"\n{Formatter.UsedTotal(v.Used, v.Total)} usados"
                + (v.ReadBps != null ? $"\nLeitura {Formatter.DiskMBps(r)} MB/s · Escrita {Formatter.DiskMBps(w)} MB/s" : "");
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
        kbText.Text = s.GgStatus ?? (!s.Enabled ? "desligado" : d.Error != null ? "GG sem resposta" : string.Join("  ", d.Lines.Select(l => l.Trim())).Trim() is { Length: > 0 } t ? $"{s.Page + 1}/{s.PageCount}  {t}" : $"tela {s.Page + 1}/{s.PageCount}");
        kbText.ToolTip = d.Error ?? (d.Lines.Length > 0 ? string.Join("\n", d.Lines) : null);
        kbPin.Active = s.Pinned;
        if (was != Visibility.Visible) LayoutChanged?.Invoke();
    }

    public void AttachFans(GpuFanService s) { fanService = s; UpdateFans(s.State); }

    public void AttachAsus(AsusFanModes m)
    {
        ac = m;
        m.Changed += () => Dispatcher.BeginInvoke(() => { TryRestoreBoard(); TryRestoreChoices(); RenderAc(); RenderGameIcon(); });
        m.OffRejected += _ => Dispatcher.BeginInvoke(RenderAcRpm); // curva de proteção não conferiu: card volta a ligado (erro no status)
        RenderAc();
    }

    private IReadOnlyDictionary<int, float> mbRpm = new Dictionary<int, float>(), mbCtl = new Dictionary<int, float>();
    private float? cpuTemp, gpuTemp;

    private readonly List<(string Fan, FanCard Card)> boardCards = [];
    private string boardLayout = "";

    /// <summary>Apelido do usuário (Settings.AcFanNames) ou o nome curto do conector.</summary>
    private string FanNick(string acName) => app.Settings.AcFanNames.TryGetValue(acName, out var n) && n.Length > 0 ? n : FanShort(acName);

    /// <summary>Cards dos fans da placa escolhidos nas Configurações (rpm, %, mini barra). Clique alterna OFF.</summary>
    private void RenderAcRpm()
    {
        if (ac == null) return;
        bool blocked = offBlocked is { } ob && (DateTime.UtcNow - ob.At).TotalSeconds < 5;
        acStatus.Text = ac.Error != null ? "erro" : ac.Pending != null ? "aplicando…" : blocked ? "não desligou" : "";
        acStatus.ToolTip = ac.Error ?? (blocked ? offBlocked!.Value.Why : null);
        var s = app.Settings;
        var fans = ac.Fans.Where(s.PopupBoardFans.Contains).ToList();
        var layout = string.Join("|", fans.Select(f => f + "=" + FanNick(f)));
        if (layout != boardLayout)
        {
            boardLayout = layout; boardCards.Clear(); acRpmGrid.Children.Clear();
            acRpmGrid.Visibility = fans.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var f in fans)
            {
                var card = new FanCard(FanNick(f).ToUpperInvariant(), BoardFanOff.Locked(f)) { Margin = CardMargin(boardCards.Count) };
                var fan = f;
                card.Click += () => ToggleBoardFan(fan);
                card.RightClick += () => OpenQuick(card, "board", fan, FanShort(fan) + " · " + FanNick(fan));
                boardCards.Add((f, card)); acRpmGrid.Children.Add(card);
            }
            LayoutChanged?.Invoke();
        }
        foreach (var (f, card) in boardCards)
        {
            var rd = BoardFanMap.Read(f, mbRpm, mbCtl); // rpm só dos conectores conferidos; nunca calculado a partir do %
            bool off = ac.Off.IsOff(f);
            card.Set(rd.Rpm, rd.Percent, off);
            var man = ac.Off.ManualPercent(f);
            card.SetManual(man != null);
            card.Clickable = BoardFanOff.Clickable(f, s.BoardFanOff);
            var head = FanShort(f) + " · " + FanNick(f)
                + (rd.RpmMeasured ? "" : $"\nrpm não medido (saída do chip deste conector não confirmada); % de controle da saída {rd.ChipIndex?.ToString(CultureInfo.InvariantCulture) ?? "—"} (estimada).");
            // Opção desligada: card só de leitura (sem mãozinha, sem dica de desligar).
            card.ToolTip = !s.BoardFanOff ? head
                : BoardFanOff.Locked(f) ? head + "\nCPU e bombas não desligam."
                : off ? head + "\nDESLIGADO (0% até 80 °C, 100% a partir de 85 °C). Clique para religar."
                : head + (BoardFanOff.BlockReason(f, cpuTemp, gpuTemp) is string why ? $"\nNão desliga agora: {why}." : "\nClique para desligar (religa sozinho com CPU > 85 °C ou GPU > 80 °C).");
            card.ToolTip = card.ToolTip + (man is int mp ? $"\nMANUAL {mp}% (100% a partir de 85 °C)" : "") + "\nBotão direito: ajuste rápido";
        }
        quickRefresh?.Invoke();
    }

    /// <summary>Clique no card: desliga/religa sem diálogo (só com a opção experimental ligada; nunca CPU/bombas).</summary>
    private void ToggleBoardFan(string fan)
    {
        if (ac is not { Available: true }) return;
        switch (ac.Off.ClickAction(fan, app.Settings.BoardFanOff, cpuTemp, gpuTemp))
        {
            case BoardFanClick.TurnOn: ac.TurnOn([fan], "clique no card"); break;
            case BoardFanClick.TurnOff: if (!ac.TurnOff(fan, cpuTemp, gpuTemp)) offBlocked = (DateTime.UtcNow, "Não desligou."); break;
            case BoardFanClick.Blocked: offBlocked = (DateTime.UtcNow, $"{FanNick(fan)} não desliga agora: {BoardFanOff.BlockReason(fan, cpuTemp, gpuTemp)}."); break;
            default: return;
        }
        RenderAcRpm();
    }

    /// <summary>Último clique que não desligou (proteção): o status da linha da placa mostra por alguns segundos.</summary>
    private (DateTime At, string Why)? offBlocked;

    /// <summary>Só para a prévia: modo game "ligado" (automático por <paramref name="by"/> ou na mão) sem comandar GPU/placa/cooler.</summary>
    internal void PreviewGame(string? by) { gameOn = true; gameAt = DateTime.UtcNow; autoGame.Restore(by != null, by); game.Active = true; RenderStrip(); }

    /// <summary>Só para a prévia: aviso de fans religados pela proteção.</summary>
    internal void PreviewSafety(SafetyEvent e) { if (ac != null) { ac.LastSafety = e; RenderStrip(); } }

    /// <summary>Só para a prévia: tooltip do card de temperatura (mín./máx. da sessão).</summary>
    internal string? TempTip(string id) => tempCards.FirstOrDefault(c => c.Def.Id == id).Card?.ToolTip as string;

    /// <summary>Só para a prévia: marca um fan como desligado sem tocar no hardware (AsusFanModes.Fake).</summary>
    internal void PreviewBoardOff(string fan) { ac?.Off.TurnOff(fan, 50, 50); RenderAcRpm(); }

    // ---- Quick card (botão direito no card de fan): CURVA / MANUAL / OFF e % rápido.
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

    private void OpenQuick(FrameworkElement anchor, string kind, string id, string title)
    {
        if (quick == null)
        {
            quick = new Popup { AllowsTransparency = true, StaysOpen = false, Placement = PlacementMode.Bottom, PopupAnimation = PopupAnimation.None, VerticalOffset = 2 };
            quick.Closed += (_, _) => quickRefresh = null;
        }
        quick.IsOpen = false;
        quick.PlacementTarget = anchor;
        quick.Child = QuickCard(kind, id, title);
        quick.IsOpen = true;
    }

    /// <summary>Conteúdo do quick card (também usado pela prévia). Grava só no clique/ao soltar o slider.</summary>
    internal Border QuickCard(string kind, string id, string title)
    {
        var body = new StackPanel { Width = 200 };
        var box = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 6, 8, 8), Child = body, SnapsToDevicePixels = true };
        box.SetResourceReference(Border.BackgroundProperty, Theme.Bg);
        box.SetResourceReference(Border.BorderBrushProperty, Theme.DStroke);
        bool board = kind == "board";
        int min = board ? BoardFanOff.ManualMinPercent : id == ThermalMap.PumpId ? MonitorTray.Ryujin.CoolerSafety.PumpMinPercent : 0;
        int? sliderValue = null;

        void Manual(int v)
        {
            v = Math.Clamp(v, min, 100);
            if (board) { if (ac == null || !ac.SetManual(id, v)) offBlocked = (DateTime.UtcNow, ac?.Error ?? "Não aplicou."); }
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
            if (!ac.TurnOff(id, cpuTemp, gpuTemp)) offBlocked = (DateTime.UtcNow, $"{FanNick(id)} não desliga agora: {BoardFanOff.BlockReason(id, cpuTemp, gpuTemp) ?? "recusado"}.");
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
                block = ac is not { Available: true } ? "Armoury Crate indisponível" : ac.Pending != null ? "aplicando um modo…" : null;
                note = $"MANUAL: fica no % até {BoardFanOff.CurveOffUntilC} °C e vai a 100% a partir de {BoardFanOff.CurveFullFromC} °C (no chip). Volta à curva ao sair do Marz.";
            }
            else
            {
                var r = cooler?.Latest; var c = cooler!.FanConfig; var part = CoolerPart(c, id);
                rpm = id == ThermalMap.PumpId ? r?.Pump?.PumpRpm : id == ThermalMap.EmbeddedId ? r?.Pump?.EmbeddedFanRpm : r?.Fans?.Rpm is { Length: > 0 } rr ? (int)rr.Average() : null;
                pct = id == ThermalMap.PumpId ? r?.PumpPercent : id == ThermalMap.EmbeddedId ? r?.EmbeddedPercent : r?.Fans?.DutyPercent;
                manual = c.Enabled && part.Mode == CoolerFanMode.Fixed ? part.FixedPercent : null;
                block = cooler is not { DeviceFound: true } ? "cooler não encontrado" : cooler.Tuning ? "auto tuning em andamento" : null;
                note = id == ThermalMap.RadiatorId ? "Os 3 fans do radiador andam juntos (limite do cooler)."
                    : id == ThermalMap.PumpId ? $"Bomba nunca abaixo de {min}%. Líquido ≥ 50 °C põe tudo em 100%."
                    : "Fan embutido na bomba.";
            }
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var live = Ui.T(isOff ? "OFF" : $"{(rpm is int x ? x + " rpm · " : "")}{(pct is int y ? y + "%" : "—")}", 10, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
            DockPanel.SetDock(live, Dock.Right); head.Children.Add(live);
            head.Children.Add(Ui.T(title, 11, weight: 600).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); }));
            body.Children.Add(head);

            bool enabled = block == null;
            var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var (bc, _) = ModeButton("CURVA", manual == null && !isOff, false); bc.Click += () => { if (enabled) Curve(); };
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
            var edit = Ui.T("Editar curva…", 10.5, Theme.Text).With(t => { t.Margin = new Thickness(0, 6, 0, 0); t.Cursor = System.Windows.Input.Cursors.Hand; t.TextDecorations = TextDecorations.Underline; });
            edit.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (quick != null) quick.IsOpen = false; ThermalActuatorRequested?.Invoke(board ? ThermalMap.BoardId(id) : id); };
            body.Children.Add(edit);
        }
        quickRefresh = () => { if (sliderValue == null) Render(); };
        Render();
        return box;
    }

    private void RenderAfterQuick() { RenderAcRpm(); RenderCooler(); quickRefresh?.Invoke(); }

    /// <summary>Só para a prévia: MANUAL sem tocar no hardware.</summary>
    internal void PreviewBoardManual(string fan, int pct) { ac?.Off.SetManual(fan, pct); RenderAcRpm(); }

    private static string FanShort(string acName) => acName switch
    {
        "CPU Fan" => "CPU", "CPU Optional Fan" => "CPU_OPT", "Water Pump+" => "W_PUMP+", "AIO Pump" => "AIO_PUMP",
        _ => acName.Replace("Chassis Fan ", "CHA"),
    };

    /// <summary>Linha PLACA (botões) e as duas linhas de configuração; reconstrói a partir das Configurações.</summary>
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
        var current = ac.Pending ?? ac.Mode ?? "curvas";
        foreach (var mode in AsusFanModes.AllModes.Where(s.AcModes.Contains))
        {
            var (b, _) = ModeButton(AsusFanModes.Label(mode), mode == current, mode == "full");
            var fans = s.AcFans.Where(ac.Fans.Contains).ToList();
            b.ToolTip = (mode == "curvas" ? "Volta às suas curvas do Armoury Crate (perfil User)" : mode == "full" ? "Ventoinhas em 100% (religa as desligadas)" : $"Perfil {AsusFanModes.Label(mode)} do Armoury Crate")
                + "\nVentoinhas: " + (fans.Count == 0 ? "nenhuma (escolha nas Configurações)" : string.Join(", ", fans.Select(FanShort)))
                + (mode != "full" && ac.Off.Any ? "\nAs desligadas seguem desligadas." : "");
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
            acFanChecks.Children.Add(Toggle(FanShort(fan), s.AcFans.Contains(fan), v =>
            {
                var l = ac.Fans.Where(x => x == fan ? v : app.Settings.AcFans.Contains(x)).ToList();
                app.Update(x => x with { AcFans = l });
            }, fan.Contains("Pump") || fan.StartsWith("CPU") ? "Cuidado: bomba/CPU" : null));
        boardCardChecks.Children.Clear();
        foreach (var fan in ac.Fans)
            boardCardChecks.Children.Add(Toggle(FanNick(fan), s.PopupBoardFans.Contains(fan), v =>
            {
                var l = ac.Fans.Where(x => x == fan ? v : app.Settings.PopupBoardFans.Contains(x)).ToList();
                app.Update(x => x with { PopupBoardFans = l });
            }, FanShort(fan)));
        RenderGameSettings();
        if (was != acRow.Visibility) LayoutChanged?.Invoke();
    }

    private StackPanel tempChecks = null!;
    private WrapPanel coolerCardChecks = null!;
    private Check15 boardOff = null!;
    private string tempSettingsKey = "";

    /// <summary>Lista das temperaturas: ligadas na ordem escolhida (▲▼ movem), depois as desligadas na ordem do catálogo.</summary>
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
                if (v && !app.Settings.PopupTemps.Contains(id)) c.Value = false; // já há 8: não liga
            };
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, ToolTip = def.Tip };
            var moveUp = FlatButton.Icon(Icons.ChevronUp, 18, 11, "Subir"); var moveDown = FlatButton.Icon(Icons.ChevronDown, 18, 11, "Descer");
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

    private static readonly (string Id, string Label)[] CoolerCardLabels = [("pump", "BOMBA"), ("radiator", "RADIADOR"), ("embedded", "FAN EMB.")];

    private void RenderCoolerSettings()
    {
        coolerCardChecks.Children.Clear();
        foreach (var (id, label) in CoolerCardLabels)
            coolerCardChecks.Children.Add(Toggle(label, app.Settings.PopupCoolerFans.Contains(id), v =>
                app.Update(x => x with { PopupCoolerFans = Settings.CoolerCardIds.Where(y => y == id ? v : x.PopupCoolerFans.Contains(y)).ToList() })));
    }

    private WrapPanel gameChecks = null!, gameFanChecks = null!, gameExeList = null!;
    private Border gameFansSetting = null!;
    private TextBox gameExeInput = null!;
    private Check15 trayTemp = null!, trayAlert = null!;
    private Segmented segTrayFont = null!, segTrayRotate = null!;
    private WrapPanel trayTempPicks = null!;
    private string? trayPicksKey;

    /// <summary>Botões das temperaturas do ícone: acesos na ordem do rodízio (número = posição); nenhum aceso = a mais quente.</summary>
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

    /// <summary>Lista dos jogos: um chip por executável com × para remover.</summary>
    private void RenderGameExes()
    {
        var exes = app.Settings.GameExes;
        var key = string.Join("|", exes);
        if (key == gameExesKey) return;
        gameExesKey = key;
        gameExeList.Children.Clear();
        if (exes.Count == 0)
            gameExeList.Children.Add(Ui.T("nenhum jogo na lista", 10, Theme.Muted, head: false).With(t => t.Margin = new Thickness(0, 2, 0, 2)));
        foreach (var exe in exes)
        {
            var (x, _) = Icons.Make(Icons.X, 9, Theme.Muted);
            x.Margin = new Thickness(4, 0, 0, 0); x.VerticalAlignment = VerticalAlignment.Center;
            var chip = new FlatButton { Height = 18, Padding = new Thickness(6, 0, 4, 0), Margin = new Thickness(0, 2, 4, 2), BorderThickness = new Thickness(1), ToolTip = $"Remover {exe}" }
                .Content(new StackPanel { Orientation = Orientation.Horizontal, Children = { Ui.T(exe, 10, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Center), x } });
            chip.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
            var e = exe;
            chip.Click += () => app.Update(s => s with { GameExes = s.GameExes.Where(y => y != e).ToList() });
            gameExeList.Children.Add(chip);
        }
        LayoutChanged?.Invoke();
    }
    private static readonly (string Id, string Label)[] GameTargetLabels = [("gpu", "GPU"), ("board", "PLACA"), ("pump", "BOMBA"), ("radiator", "RADIADOR"), ("embedded", "FAN EMB.")];

    private static Segmented Seg(IEnumerable<(object, string)> o, object sel) { var s = new Segmented(o); s.Select(sel); return s; }

    private string GameSummary()
    {
        var s = app.Settings; var parts = new List<string>();
        if (s.GameTargets.Contains("gpu")) parts.Add($"GPU {s.GameGpuPercent}%");
        if (s.GameTargets.Contains("board")) parts.Add("placa FULL");
        var c = GameTargetLabels.Skip(2).Where(x => s.GameTargets.Contains(x.Id)).Select(x => x.Label.ToLowerInvariant()).ToList();
        if (c.Count > 0) parts.Add($"{string.Join("/", c)} {s.GameCoolerPercent}%");
        return parts.Count == 0 ? "nada selecionado" : string.Join(" · ", parts);
    }

    /// <summary>O que o modo game liga e quais ventoinhas da placa entram (Configurações).</summary>
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
                gameFanChecks.Children.Add(Toggle(FanShort(fan), chosen.Contains(fan), v =>
                {
                    var cur = GameBoardFans();
                    var l = ac.Fans.Where(x => x == fan ? v : cur.Contains(x)).ToList();
                    app.Update(x => x with { GameBoardFans = l });
                }, fan.Contains("Pump") || fan.StartsWith("CPU") ? "Bomba/CPU em FULL é seguro (só mais barulho)" : null));
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
        // Título fixo "GPU" (como PLACA-MÃE e WC); o modelo fica no tooltip. Temperaturas estão na faixa de cards.
        if (r != null) gpuName.ToolTip = r.Identity.Name + " · abrir no Detalhes";
        // Ícone: "A" = automático do driver; número = preset manual; "?" = misto/desconhecido.
        fanBadge.Text = s.Mode switch { FanMode.Automatic => "A", FanMode.Manual => s.RequestedPercent?.ToString(CultureInfo.InvariantCulture) ?? "M", _ => "?" };
        fanPath.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, s.Mode == FanMode.Manual ? Theme.UStroke : s.Mode == FanMode.Automatic ? Theme.Fill : Theme.Muted);
        fanSpeeds.Text = s.Error != null && r == null ? "sem leitura"
            : r == null ? "—" : Formatter.GpuFans(r.Fans.Select(f => (f.SpeedPercent, f.SpeedRpm)));
        var tip = r == null ? s.Error : string.Join("\n", r.Fans.Select(f => $"Fan {f.Index + 1}: {(f.SpeedPercent is uint v ? v + "%" : "—")} · {(f.SpeedRpm is uint rr ? rr + " RPM" : "— RPM")}"))
            + "\n" + Views.FanText.Mode(s);
        fanSpeeds.ToolTip = tip;
        // Gira enquanto as fans giram; mais rápido com mais %. Parado em 0%.
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
                : !s.CanControl ? s.ReadOnlyReason ?? "Controle indisponível." : s.Busy ? "Comando em andamento." : s.Reading == null ? "Sem leitura." : null;
            b.IsEnabled = block == null; b.Opacity = block == null ? 1 : 0.45;
            b.ToolTip = block ?? s.Error ?? (v is null ? "Devolver ao automático do driver" : $"Fixar as fans em {v}%");
            bool active = v is null ? s.Mode == FanMode.Automatic : s.Mode == FanMode.Manual && s.RequestedPercent == v;
            b.Active = active;
            t.SetResourceReference(TextBlock.ForegroundProperty, active ? Theme.Bg : Theme.Muted);
        }
        if (was != Visibility.Visible) LayoutChanged?.Invoke();
    }

    /// <summary>Nome curto da GPU (ver Formatter.ShortGpuName).</summary>
    internal static string ShortGpuName(string name) => Formatter.ShortGpuName(name);

    public void ApplySettings(Settings s)
    {
        if (ac != null) Dispatcher.BeginInvoke(RenderAc);
        refresh.Text = Formatter.RefreshLabel(s.RefreshMs);
        paletteLabel.Text = "COR · " + Theme.Palette(s.Palette).Name.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
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
        bool pinned = !s.Autohide;
        pin.Active = pinned;
        pin.ToolTip = pinned ? "Fixado: fica aberto ao clicar fora (clique para soltar)" : "Solto: esconde ao clicar fora (clique para fixar)";
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
        storageLabel.ToolTip = $"Total L/E: {rd.Text} / {wr.Text} MB/s";
        UpdateVolumes(snap.Volumes);
        cpuTemp = s.CpuTemp; gpuTemp = s.GpuTemp ?? (fanService?.State.Reading?.TemperatureC is uint g ? (float?)g : null);
        TryRestoreOff();
        mbRpm = snap.MbFanRpm; mbCtl = snap.MbFanControl; RenderAcRpm(); RenderGameIcon(); RenderCooler();
        lhmTemps = snap.Temps; tempSources = snap.TempSources; RenderTemps();
        var d = Formatter.Net(s.NetDownBps); var u = Formatter.Net(s.NetUpBps);
        down.Text = d.Value; downU.Text = d.Unit; up.Text = u.Value; upU.Text = u.Unit;
        // O serviço já entrega a janela agrupada (pico por balde) com 1 ponto extra à esquerda para a rolagem.
        var n = snap.HistoryPoints;
        var (sd, su) = NiceScale.NetScales(snap.NetDownHistory, snap.NetUpHistory, app.Settings.Scale == "compartilhada");
        chart.Set(snap.NetDownHistory, snap.NetUpHistory, n, sd, su);
        chart.ToolTip = "Rede · últimos " + Formatter.Window(app.Settings.History) + (n < app.Settings.History ? " (pico a cada " + RateHistoryLabel(app.Settings.History) + ")" : "");
        NetCorner(scaleDown, "↓", $"{Formatter.Net(s.NetDownBps)}", $"{Formatter.Net(sd)}", Theme.DStroke);
        NetCorner(scaleUp, "↑", $"{Formatter.Net(s.NetUpBps)}", $"{Formatter.Net(su)}", Theme.UStroke);
        scaleDown.ToolTip = scaleUp.ToolTip = "atual  |  escala do gráfico";
    }

    /// <summary>
    /// Faixa de temperaturas: papéis escolhidos nas Configurações, na ordem; sem leitura = card omitido.
    /// LHM primeiro; GPU pela NVML quando o LHM não tem; líquido pelo cooler.
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

    /// <summary>Para onde o clique leva: sensor do LHM no Detalhes; GPU pela NVML = dispositivo GPU; líquido = controle térmico.</summary>
    private string Target(string id) => id == "liq" ? "controle térmico" : tempSources.ContainsKey(id) ? "Detalhes" : id is "gpu" or "hot" or "vram" ? "GPU no Detalhes" : "Detalhes";

    private void OpenTemp(string id)
    {
        if (id == "liq") { ThermalRequested?.Invoke(); return; }
        var category = id is "gpu" or "hot" or "vram" ? "GPU" : id is "cpu" or "coremax" ? "CPU" : id == "ram" ? "Memória" : "Placa-mãe";
        SensorRequested?.Invoke(tempSources.TryGetValue(id, out var sid) ? sid : null, category);
    }

    private static string RateHistoryLabel(int window) { var b = MonitorTray.Core.History.RateHistory.BucketSeconds(window); return b >= 60 ? b / 60 + " min" : b + " s"; }

    /// <summary>Célula de métrica (CPU/GPU/MEM): carga em destaque; W (ou % da memória) no canto. Temperaturas ficam nos cards.</summary>
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
    /// Card de temperatura (menor que o do mock): rótulo, valor, mini barra (cheia no limite). Cor de alerta pelos limites das
    /// proteções (TempLimits): perto (≥ limite − 5 °C) o valor fica na cor B; no limite também o contorno. Clique abre o sensor.
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

        /// <param name="range">Mínimo e máximo desde que o app abriu.</param>
        /// <param name="target">Para onde o clique leva (texto do tooltip).</param>
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
                + (range is { } r ? $"\nSessão: mín. {F(r.Min)} · máx. {F(r.Max)} °C" : "")
                + $"\nAlerta: perto a partir de {d.LimitC - TempLimits.NearMarginC:0} °C, limite {d.LimitC:0} °C"
                + $"\nClique: abrir no {target}";
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
    /// Card de fan de tamanho padrão (placa e cooler): nome curto (+ cadeado) com o % à direita; rpm embaixo, mini barra.
    /// Sem rpm medido: "—" no lugar do rpm (o % segue; o rpm nunca é estimado). Desligado: cinza, "OFF" e o rpm/% lidos.
    /// Clicável só quando pode desligar.
    /// </summary>
    private sealed class FanCard : Border
    {
        private readonly TextBlock value, unit, pct;
        private readonly Bar bar = new() { Height = 2, Margin = new Thickness(-4, 1, -4, 0) };
        private bool clickable, pressed;
        private bool? off;
        private bool manual;
        public event Action? Click;
        /// <summary>Botão direito: abre o quick card (ajuste rápido).</summary>
        public event Action? RightClick;

        /// <summary>MANUAL: contorno na cor de destaque.</summary>
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
            head.Children.Add(n);
            value = Ui.T("—", 12, weight: 600, tabular: true).With(t => { t.LineHeight = 14; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; t.TextTrimming = TextTrimming.None; t.VerticalAlignment = VerticalAlignment.Bottom; });
            unit = Ui.T(" rpm", 9, Theme.Muted).With(t => { t.VerticalAlignment = VerticalAlignment.Bottom; t.Margin = new Thickness(0, 0, 0, 1); t.TextTrimming = TextTrimming.None; });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { value, unit } };
            Child = new StackPanel { Children = { head, row, bar } };
            SetOff(false);
        }

        /// <summary>false = só leitura: cursor normal (herdado) e o clique passa direto.</summary>
        public bool Clickable { get => clickable; set { clickable = value; Cursor = value ? System.Windows.Input.Cursors.Hand : null; } }

        /// <param name="rpm">rpm medido; null = sem medição ("—", sem unidade).</param>
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
            // Desligado: fundo e contorno apagados, textos em Muted.
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
