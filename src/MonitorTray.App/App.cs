using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using Microsoft.Win32;
using MonitorTray.App.Platform;
using MonitorTray.App.Theming;
using MonitorTray.App.Tray;
using MonitorTray.App.Views;
using MonitorTray.Core.Format;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;
using MonitorTray.Gpu;
using MonitorTray.Oled;
using MonitorTray.Sensors;

namespace MonitorTray.App;

public sealed class Options
{
    public string? Simulate { get; init; }
    public bool Tray { get; init; }
    public bool Details { get; init; }
    public bool FakeNew { get; init; }
    public string? Preview { get; init; }

    public static Options Parse(string[] a)
    {
        string? Arg(string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal) ? a[i + 1] : null; }
        var sim = Arg("--simulate");
        if (sim == null && a.Contains("--simulate")) sim = Path.Combine(AppContext.BaseDirectory, "sim", "sensores.json");
        return new Options { Simulate = sim, Tray = a.Contains("--tray"), Details = a.Contains("--details"), FakeNew = a.Contains("--fake-new"), Preview = Arg("--preview") };
    }
}

public sealed class App : Application
{
    private readonly Options opt;
    private readonly SingleInstance? instance;
    private AppState state = null!;
    private SensorService service = null!;
    private GpuFanService? fans;
    private AsusFanModes? asus;
    private KeyboardService? keyboard;
    private MonitorTray.Ryujin.RyujinService? ryujin;
    private LcdWindow? lcdWindow;
    public static string LcdPath => Path.Combine(AppState.DataDir, "ryujin-lcd.json");
    private KeyboardValues? kbValues;
    private System.Windows.Interop.HwndSource? kbSink;
    private KeyboardWindow? kbWindow;
    public static string KeyboardPath => Path.Combine(AppState.DataDir, "keyboard.json");
    private EventWaitHandle? quitSignal;
    private bool exiting;
    private TaskbarIcon? tray;
    private readonly TrayIconRenderer iconRenderer = new();
    private PopupWindow? popup;
    private DetailsWindow? details;
    private string lastTooltip = "";

    public App(Options opt, SingleInstance? instance) { this.opt = opt; this.instance = instance; ShutdownMode = ShutdownMode.OnExplicitShutdown; }

    public static AppState CreateState() => new();

    public static void InitResources(Application app, Settings s)
    {
        Theme.Apply(app.Resources, s);
        app.Resources["Transparent"] = Brushes.Transparent;
        var tt = new Style(typeof(ToolTip));
        tt.Setters.Add(new Setter(Control.FontFamilyProperty, AppFonts.Body));
        tt.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        tt.Setters.Add(new Setter(Control.BackgroundProperty, new DynamicResourceExtension(Theme.Bg)));
        tt.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension(Theme.Text)));
        tt.Setters.Add(new Setter(Control.BorderBrushProperty, new DynamicResourceExtension(Theme.Line)));
        app.Resources[typeof(ToolTip)] = tt;
        app.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = ScrollBarStyle;
    }

    /// <summary>Barra de rolagem fina (6 px), sem setas, polegar na cor Muted — combina com a moldura blueprint.</summary>
    private static readonly Style ScrollBarStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollBar'>
  <Setter Property='Width' Value='6'/>
  <Setter Property='MinWidth' Value='6'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Grid Background='Transparent'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Focusable='False'/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Focusable='False'/></Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType='Thumb'>
                    <Border x:Name='b' Background='{DynamicResource Muted}' Opacity='0.35' Margin='1,2,1,2'/>
                    <ControlTemplate.Triggers>
                      <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.6'/></Trigger>
                      <Trigger Property='IsDragging' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.8'/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");

    public static string StatusPath => Path.Combine(AppState.DataDir, "state", "fan-status.json");

    public static GpuFanService CreateFans(Options opt) => opt.Simulate != null
        ? new GpuFanService(() => new SimulatedGpuApi(), statusPath: null, takeMutex: false)
        : new GpuFanService(statusPath: StatusPath);

    public static SensorService CreateService(Options opt, string dataDir)
    {
        IHardwareSource hw;
        string knownFile;
        if (opt.Simulate != null)
        {
            var sim = new SimulatedSource(opt.Simulate);
            if (opt.FakeNew) { sim.Open(); sim.AddFakeDevice(); hw = new PreOpened(sim); }
            else hw = sim;
            knownFile = "known-devices-sim.json";
        }
        else { hw = new LhmSource(); knownFile = "known-devices.json"; }
        var known = new KnownDevices(Path.Combine(dataDir, knownFile));
        return new SensorService(hw, opt.Simulate == null ? new SystemSummarySource() : null, known, Autostart.IsAdmin());
    }

    /// <summary>Fonte já aberta (para injetar o dispositivo inventado antes da primeira leitura).</summary>
    private sealed class PreOpened(SimulatedSource s) : IHardwareSource
    {
        public void Open() { }
        public IReadOnlyList<RawSensor> Read() => s.Read();
        public bool IsSimulated => true;
        public string? Notice => s.Notice;
        public void Dispose() => s.Dispose();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        state = CreateState();
        InitResources(this, state.Settings);
        Formatter.NetBits = state.Settings.NetUnit == "bits";
        service = CreateService(opt, AppState.DataDir);
        service.RefreshMs = state.Settings.RefreshMs;
        service.HistorySeconds = state.Settings.History;
        service.Published += OnPublished;
        state.Changed += OnSettingsChanged;

        fans = CreateFans(opt);
        fans.Changed += s => Dispatcher.BeginInvoke(() => popup?.Content2.UpdateFans(s));
        fans.Start();
        quitSignal = QuitSignal.Create();
        if (quitSignal != null) QuitSignal.Watch(quitSignal, () => Dispatcher.BeginInvoke(() => ExitApp()));

        popup = new PopupWindow(state);
        popup.Content2.Session = temps;
        popup.Content2.SensorRequested += ShowDetailsAt;
        popup.Content2.DetailsRequested += ToggleDetails;
        popup.Content2.AboutRequested += ShowAbout;
        popup.Content2.QuitRequested += ExitApp;
        popup.Content2.AttachFans(fans);
        if (opt.Simulate == null)
        {
            try
            {
                asus = new AsusFanModes(Path.Combine(AppState.DataDir, "state")); popup.Content2.AttachAsus(asus);
                asus.Changed += () => Dispatcher.BeginInvoke(UpdateKeepHardware); // fan desligado: temperaturas frescas mesmo com o popup fechado
            }
            catch (Exception ex) { Crash(ex); }
        }
        popup.Content2.KeyboardRequested += ShowKeyboard;
        popup.Content2.ThermalRequested += ShowLcd;
        popup.Content2.ThermalActuatorRequested += id => { ShowLcd(); lcdWindow?.ShowActuator(id); };
        popup.Content2.KeyboardAction += a => { if (a == "pin") keyboard?.TogglePin(); else keyboard?.Next(); };
        if (opt.Simulate == null) StartKeyboard();
        if (opt.Simulate == null) StartRyujin();
        if (opt.Simulate == null) popup.Content2.RestoreGame();
        if (opt.Simulate == null) StartGameWatch();
        UpdateKeepHardware();
        popup.IsVisibleChanged += (_, _) => UpdateCollection();

        tray = new TaskbarIcon { ToolTipText = "Marz Monitor", NoLeftClickDelay = true, MenuActivation = H.NotifyIcon.Core.PopupActivationMode.RightClick };
        // Id fixo POR CAMINHO do exe: o Windows guarda "mostrar na barra" por (Guid, caminho). Com o Guid padrão
        // (nome do assembly) o instalado e os builds de teste disputavam o mesmo Guid e a escolha do usuário se perdia.
        tray.Id = H.NotifyIcon.Core.TrayIcon.CreateUniqueGuidFromString("MarzMonitor|" + (Environment.ProcessPath ?? "").ToLowerInvariant());
        tray.Icon = TrayIcons.Current();
        tray.TrayLeftMouseUp += (_, _) => TogglePopup();
        tray.ContextMenu = BuildMenu();
        tray.ForceCreate(false);
        _ = Task.Delay(3000).ContinueWith(_ => PromoteTrayIcon());

        SystemEvents.UserPreferenceChanged += OnUserPreference;
        SystemEvents.PowerModeChanged += OnPower;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => popup?.Reposition());
        if (instance != null)
        {
            instance.Message += m => Dispatcher.BeginInvoke(() => { if (m == "details") ShowDetails(); else if (m == "game") popup?.Content2.ToggleGameExternal(); else ShowPopup(); });
            instance.Listen();
        }
        // Autostart só muda quando o usuário mexe no toggle (OnSettingsChanged). Rodar o exe não altera o registro.
        service.Start();
        if (opt.Details) ShowDetails();
        // --tray (tarefa do logon) respeita "Iniciar oculto"; abertura manual (atalho) sempre mostra o popup.
        else if (!opt.Tray || !state.Settings.StartHidden) ShowPopup();
        UpdateCollection(); // inicia só na bandeja: agenda o trim de memória
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        MenuItem Item(string text, Action a) { var i = new MenuItem { Header = text }; i.Click += (_, _) => a(); return i; }
        m.Items.Add(Item("Abrir", ShowPopup));
        m.Items.Add(Item("Detalhes", ShowDetails));
        m.Items.Add(Item("Configurações", () => ShowPopup(settings: true)));
        m.Items.Add(Item("Telas do teclado", ShowKeyboard));
        m.Items.Add(Item("Controle térmico", ShowLcd));
        m.Items.Add(Item("Sobre", ShowAbout));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Sair", ExitApp));
        return m;
    }

    private void TogglePopup()
    {
        if (popup == null) return;
        if (popup.IsVisible) { popup.HideNow(); return; }
        // O clique na tray desativa o popup antes de chegar aqui; evitar reabrir logo em seguida.
        if ((DateTime.UtcNow - popup.HiddenByDeactivateAt).TotalMilliseconds < 350) return;
        ShowPopup();
    }

    private void ShowPopup() => ShowPopup(false);
    private void ShowPopup(bool settings)
    {
        Native.GetCursorPos(out var p);
        if (service.Latest is { } s) popup!.Content2.Update(s);
        popup!.ShowAt(p, settings);
    }

    /// <summary>Clique num card de temperatura: abre o Detalhes no sensor (ou na categoria, sem sensor do LHM).</summary>
    private void ShowDetailsAt(string? sensorId, string category)
    {
        ShowDetails();
        details?.Body.FocusSensor(sensorId, category);
    }

    private void ToggleDetails()
    {
        if (details is { IsVisible: true }) details.Close();
        else ShowDetails();
    }

    private void ShowDetails()
    {
        if (details == null)
        {
            details = new DetailsWindow(state, service, fans);
            details.QuitRequested += ExitApp;
            details.Closed += (_, _) =>
            {
                details = null; service.DetailsOpen = false; UpdateCollection();
                popup?.Content2.SetDetailsOpen(false);
                Task.Run(() => { try { KnownSave(); } catch (Exception) { } });
            };
            details.IsVisibleChanged += (_, _) => UpdateCollection();
        }
        service.DetailsOpen = true;
        UpdateCollection();
        details.Show();
        if (details.WindowState == WindowState.Minimized) details.WindowState = WindowState.Normal;
        details.Activate();
        popup?.Content2.SetDetailsOpen(true);
        if (service.Latest is { } s) details.Update(s);
    }

    private AboutWindow? about;
    private void ShowAbout()
    {
        if (about == null)
        {
            var snap = service.Latest;
            about = new AboutWindow(state, fans, snap?.Devices.Count ?? 0, snap?.EntryCount ?? 0);
            about.Closed += (_, _) => about = null;
        }
        about.Show(); about.Activate();
    }

    private void KnownSave() { /* gravação feita pelo SensorService ao sair; aqui reservado para o fechamento do Detalhes */ service.SaveKnownDevices(); }

    /// <summary>
    /// Teclado (OLED + rodinha), portado do GPU by MarzOps. Usa os mesmos sensores do app (LHM fica ativo enquanto o OLED estiver ligado).
    /// A rodinha recebe WM_INPUT numa janela só de mensagens nesta thread de UI.
    /// </summary>
    private void StartKeyboard()
    {
        try
        {
            kbValues = new KeyboardValues(service, () => fans?.State);
            keyboard = new KeyboardService(KeyboardPath, kbValues.Value, kbValues.Name);
            if (!File.Exists(KeyboardPath)) keyboard.Save(keyboard.Config); // grava o perfil (padrão ou migrado do GPU by MarzOps)
            UpdateKeepHardware();
            keyboard.Changed += s => { UpdateKeepHardware(); Dispatcher.BeginInvoke(() => popup?.Content2.UpdateKeyboard(s)); };
            var prm = new System.Windows.Interop.HwndSourceParameters("MarzMonitor.Keyboard") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
            kbSink = new System.Windows.Interop.HwndSource(prm);
            kbSink.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) => { if (msg is 0x00FF or 0x00FE) keyboard?.OnWindowMessage(msg, l); return IntPtr.Zero; });
            keyboard.AttachWindow(kbSink.Handle);
            keyboard.Start();
            popup?.Content2.UpdateKeyboard(keyboard.State);
        }
        catch (Exception ex) { Crash(ex); }
    }

    /// <summary>
    /// ROG RYUJIN II: leitura sempre (só leitura); LCD só se ligado em ryujin-lcd.json (padrão desligado).
    /// Sem cooler conectado o serviço fica tentando abrir a cada 10 s, sem custo.
    /// </summary>
    private void StartRyujin()
    {
        try
        {
            kbValues ??= new KeyboardValues(service, () => fans?.State);
            var kv = kbValues;
            ryujin = new MonitorTray.Ryujin.RyujinService(LcdPath, kv.Value);
            UpdateKeepHardware();
            ryujin.Changed += _ => UpdateKeepHardware();
            ryujin.FanChanged += _ => Dispatcher.BeginInvoke(UpdateKeepHardware);
            ryujin.Start();
            popup?.Content2.AttachCooler(ryujin);
        }
        catch (Exception ex) { Crash(ex); }
    }

    /// <summary>LHM fica ativo com a bandeja fechada se o OLED do teclado, o LCD do cooler ou a janela do cooler precisarem de sensores.</summary>
    private void UpdateKeepHardware() =>
        service.KeepHardware = keyboard?.Config.Enabled == true || ryujin?.Config.Enabled == true || ryujin?.FanConfig.Enabled == true || lcdWindow != null || asus?.Off.Any == true
            || state.Settings.TrayTemp || state.Settings.TrayAlert; // ícone da bandeja com temperatura/alerta precisa das leituras com o popup fechado

    // ---- Modo game automático: processos a cada 3 s (só com jogos na lista ou enquanto o automático for dono do modo game).
    private System.Windows.Threading.DispatcherTimer? gameTimer;
    private bool gamePolling;

    private void StartGameWatch()
    {
        gameTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        gameTimer.Tick += async (_, _) =>
        {
            if (gamePolling || exiting || popup == null) return;
            if (state.Settings.GameExes.Count == 0 && !popup.Content2.AutoGameOwned) return;
            gamePolling = true;
            try
            {
                var names = await Task.Run(RunningProcessNames);
                if (!exiting) popup.Content2.AutoGameStep(names);
            }
            catch (Exception ex) { Crash(ex); }
            finally { gamePolling = false; }
        };
        gameTimer.Start();
    }

    /// <summary>Nomes dos processos (sem ".exe"); só a lista, sem abrir nenhum processo (não precisa de admin).</summary>
    private static IReadOnlyList<string> RunningProcessNames()
    {
        var ps = System.Diagnostics.Process.GetProcesses();
        try { return ps.Select(p => { try { return p.ProcessName; } catch (Exception) { return ""; } }).ToList(); }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    // ---- Temperaturas da sessão e ícone da bandeja (maior temperatura e/ou cor de alerta).
    private readonly TempSession temps = new();
    private string trayIconKey = "";
    private string? trayIconLabel;

    private Dictionary<string, float> MergedTemps(Snapshot s)
    {
        var r = fans?.State.Reading;
        return TempCards.Merge(s.Temps, ryujin?.Latest?.Pump?.WaterTempC, r?.TemperatureC, r?.Telemetry.HotspotC, r?.Telemetry.MemoryTemperatureC);
    }

    private void UpdateTrayIcon(IReadOnlyDictionary<string, float> values)
    {
        if (tray == null) return;
        var st = state.Settings;
        var hot = st.TrayTemp || st.TrayAlert ? TempCards.ForTray(st.TrayTemps, st.PopupTemps, values, Environment.TickCount64, st.TrayRotateSec) : null;
        trayIconLabel = st.TrayTemp && hot != null && TempCards.Find(hot.Id) is { } hd ? $"Ícone: {hd.Label} {Math.Round(hot.Value):0}°" : null;
        bool light = TrayIcons.TaskbarIsLight();
        int px = TrayIconRenderer.SystemSize();
        var level = st.TrayAlert && hot != null ? hot.Worst : TempLevel.Normal;
        string key = hot == null || (!st.TrayTemp && level == TempLevel.Normal) ? $"fixo|{light}"
            : st.TrayTemp ? $"temp|{hot.Id}|{Math.Round(hot.Value, MidpointRounding.AwayFromZero)}|{level}|{light}|{px}|{st.TrayFontSize}"
            : $"alerta|{level}|{light}|{px}";
        if (key == trayIconKey) return;
        trayIconKey = key;
        if (key.StartsWith("fixo", StringComparison.Ordinal)) { tray.UpdateIcon(TrayIcons.For(light)); return; }
        using var bmp = st.TrayTemp ? TrayIconRenderer.DrawTemp(px, hot!.Value, level, light, Settings.TrayFontScale(st.TrayFontSize)) :TrayIconRenderer.Tint(TrayIcons.For(light, px), px, TrayIconRenderer.ColorFor(level, light));
        tray.UpdateIcon(iconRenderer.Make(bmp));
    }

    private void ShowLcd()
    {
        if (ryujin == null || kbValues == null) { MessageBox.Show("Cooler indisponível nesta execução (modo simulado).", "Marz Monitor"); return; }
        if (lcdWindow == null)
        {
            lcdWindow = new LcdWindow(state, ryujin, kbValues, service, asus, fans);
            lcdWindow.Closed += (_, _) => { lcdWindow = null; UpdateKeepHardware(); };
        }
        UpdateKeepHardware();
        lcdWindow.Show(); lcdWindow.Activate();
    }

    private static void Crash(Exception ex)
    {
        try { var d = Path.Combine(AppState.DataDir, "logs"); Directory.CreateDirectory(d); File.AppendAllText(Path.Combine(d, "errors.log"), $"{DateTimeOffset.Now:O} {ex}\r\n"); } catch (Exception) { }
    }

    private void ShowKeyboard()
    {
        if (keyboard == null || kbValues == null) { MessageBox.Show("Teclado indisponível nesta execução (modo simulado ou SteelSeries GG ausente).", "Marz Monitor"); return; }
        kbWindow ??= new KeyboardWindow(state, keyboard, kbValues, service);
        kbWindow.Closed += (_, _) => { kbWindow = null; UpdateCollection(); };
        service.KeepHardware = true;
        kbWindow.Show(); kbWindow.Activate();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);
    private System.Windows.Threading.DispatcherTimer? trimTimer;

    /// <summary>
    /// Com popup, Detalhes e editor do teclado fechados: depois de 10 s compacta o heap e devolve as páginas ao Windows;
    /// repete a cada 5 min enquanto ficar só na bandeja. Nada muda no comportamento; ao reabrir as páginas voltam sob demanda.
    /// </summary>
    private void ScheduleTrim(bool hidden)
    {
        trimTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);
        trimTimer.Stop();
        if (!hidden || exiting) return;
        trimTimer.Interval = TimeSpan.FromSeconds(10);
        trimTimer.Tick -= OnTrim; trimTimer.Tick += OnTrim;
        trimTimer.Start();
    }

    private void OnTrim(object? sender, EventArgs e)
    {
        trimTimer!.Interval = TimeSpan.FromMinutes(5);
        if (popup?.IsVisible == true || details?.IsVisible == true || kbWindow?.IsVisible == true) { trimTimer.Stop(); return; }
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1);
        }
        catch (Exception) { }
    }

    private void UpdateCollection()
    {
        ScheduleTrim(popup?.IsVisible != true && details?.IsVisible != true && kbWindow?.IsVisible != true);
        service.FullCollection = popup?.IsVisible == true || details?.IsVisible == true;
    }

    private void OnPublished(Snapshot snap) { if (!exiting) Dispatcher.BeginInvoke(() => OnSnapshot(snap)); }

    private void OnSnapshot(Snapshot s)
    {
        if (exiting) return; // snapshots ainda enfileirados durante a saída (ícone/janelas já descartados)
        // Fans da placa desligados pelo popup: CPU > 85 °C, GPU > 80 °C ou sem leitura da CPU religa todos (popup aberto ou não).
        asus?.SafetyCheck(s.Summary.CpuTemp, s.Summary.GpuTemp ?? (fans?.State.Reading?.TemperatureC is uint g ? (float?)g : null));
        var t = MergedTemps(s);
        temps.Push(t); // mínimo/máximo da sessão mesmo com o popup fechado
        if (popup is { IsVisible: true }) { popup.Content2.Update(s); popup.Content2.UpdateFans(fans?.State); }
        details?.Update(s);
        UpdateTray(s);
        UpdateTrayIcon(t);
    }

    /// <summary>
    /// Primeira vez deste ícone: deixa visível na barra (HKCU\Control Panel\NotifyIconSettings\…\IsPromoted=1).
    /// Só quando o valor ainda não existe — se o usuário esconder depois, a escolha dele vale.
    /// </summary>
    private static void PromoteTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath ?? ""; var name = Path.GetFileName(exe);
            var sid = Path.GetFileName(Path.GetDirectoryName(exe) ?? "");
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return;
            foreach (var k in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(k, writable: true);
                var path = key?.GetValue("ExecutablePath") as string ?? "";
                bool mine = path.Equals(exe, StringComparison.OrdinalIgnoreCase) || (path.EndsWith(@"\" + sid + @"\" + name, StringComparison.OrdinalIgnoreCase) && path.Contains(@"\MonitorTray\", StringComparison.OrdinalIgnoreCase));
                if (mine && key!.GetValue("IsPromoted") == null) key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }
        catch (Exception) { }
    }

    private void UpdateTray(Snapshot s)
    {
        if (tray == null) return;
        var tip = TrayTip(s.Summary, fans?.State);
        if (trayIconLabel != null) { tip = trayIconLabel + "\n" + tip; if (tip.Length > 127) tip = tip[..127]; }
        if (tip != lastTooltip) { lastTooltip = tip; tray.ToolTipText = tip; }
    }

    /// <summary>
    /// Tooltip da bandeja (limite do Windows: 127 caracteres). Funciona com o popup fechado:
    /// CPU/memória/disco/rede vêm do PDH; GPU (uso e °C) vem da NVML do serviço de fans.
    /// </summary>
    internal static string TrayTip(SummaryData s, FanState? f)
    {
        static string P(double? v) => v is double d ? Math.Round(d).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—";
        const double GiB = 1024d * 1024 * 1024;
        var cpu = "CPU " + P(s.CpuLoad) + (s.CpuTemp is float ct ? $" {Math.Round(ct):0}°C" : "");
        var mem = s.MemTotal == 0 ? "MEM —" : $"MEM {s.MemUsed / GiB:0.0}/{Math.Round(s.MemTotal / GiB):0} GB";
        var r = f?.Reading;
        double? gl = s.GpuLoad ?? r?.Telemetry.GpuUtilPercent;
        double? gt = s.GpuTemp ?? r?.TemperatureC;
        var gpu = "GPU " + P(gl) + (gt is double t ? $" {Math.Round(t):0}°C" : "")
                  + (f is { Mode: FanMode.Manual, RequestedPercent: uint rp } ? $" · fan {rp}%" : "");
        var disk = $"Disco L {Formatter.DiskMBps(s.DiskReadBps)} · E {Formatter.DiskMBps(s.DiskWriteBps)} MB/s";
        var net = $"Rede ↓ {Formatter.Net(s.NetDownBps)} ↑ {Formatter.Net(s.NetUpBps)}";
        var tip = string.Join("\n", cpu + "  " + mem, gpu, disk, net);
        return tip.Length <= 127 ? tip : tip[..127];
    }

    private void OnSettingsChanged(Settings old, Settings now)
    {
        Theme.Apply(Resources, now);
        Formatter.NetBits = now.NetUnit == "bits";
        if (old.RefreshMs != now.RefreshMs) service.RefreshMs = now.RefreshMs;
        service.HistorySeconds = now.History;
        if (old.Autostart != now.Autostart) Task.Run(() => Autostart.Apply(now.Autostart));
        if (old.TrayTemp != now.TrayTemp || old.TrayAlert != now.TrayAlert) UpdateKeepHardware();
        // Opção "DESLIGAR FAN DA PLACA" desligada: o card não religa mais pelo clique, então religa os desligados agora.
        if (old.BoardFanOff && !now.BoardFanOff && asus is { Off.Any: true }) asus.TurnOn(asus.Off.Off, "opção desligada");
        if (service.Latest is { } s) OnSnapshot(s);
    }

    private void OnUserPreference(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
            Dispatcher.BeginInvoke(() =>
            {
                trayIconKey = ""; // barra clara/escura mudou: redesenha o ícone (fixo ou com temperatura)
                if (tray != null) { if (service.Latest is { } s) UpdateTrayIcon(MergedTemps(s)); else tray.UpdateIcon(TrayIcons.Current()); }
                Theme.Apply(Resources, state.Settings); popup?.ApplyBlur(); details?.ApplyBlur();
            });
    }

    private void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) service.OnResume();
    }

    public void ExitApp()
    {
        if (exiting) return;
        popup?.Content2.BeginShutdown();
        // Fans primeiro: devolve ao automático e confirma por fan. Sem confirmação, o app NÃO fecha
        // (um processo encerrado com fan manual deixaria a placa numa velocidade fixa).
        if (fans != null && !fans.TryClose(TimeSpan.FromSeconds(5)))
        {
            MessageBox.Show("Não foi possível confirmar a volta das fans ao automático:\n\n" + fans.State.Error +
                "\n\nO Marz Monitor continua aberto. Tente \"AUTO\" no Detalhes e saia de novo.", "Marz Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowDetails();
            return;
        }
        exiting = true;
        try { asus?.RestoreAndWait(TimeSpan.FromSeconds(8)); asus?.Dispose(); } catch (Exception) { } // volta às curvas do usuário
        service.Published -= OnPublished;
        try { state.Flush(); } catch (Exception) { }
        details?.Close();
        popup?.Close();
        gameTimer?.Stop();
        tray?.Dispose();
        iconRenderer.Dispose();
        kbWindow?.Close();
        keyboard?.Dispose(); kbSink?.Dispose();
        lcdWindow?.Close(); ryujin?.Dispose();
        service.Dispose();
        fans?.Dispose();
        quitSignal?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreference;
        SystemEvents.PowerModeChanged -= OnPower;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Logoff/desligamento: melhor esforço, sem diálogo.
        try { fans?.TryClose(TimeSpan.FromSeconds(4)); } catch (Exception) { }
        exiting = false; fans?.Dispose(); fans = null;
        ExitApp();
        base.OnSessionEnding(e);
    }
}
