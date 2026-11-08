using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MonitorTray.App.Controls;
using MonitorTray.App.Views;
using MonitorTray.Core.Detail;
using MonitorTray.Core.Model;
using MonitorTray.Gpu;
using SD = System.Drawing;

namespace MonitorTray.App;

/// <summary>
/// --preview &lt;dir&gt; [--simulate &lt;json&gt;]: renders the screens off-screen (no tray, no hardware) to PNG.
/// Uses simulated data; meant for reviewing the visuals without installing anything.
/// </summary>
public static class Preview
{
    public static int Run(Options opt)
    {
        FanChoices.Enabled = false; // preview never saves the user's choices
        var dir = Path.GetFullPath(opt.Preview!);
        Directory.CreateDirectory(dir);
        var sim = opt.Simulate ?? Path.Combine(AppContext.BaseDirectory, "sim", "sensors.json");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Controls.ScrollChart.AnimationsEnabled = false;
        PopupContent.SettingsMaxHeight = double.PositiveInfinity; // whole Settings page in the PNG
        var tmp = Path.Combine(Path.GetTempPath(), "mt-preview-" + Guid.NewGuid().ToString("N"));
        var state = new AppState(tmp);
        App.InitResources(app, state.Settings);
        var service = App.CreateService(new Options { Simulate = sim, FakeNew = opt.FakeNew }, tmp);
        service.RefreshMs = 50; service.FullCollection = true; service.DetailsOpen = true;
        service.Start();
        var until = DateTime.UtcNow.AddSeconds(20);
        while ((service.Latest?.Devices.Count ?? 0) == 0 && DateTime.UtcNow < until) Thread.Sleep(50);
        Thread.Sleep(400);
        service.RefreshMs = 100000;
        // Real drives of this machine (read-only) to review the disk list in the popup.
        var vr = new MonitorTray.Sensors.Volumes(); vr.WithRates(vr.Read()); Thread.Sleep(600); var snap = service.Latest! with { Volumes = vr.WithRates(vr.Read()) };
        var outputs = new List<string>();
        var fans = new GpuFanService(() => new SimulatedGpuApi(), takeMutex: false);
        fans.Start();
        while (fans.State.Reading == null && DateTime.UtcNow < until) Thread.Sleep(50);

        void Popup(string name, Settings s, bool settings)
        {
            state.Update(_ => s);
            App.InitResources(app, s);
            var pc = new PopupContent(state);
            pc.Update(snap);
            pc.AttachFans(fans);
            pc.ShowSettings(settings);
            outputs.Add(Save(Path.Combine(dir, name), Scene(new BlueprintFrame(pc, shadow: false, outline: false), 344, double.NaN, s), 2));
        }
        var dark = Settings.Default;
        Popup("01-popup-summary-dark.png", dark, false);
        Popup("02-popup-settings-dark.png", dark, true);
        Popup("03-popup-summary-light.png", dark with { Theme = "light" }, false);
        Popup("04-popup-settings-light.png", dark with { Theme = "light" }, true);
        foreach (var (i, p) in new[] { (5, "neon"), (6, "lime"), (7, "ember"), (8, "violet"), (9, "steel") })
            Popup($"{i:00}-palette-{p}.png", dark with { Palette = p }, false);

        void Details(string name, Settings s, Func<Device, bool> pick, Filters f, double h = 640)
        {
            state.Update(_ => s);
            App.InitResources(app, s);
            var dc = new DetailsContent(state, service, fans);
            dc.PreviewSet(snap.Devices.FirstOrDefault(pick)?.Id, f);
            dc.Update(snap);
            outputs.Add(Save(Path.Combine(dir, name), Scene(new BlueprintFrame(dc, shadow: false), 960, h, s), 1));
        }
        Details("10-details-cpu.png", dark, d => d.Category == "CPU", Filters.Default, 1500);
        var cpuId = snap.Devices.First(d => d.Category == "CPU").Id;
        Details("11-details-cpu-clock-avg.png", dark with { CardPicks = new Dictionary<string, string> { [cpuId + "|Clock"] = "avg" } }, d => d.Category == "CPU", Filters.Default, 640);
        Details("13-details-gpu-auto.png", dark, d => d.Category == "GPU", Filters.Default, 900);
        fans.CommandAsync(50).Wait();
        Thread.Sleep(1100);
        Details("14-details-gpu-manual50.png", dark, d => d.Category == "GPU", Filters.Default, 900);
        Popup("01b-popup-fans-manual.png", dark, false);
        Details("15-details-memory.png", dark, d => d.Family == "Memory", Filters.Default, 900);
        Details("17-details-motherboard.png", dark, d => d.Category == "Motherboard", Filters.Default, 900);
        Details("19-details-ssd.png", dark, d => d.Family == "Storage", Filters.Default, 900);
        Details("21-details-ssd-zeros-limits.png", dark, d => d.Family == "Storage", Filters.Default with { ShowZero = true, ShowLimits = true }, 900);
        Details("22-details-network.png", dark, d => d.Category == "Network", Filters.Default, 900);
        Details("23-details-search-fan.png", dark, _ => false, Filters.Default with { Query = "fan" });
        Details("24-details-search-empty.png", dark, _ => false, Filters.Default with { Query = "xyzzy" }, 440);
        Details("26-details-light.png", dark with { Theme = "light" }, d => d.Category == "CPU", Filters.Default);
        if (opt.FakeNew) Details("27-details-new-psu.png", dark, d => d.Category == "PSU", Filters.Default, 640);

        {
            state.Update(_ => dark); App.InitResources(app, dark);
            var aw = new AboutWindow(state, fans, snap.Devices.Count, snap.EntryCount);
            var body = (FrameworkElement)aw.Content; aw.Content = null;
            outputs.Add(Save(Path.Combine(dir, "30-about.png"), Scene(body, 432, double.NaN, dark), 2));
        }

        {
            // Keyboard: fake transport (nothing is sent to GG), simulated sensors.
            state.Update(_ => dark); App.InitResources(app, dark);
            var kv = new KeyboardValues(service, () => fans.State);
            var kb = new MonitorTray.Oled.KeyboardService(Path.Combine(tmp, "kb.json"), kv.Value, kv.Name, new NullTransport());
            var pc = new PopupContent(state); pc.Update(snap); pc.AttachFans(fans); pc.UpdateKeyboard(kb.State with { Delivery = new(true, "ok", null, DateTimeOffset.UtcNow, ["CPU load  7%", "CPU temp 61 C"]) });
            outputs.Add(Save(Path.Combine(dir, "01c-popup-keyboard.png"), Scene(new BlueprintFrame(pc, shadow: false), 344, double.NaN, dark), 2));
            MonitorTray.Core.Format.Formatter.NetBits = true;
            var pb = new PopupContent(state); pb.Update(snap); pb.AttachFans(fans);
            outputs.Add(Save(Path.Combine(dir, "01d-popup-mbps.png"), Scene(new BlueprintFrame(pb, shadow: false), 344, double.NaN, dark), 2));
            MonitorTray.Core.Format.Formatter.NetBits = false;
            {
                var acf = AsusFanModes.Fake(tmp, ["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4", "Water Pump+", "AIO Pump"]);
                state.Update(_ => dark with { AcModes = ["curves", "turbo", "full"] });
                var snapR = snap with { } ; snapR = new Snapshot(snap.At, snap.Summary, snap.NetDownHistory, snap.NetUpHistory, snap.Devices, snap.EntryCount, snap.Status) { MbTemps = new Dictionary<string, float> { ["Motherboard"] = 46, ["Chipset"] = 88, ["VRM"] = 60 }, MbFanRpm = new Dictionary<int, float> { [0] = 1408, [2] = 629, [3] = 620, [4] = 641 }, HistoryPoints = snap.HistoryPoints, CpuHistory = snap.CpuHistory, GpuHistory = snap.GpuHistory, MemHistory = snap.MemHistory, Volumes = snap.Volumes };
                var pa = new PopupContent(state); pa.Update(snapR); pa.AttachFans(fans); pa.AttachAsus(acf);
                outputs.Add(Save(Path.Combine(dir, "01h-popup-board-modes.png"), Scene(new BlueprintFrame(pa, shadow: false), 344, double.NaN, dark), 2));
                var pb2 = new PopupContent(state); pb2.Update(snap); pb2.AttachFans(fans); pb2.AttachAsus(acf); pb2.ShowSettings(true);
                outputs.Add(Save(Path.Combine(dir, "02c-settings-board-modes.png"), Scene(new BlueprintFrame(pb2, shadow: false), 344, double.NaN, dark), 2));
                state.Update(_ => dark);
            }
            state.Update(_ => dark with { History = 7200 }); service.HistorySeconds = 7200;
            Thread.Sleep(1200);
            var p2 = new PopupContent(state); p2.Update(service.Latest ?? snap); p2.AttachFans(fans);
            outputs.Add(Save(Path.Combine(dir, "01e-popup-2h.png"), Scene(new BlueprintFrame(p2, shadow: false), 344, double.NaN, dark), 2));
            state.Update(_ => dark); service.HistorySeconds = 60;
            var kw = new KeyboardWindow(state, kb, kv, service);
            var body = (FrameworkElement)kw.Content; kw.Content = null;
            outputs.Add(Save(Path.Combine(dir, "31-keyboard.png"), Scene(body, 772, 572, dark), 1));
            // Cooler: service not started (no USB access); just the window with the default configuration.
            var ry = new MonitorTray.Ryujin.RyujinService(Path.Combine(tmp, "lcd.json"), kv.Value);
            ry.Simulate(new MonitorTray.Ryujin.RyujinSnapshot(DateTimeOffset.Now, "AURJ1-S750-0104", new(34.8, 1650, 3480),
                new([1020, 1020, 1050], 130), 51, 60), new(51, 51, 60, null), 48.0);
            var lw = new LcdWindow(state, ry, kv, service, MonitorTray.App.AsusFanModes.Fake(tmp, ["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"]), fans);
            lw.ShowTab(false);
            var lbody = (FrameworkElement)lw.Content; lw.Content = null;
            outputs.Add(Save(Path.Combine(dir, "32-cooler-lcd.png"), Scene(lbody, 1300, 860, dark), 1));
            var pw = new PopupContent(state); pw.Update(snap); pw.AttachFans(fans);
            pw.AttachAsus(MonitorTray.App.AsusFanModes.Fake(tmp, ["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4"]));
            state.Update(_ => dark with { AcModes = ["curves", "full"] });
            pw.AttachCooler(ry);
            outputs.Add(Save(Path.Combine(dir, "01i-popup-water-cooler.png"), Scene(new BlueprintFrame(pw, shadow: false, outline: false), 344, double.NaN, dark), 2));
            // Full new popup: board with the 5 modes, CHA1 off (UI state only, no hardware access) and the RYUJIN.
            {
                var all = dark with { AcModes = AsusFanModes.AllModes, BoardFanOff = true, GameExes = ["cs2.exe", "eldenring.exe"], TrayTemp = true, TrayAlert = true };
                state.Update(_ => all); App.InitResources(app, all);
                var acAll = AsusFanModes.Fake(tmp, ["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4", "Water Pump+", "AIO Pump"]);
                // CHA1 off: control % 0 and rpm 0. CPU Fan (output 1) has a % but its rpm is not measured: the card shows "—".
                var ctl = new Dictionary<int, float>(snap.MbFanControl) { [0] = 0 };
                var rpm = new Dictionary<int, float>(snap.MbFanRpm) { [0] = 0, [1] = 1268 };
                var snapOff = snap with { MbFanControl = ctl, MbFanRpm = rpm };
                var pn = new PopupContent(state); pn.AttachFans(fans); pn.AttachAsus(acAll); pn.AttachCooler(ry); pn.Update(snapOff);
                pn.PreviewBoardOff("Chassis Fan 1"); pn.PreviewBoardManual("Chassis Fan 3", 40); pn.Update(snapOff);
                outputs.Add(Save(Path.Combine(dir, "01j-popup-new-off.png"), Scene(new BlueprintFrame(pn, shadow: false, outline: false), 344, double.NaN, all), 2));
                // Quick card (right click): board in MANUAL 40% and the cooler radiator. UI only.
                var q1 = pn.QuickCard("board", "Chassis Fan 3");
                outputs.Add(Save(Path.Combine(dir, "01p-quick-board.png"), Scene(q1, 218, double.NaN, all), 2));
                var q2 = pn.QuickCard("cooler", MonitorTray.Core.Fans.ThermalMap.RadiatorId);
                outputs.Add(Save(Path.Combine(dir, "01q-quick-radiator.png"), Scene(q2, 218, double.NaN, all), 2));
                // 125% (fractional DPI) over a white background: any seam/lower-alpha outline at the edge would show up as a light line.
                var p125 = new PopupContent(state); p125.AttachFans(fans); p125.AttachAsus(acAll); p125.AttachCooler(ry); p125.Update(snapOff);
                var f125 = new BlueprintFrame(p125, shadow: false, outline: false) { Width = 344, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, UseLayoutRounding = true };
                outputs.Add(Save(Path.Combine(dir, "01o-popup-125-light-bg.png"), new Border { Background = Brushes.White, Child = f125 }, 1.25));
                var pt = new PopupContent(state); pt.AttachFans(fans); pt.AttachAsus(acAll); pt.AttachCooler(ry); pt.Update(snapOff); pt.ShowSettings(true);
                outputs.Add(Save(Path.Combine(dir, "02j-settings-new-popup.png"), Scene(new BlueprintFrame(pt, shadow: false, outline: false), 344, double.NaN, all), 2));
                // Different order/choice, a hot card (chipset 89 °C) and fans renamed by the user (a 16-character name is cut with an ellipsis).
                var custom = all with { PopupTemps = ["liq", "cpu", "coremax", "socket", "mobo", "chip"], PopupBoardFans = ["Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3"], PopupCoolerFans = ["pump", "embedded"],
                    AcFanNames = new Dictionary<string, string> { ["Chassis Fan 1"] = "Exhaust", ["Chassis Fan 3"] = "Rear exhaust top" }, CoolerNames = new Dictionary<string, string> { ["pump"] = "Main pump" } };
                state.Update(_ => custom);
                var hot = new Dictionary<string, float>(snap.Temps) { ["chip"] = 89 };
                var pk = new PopupContent(state); pk.AttachFans(fans); pk.AttachAsus(acAll); pk.AttachCooler(ry); pk.Update(snapOff with { Temps = hot });
                outputs.Add(Save(Path.Combine(dir, "01k-popup-choices.png"), Scene(new BlueprintFrame(pk, shadow: false, outline: false), 344, double.NaN, custom), 2));
                // Rename in the quick card (inline box) and the FAN NAMES list in Settings, with the names above.
                outputs.Add(Save(Path.Combine(dir, "01r-quick-rename.png"), Scene(pk.QuickCard("board", "Chassis Fan 1", rename: true), 218, double.NaN, custom), 2));
                var pkn = new PopupContent(state); pkn.AttachFans(fans); pkn.AttachAsus(acAll); pkn.AttachCooler(ry); pkn.Update(snapOff); pkn.ShowSettings(true);
                outputs.Add(Save(Path.Combine(dir, "02k-settings-fan-names.png"), Scene(new BlueprintFrame(pkn, shadow: false, outline: false), 344, double.NaN, custom), 2));
                state.Update(_ => all);
                // Top strip: game mode turned on by the game (all 100%). UI state only, nothing is commanded.
                var pg = new PopupContent(state); pg.AttachFans(fans); pg.AttachAsus(acAll); pg.AttachCooler(ry); pg.Update(snapOff);
                pg.PreviewGame("cs2.exe");
                outputs.Add(Save(Path.Combine(dir, "01l-popup-game-strip.png"), Scene(new BlueprintFrame(pg, shadow: false, outline: false), 344, double.NaN, all), 2));
                // Strip: the OFF protection turned CHA1 back on (CPU 86 °C) + game mode turned on by hand (GPU 85%).
                state.Update(_ => all with { GameGpuPercent = 85 });
                var pr = new PopupContent(state); pr.AttachFans(fans); pr.AttachAsus(acAll); pr.AttachCooler(ry); pr.Update(snapOff);
                pr.PreviewSafety(new MonitorTray.Core.Format.SafetyEvent(DateTimeOffset.Now.AddSeconds(-40), ["Chassis Fan 1"], 86, 62));
                pr.PreviewGame(null);
                outputs.Add(Save(Path.Combine(dir, "01m-popup-strip-back-on.png"), Scene(new BlueprintFrame(pr, shadow: false, outline: false), 344, double.NaN, all), 2));
                state.Update(_ => all); acAll.LastSafety = null; // the notice belongs to the previous scene
                // Alerts from the protection limits: CPU near (82 of 85), GPU at the limit (81 of 80), liquid near (46.5 of 50);
                // GPU card tooltip with the session min/max.
                var alert = new Dictionary<string, float>(snap.Temps) { ["cpu"] = 82, ["gpu"] = 81, ["chip"] = 60 };
                var pa2 = new PopupContent(state); pa2.AttachFans(fans); pa2.AttachAsus(acAll); pa2.AttachCooler(ry);
                pa2.Session.Push(new Dictionary<string, float> { ["gpu"] = 38, ["cpu"] = 41 }); pa2.Session.Push(new Dictionary<string, float> { ["gpu"] = 84, ["cpu"] = 88 });
                pa2.Update(snapOff with { Temps = alert });
                var tipBox = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 6), Margin = new Thickness(24, 0, 24, 24), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = pa2.TempTip("gpu") ?? "", FontFamily = Theming.AppFonts.Body, FontSize = 12 } };
                tipBox.SetResourceReference(Border.BackgroundProperty, Theming.Theme.Bg); tipBox.SetResourceReference(Border.BorderBrushProperty, Theming.Theme.Line);
                ((TextBlock)tipBox.Child).SetResourceReference(TextBlock.ForegroundProperty, Theming.Theme.Text);
                var frameA = Scene(new BlueprintFrame(pa2, shadow: false, outline: false), 344, double.NaN, all);
                var bgA = ((Border)frameA).Background; var inner = ((Border)frameA).Child; ((Border)frameA).Child = null;
                outputs.Add(Save(Path.Combine(dir, "01n-popup-alerts-tooltip.png"), new Border { Background = bgA, Child = new StackPanel { Children = { inner, new TextBlock { Text = "GPU card tooltip (mouse hover):", Margin = new Thickness(24, 0, 0, 4), Foreground = Brushes.Gray, FontSize = 11 }, tipBox } } }, 2));
                App.InitResources(app, dark);
            }
            ThermalScenes(dir, outputs, state, app, ry, kv, service, fans, snap, tmp);
            state.Update(_ => dark);
            ry.Dispose();
        }

        // Tray icon in 3 states, enlarged 8×.
        var icons = new[] { (0.0, 0.0), (0.5, 0.17), (1.0, 1.0) };
        for (int i = 0; i < icons.Length; i++)
            foreach (var px in new[] { 16, 24, 32 })
            {
                var p = Theming.Theme.Palette("electric");
                using var bmp = Tray.TrayIconRenderer.Draw(px, icons[i].Item1, icons[i].Item2, p.A, p.B);
                var f = Path.Combine(dir, $"tray-{i}-{px}.png");
                bmp.Save(f, System.Drawing.Imaging.ImageFormat.Png);
                outputs.Add(f);
            }
        outputs.Add(TraySheet(Path.Combine(dir, "tray-temperature.png")));

        fans.TryClose(TimeSpan.FromSeconds(3)); fans.Dispose();
        service.Dispose();
        try { Directory.Delete(tmp, true); } catch (Exception) { }
        File.WriteAllLines(Path.Combine(dir, "index.txt"), outputs);
        return 0;
    }

    /// <summary>
    /// Thermal control (mock 03e): RYUJIN II with the radiator being edited, board with CHA_FAN1 off, board in CURVES (editable)
    /// and GPU. UI state only: the cooler is simulated (no USB), the board is AsusFanModes.Fake (no COM), the GPU is simulated and
    /// Apply is never called. The cooler config goes to the preview's temp directory.
    /// </summary>
    private static void ThermalScenes(string dir, List<string> outputs, AppState state, Application app, MonitorTray.Ryujin.RyujinService ry, KeyboardValues kv,
        MonitorTray.Sensors.SensorService service, GpuFanService fans, Snapshot snap, string tmp)
    {
        var dark = Settings.Default;
        state.Update(_ => dark); App.InitResources(app, dark);
        // Armoury Crate curves per mode (°C, PWM 0..255): values from the service log (see asus-rog-protocol-notes).
        int[][] std = [[20, 51], [45, 102], [65, 179], [70, 255]], sil = [[21, 46], [46, 102], [66, 102], [71, 102]], tur = [[20, 102], [40, 153], [55, 204], [65, 255]];
        IReadOnlyDictionary<string, int[][]> Modes(int[][] user) => new Dictionary<string, int[][]> { ["curves"] = user, ["standard"] = std, ["silent"] = sil, ["turbo"] = tur };
        int[][] pump = [[20, 204], [50, 217], [60, 230], [70, 255]];
        var curves = new Dictionary<string, IReadOnlyDictionary<string, int[][]>>
        {
            ["CPU Fan"] = Modes([[20, 153], [45, 179], [60, 217], [70, 255]]), ["Chassis Fan 1"] = Modes([[44, 54], [48, 74], [60, 158], [66, 255]]),
            ["Chassis Fan 2"] = Modes(std), ["Chassis Fan 3"] = Modes(std), ["Chassis Fan 4"] = Modes([[20, 51], [45, 102], [60, 166], [70, 255]]),
            ["Water Pump+"] = Modes(pump), ["AIO Pump"] = Modes(pump),
        };
        string[] boardFans = ["CPU Fan", "Chassis Fan 1", "Chassis Fan 2", "Chassis Fan 3", "Chassis Fan 4", "Water Pump+", "AIO Pump"];
        var ctl = new Dictionary<int, float>(snap.MbFanControl) { [0] = 55, [1] = 80, [2] = 55, [3] = 55, [4] = 55, [5] = 100, [6] = 100 };
        var rpm = new Dictionary<int, float>(snap.MbFanRpm) { [0] = 594, [1] = 1268, [2] = 834, [3] = 837, [4] = 847 };
        var snapOn = snap with { MbFanControl = ctl, MbFanRpm = rpm };
        var snapOff = snap with { MbFanControl = new Dictionary<int, float>(ctl) { [0] = 0 }, MbFanRpm = new Dictionary<int, float>(rpm) { [0] = 0 } };
        // Cooler: custom curves with the liquid as source (file in the temp directory only; the service was not started).
        static List<MonitorTray.Ryujin.CurveNode> N(params (double T, double P)[] p) => p.Select(x => new MonitorTray.Ryujin.CurveNode(x.T, x.P)).ToList();
        ry.SaveFans(ry.FanConfig with
        {
            Enabled = true, SourceId = MonitorTray.Ryujin.RyujinSensorIds.Water,
            Pump = new() { Custom = N((20, 30), (30, 43), (40, 70), (45, 100)) },
            Radiator = new() { Custom = N((25, 30), (30, 45), (35, 65), (40, 90), (45, 100)) },
            Embedded = new() { Mode = MonitorTray.Ryujin.FanMode.Fixed, FixedPercent = 60 },
        });

        FrameworkElement Window(MonitorTray.App.AsusFanModes ac, Snapshot s, Action<ThermalPanel> setup)
        {
            ry.Simulate(new MonitorTray.Ryujin.RyujinSnapshot(DateTimeOffset.Now, "fw", new(32.4, 2050, 3200), new([1268, 1281, 1276], 140), 59, 60), new(59, 55, 60, null), 32.4);
            var w = new LcdWindow(state, ry, kv, service, ac, fans);
            w.Thermal.Snap = () => s;
            setup(w.Thermal);
            var body = (FrameworkElement)w.Content; w.Content = null;
            return Scene(body, 1300, 860, dark);
        }

        var acOn = MonitorTray.App.AsusFanModes.Fake(tmp, boardFans, mode: null, curves);
        outputs.Add(Save(Path.Combine(dir, "33-thermal-ryujin-radiator.png"), Window(acOn, snapOn, t =>
            t.PreviewEdit(MonitorTray.Core.Fans.ThermalMap.RadiatorId, [new(25, 25), new(30, 35), new(35, 55), new(40, 80), new(45, 100)], 2)), 1));

        var acOff = MonitorTray.App.AsusFanModes.Fake(tmp, boardFans, mode: null, curves);
        acOff.Off.TurnOff("Chassis Fan 1", 50, 50); // memory only (Fake): no curve is written
        outputs.Add(Save(Path.Combine(dir, "34-thermal-board-cha1-off.png"), Window(acOff, snapOff, t => t.SelectActuator(MonitorTray.Core.Fans.ThermalMap.BoardId("Chassis Fan 1"))), 1));
        // CHA_FAN2 renamed by the user: the name is the title/strip label, the header goes to the tooltip and the subtitle.
        state.Update(_ => dark with { AcFanNames = new Dictionary<string, string> { ["Chassis Fan 2"] = "Front intake" } });
        outputs.Add(Save(Path.Combine(dir, "35-thermal-board-cha2-curves.png"), Window(acOff, snapOff, t => t.SelectActuator(MonitorTray.Core.Fans.ThermalMap.BoardId("Chassis Fan 2"))), 1));
        state.Update(_ => dark);
        var acStd = MonitorTray.App.AsusFanModes.Fake(tmp, boardFans, mode: "standard", curves);
        outputs.Add(Save(Path.Combine(dir, "36-thermal-board-std-readonly.png"), Window(acStd, snapOn, t => t.SelectActuator(MonitorTray.Core.Fans.ThermalMap.BoardId("Chassis Fan 4"))), 1));

        // GPU on driver automatic (simulated API).
        fans.CommandAsync(null).Wait();
        Thread.Sleep(1100);
        outputs.Add(Save(Path.Combine(dir, "37-thermal-gpu-auto.png"), Window(acOn, snapOn, t => t.SelectActuator(MonitorTray.Core.Fans.ThermalMap.GpuId)), 1));
    }

    /// <summary>
    /// Tray icon with temperature/alert (Settings › TRAY ICON) at 16, 24 and 32 px, on the dark and the light taskbar:
    /// real size and enlarged without smoothing to check the pixels.
    /// </summary>
    private static string TraySheet(string path)
    {
        static double F(string size) => Settings.TrayFontScale(size);
        var states = new (string Label, Func<int, bool, SD.Bitmap> Draw)[]
        {
            ("62° S (default)", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 62, MonitorTray.Core.Normalize.TempLevel.Normal, l, F("s"))),
            ("62° M", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 62, MonitorTray.Core.Normalize.TempLevel.Normal, l, F("m"))),
            ("62° L", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 62, MonitorTray.Core.Normalize.TempLevel.Normal, l, F("l"))),
            ("82° near S", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 82, MonitorTray.Core.Normalize.TempLevel.Near, l, F("s"))),
            ("88° limit S", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 88, MonitorTray.Core.Normalize.TempLevel.Over, l, F("s"))),
            ("104° limit S", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 104, MonitorTray.Core.Normalize.TempLevel.Over, l, F("s"))),
            ("104° limit L", (px, l) => Tray.TrayIconRenderer.DrawTemp(px, 104, MonitorTray.Core.Normalize.TempLevel.Over, l, F("l"))),
            ("color only: near", (px, l) => Tray.TrayIconRenderer.Tint(Tray.TrayIcons.For(l, px), px, Tray.TrayIconRenderer.ColorFor(MonitorTray.Core.Normalize.TempLevel.Near, l))),
            ("color only: limit", (px, l) => Tray.TrayIconRenderer.Tint(Tray.TrayIcons.For(l, px), px, Tray.TrayIconRenderer.ColorFor(MonitorTray.Core.Normalize.TempLevel.Over, l))),
        };
        const int Row = 112, LabelW = 110, Half = 16 + 24 + 32 + 4 * 10 + 96 + 96 + 20;
        using var sheet = new SD.Bitmap(LabelW + 2 * Half, Row * states.Length);
        using var g = SD.Graphics.FromImage(sheet);
        g.Clear(SD.Color.FromArgb(0x80, 0x80, 0x80));
        g.InterpolationMode = SD.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = SD.Drawing2D.PixelOffsetMode.Half;
        using var font = new SD.Font("Segoe UI", 9);
        for (int i = 0; i < states.Length; i++)
        {
            int y = i * Row;
            g.DrawString(states[i].Label, font, SD.Brushes.White, 4, y + Row / 2 - 8);
            foreach (var light in new[] { false, true })
            {
                int x0 = LabelW + (light ? Half : 0);
                using (var bg = new SD.SolidBrush(light ? SD.Color.FromArgb(0xEE, 0xEE, 0xEE) : SD.Color.FromArgb(0x1C, 0x1C, 0x1C))) g.FillRectangle(bg, x0, y, Half - 4, Row - 4);
                int x = x0 + 10;
                foreach (var px in new[] { 16, 24, 32 })
                {
                    using var b = states[i].Draw(px, light);
                    g.DrawImageUnscaled(b, x, y + (Row - px) / 2);
                    x += px + 10;
                }
                using (var b16 = states[i].Draw(16, light)) g.DrawImage(b16, new SD.Rectangle(x, y + 6, 96, 96));
                x += 96 + 10;
                using (var b32 = states[i].Draw(32, light)) g.DrawImage(b32, new SD.Rectangle(x, y + 6, 96, 96));
            }
        }
        sheet.Save(path, SD.Imaging.ImageFormat.Png);
        return path;
    }

    private sealed class NullTransport : MonitorTray.Oled.IOledTransport
    {
        public Task BindAsync(int icon, CancellationToken t) => Task.CompletedTask;
        public Task SendAsync(string[] l, CancellationToken t) => Task.CompletedTask;
        public Task ReleaseAsync(CancellationToken t) => Task.CompletedTask;
    }

    private static FrameworkElement Scene(FrameworkElement frame, double w, double h, Settings s)
    {
        // Checkered background (as in the kit screenshots) to make the transparency visible.
        var light = !Theming.Theme.IsDark(s);
        var bg = new DrawingBrush
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 48, 48), ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new GeometryDrawing(new SolidColorBrush(light ? Color.FromRgb(0xC8, 0xC8, 0xCC) : Color.FromRgb(0x3A, 0x3B, 0x3F)), new Pen(new SolidColorBrush(light ? Color.FromRgb(0xB4, 0xB4, 0xB8) : Color.FromRgb(0x46, 0x47, 0x4B)), 1), new RectangleGeometry(new Rect(0, 0, 48, 48))),
        };
        frame.Width = w; if (!double.IsNaN(h)) frame.Height = h;
        frame.Margin = new Thickness(24);
        frame.HorizontalAlignment = HorizontalAlignment.Left; frame.VerticalAlignment = VerticalAlignment.Top;
        return new Border { Background = bg, Child = frame };
    }

    private static string Save(string path, FrameworkElement e, double scale)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = e.DesiredSize;
        e.Arrange(new Rect(size));
        e.UpdateLayout();
        // Second pass: some panels only know their height after the first arrange.
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); size = e.DesiredSize; e.Arrange(new Rect(size)); e.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(e);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        return path;
    }
}
