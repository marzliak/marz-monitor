using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using MonitorTray.Core.Fans;
using MonitorTray.Core.Format;

namespace MonitorTray.App;

/// <summary>
/// Modos das ventoinhas da placa via Armoury Crate (COM do AsusFanControlService). O Marz NUNCA escreve no chip:
/// pede ao serviço da ASUS para trocar de perfil (Silent/Standard/Turbo/User=curvas) ou aplicar uma curva em 100% (FULL).
/// Atenção (medido): ApplyFanCurveButNotSave grava na curva User. Por isso, antes do FULL os pontos de User de cada
/// ventoinha vão para state\ac-user-curves.json e são devolvidos (SaveUserProfile) ao voltar para CURVAS, ao sair do app
/// e ao abrir o app (se ficou pendente). Todas as chamadas COM rodam numa thread própria.
/// O editor de curvas (controle térmico) lê as curvas dos perfis e grava a curva User pelo mesmo WriteUser dessa devolução.
/// </summary>
public sealed class AsusFanModes : IDisposable
{
    public static readonly string[] AllModes = MonitorTray.Core.Model.Settings.AcModeIds;
    public static string Label(string m) => m switch { "curvas" => "CURVAS", "silent" => "SILENT", "standard" => "STD", "turbo" => "TURBO", "full" => "FULL", _ => m.ToUpperInvariant() };
    private static int? ProfileIndex(string m) => m switch { "curvas" => 4, "standard" => 1, "silent" => 2, "turbo" => 3, _ => null };
    private static readonly Guid Clsid = new("14083c53-b8e7-48e4-9320-811f3478c4a4");

    private readonly string snapPath;
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private dynamic? mgr;

    public bool Available { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Nomes das ventoinhas como o Armoury Crate chama ("Chassis Fan 3").</summary>
    public IReadOnlyList<string> Fans { get; private set; } = [];
    /// <summary>Modo aplicado pelo Marz (null = não mexeu; vale o que o Armoury Crate estiver usando).</summary>
    public string? Mode { get; private set; }
    /// <summary>Modo pedido e ainda sendo aplicado: a UI acende o botão na hora do clique.</summary>
    public string? Pending { get; private set; }
    public event Action? Changed;
    public static Action<string>? Log;
    private void Trace(string s) { Log?.Invoke(s); try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(snapPath)!, "ac-modes.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); } catch (Exception) { } }

    /// <summary>Só para a prévia: sem COM, lista fixa (e curvas fixas por ventoinha/modo, se dadas).</summary>
    public static AsusFanModes Fake(string dir, IReadOnlyList<string> fans, string? mode = "full", IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[][]>>? curves = null) =>
        new(dir, fans) { Mode = mode, Curves = curves ?? new Dictionary<string, IReadOnlyDictionary<string, int[][]>>() };
    private AsusFanModes(string dir, IReadOnlyList<string> fans) { snapPath = Path.Combine(dir, "x.json"); thread = new Thread(() => { }); Fans = fans; Available = true; }

    public AsusFanModes(string stateDir)
    {
        snapPath = Path.Combine(stateDir, "ac-user-curves.json");
        thread = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Error = ex.Message; Trace("ERRO " + ex); } } })
        { IsBackground = true, Name = "AsusFanModes" };
        thread.SetApartmentState(ApartmentState.MTA); // STA sem bomba de mensagens: chamadas ao servidor COM ficavam sem efeito
        thread.Start();
        queue.Add(Init);
    }

    private void Init()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(Clsid, throwOnError: false);
            if (t == null) { Error = "Armoury Crate (serviço de fans) não encontrado."; return; }
            mgr = Activator.CreateInstance(t);
            var names = new List<string>();
            dynamic cs = mgr!.Controls;
            for (int i = 0; i < (int)cs.Count; i++) names.Add((string)cs.Item(i).Name);
            Fans = names; Available = names.Count > 0;
            if (File.Exists(snapPath)) RestoreUser(null); // FULL ficou pendente (app fechou/travou): devolve as curvas
        }
        catch (Exception ex) { Error = "Armoury Crate: " + ex.Message; Available = false; }
        Changed?.Invoke();
    }

    private dynamic? Control(string name)
    {
        dynamic cs = mgr!.Controls;
        for (int i = 0; i < (int)cs.Count; i++) { dynamic c = cs.Item(i); if ((string)c.Name == name) return c; }
        return null;
    }

    private static int[][] Points(dynamic curve)
    {
        var l = new List<int[]>();
        for (int i = 0; i < (int)curve.Count; i++) { dynamic p = curve.Item(i); l.Add([(int)p.Temperature, (int)p.Speed]); }
        return l.ToArray();
    }

    private static string Fmt(int[][] a) => string.Join(" ", a.Select(x => $"({x[0]},{x[1]})"));
    private static bool Same(int[][] a, int[][] b) => a.Length == b.Length && a.Zip(b).All(z => z.First.SequenceEqual(z.Second));

    private Dictionary<string, int[][]> LoadSnap()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, int[][]>>(File.ReadAllText(snapPath)) ?? new(); } catch (Exception) { return new(); }
    }

    /// <summary>Devolve as curvas User salvas (só das ventoinhas em <paramref name="only"/>, ou todas) e reaplica o perfil User.</summary>
    private void RestoreUser(IEnumerable<string>? only)
    {
        var snap = LoadSnap();
        var keys = only == null ? snap.Keys.ToList() : snap.Keys.Intersect(only).ToList();
        foreach (var name in keys)
        {
            dynamic? c = Control(name); if (c == null) continue;
            if (!WriteUser(c, snap[name])) Error = $"{name}: curva User não conferiu após restaurar.";
            snap.Remove(name);
        }
        if (snap.Count == 0) { try { File.Delete(snapPath); } catch (Exception) { } }
        else File.WriteAllText(snapPath, JsonSerializer.Serialize(snap));
    }

    /// <summary>Grava os pontos na curva User (SaveUserProfile), aplica o perfil User e relê. true = conferiu ponto a ponto.</summary>
    private static bool WriteUser(dynamic c, int[][] pts)
    {
        dynamic u = c.Profiles.Item(4).FanCurve;
        for (int i = 0; i < Math.Min((int)u.Count, pts.Length); i++) { u.Item(i).Temperature = pts[i][0]; u.Item(i).Speed = pts[i][1]; }
        c.Profiles.SaveUserProfile(u);
        c.ApplyIndex(4);
        return Same(Points(c.Profiles.Item(4).FanCurve), pts);
    }

    /// <summary>
    /// Curvas de cada ventoinha por modo (°C, PWM 0..255), lidas do Armoury Crate (só leitura): curvas/standard/silent/turbo.
    /// Em "curvas" vale a curva User guardada antes do FULL/OFF, se houver (a do chip é a temporária).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[][]>> Curves { get; private set; } = new Dictionary<string, IReadOnlyDictionary<string, int[][]>>();
    /// <summary>Ventoinhas com a curva User guardada fora do chip (FULL/OFF): a edição espera ela voltar.</summary>
    public IReadOnlyCollection<string> SavedPending { get; private set; } = [];

    /// <summary>Relê as curvas dos perfis (fila do COM). Nada é gravado.</summary>
    public void RefreshCurves()
    {
        watchCurves = true;
        queue.Add(() => { ReadCurves(); Changed?.Invoke(); });
    }

    /// <summary>A janela do controle térmico já abriu: cada modo/OFF aplicado relê as curvas (sem a janela, nada é lido).</summary>
    private volatile bool watchCurves;
    private void ReadCurvesIfWatched() { if (watchCurves) try { ReadCurves(); } catch (Exception ex) { Trace("ERRO leitura curvas " + ex.Message); } }

    private void ReadCurves()
    {
        if (!Available || mgr == null) return;
        var snap = LoadSnap();
        var all = new Dictionary<string, IReadOnlyDictionary<string, int[][]>>();
        foreach (var name in Fans)
        {
            dynamic? c = Control(name); if (c == null) continue;
            var m = new Dictionary<string, int[][]>();
            foreach (var mode in AllModes)
                if (ProfileIndex(mode) is int idx)
                    try { m[mode] = mode == "curvas" && snap.TryGetValue(name, out var saved) ? saved : Points(c.Profiles.Item(idx).FanCurve); }
                    catch (Exception ex) { Trace($"curva {name}/{mode}: {ex.Message}"); }
            all[name] = m;
        }
        Curves = all; SavedPending = snap.Keys.ToList();
    }

    /// <summary>
    /// Botão Aplicar do editor: grava a curva User de uma ventoinha pelo mesmo caminho que devolve as curvas depois do FULL/OFF
    /// (SaveUserProfile + perfil User + releitura). Só em CURVAS, com o fan ligado, sem curva guardada pendente e com a
    /// mesma quantidade de pontos do chip. false = recusado na hora (o motivo fica em Error).
    /// </summary>
    public bool SaveUserCurve(string fan, int[][] pts)
    {
        if (!Available || mgr == null || Mode != null || Pending is not (null or "curvas") || Off.IsOff(fan) || Off.ManualPercent(fan) != null) { Error = $"{fan}: curva não gravada (só em CURVAS, com o fan ligado e fora do MANUAL)."; return false; }
        if (pts.Length < 2 || pts.Any(p => p.Length != 2 || p[0] is < 0 or > 120 || p[1] is < 0 or > 255) || pts.Zip(pts.Skip(1)).Any(z => z.Second[0] <= z.First[0])) { Error = $"{fan}: curva inválida."; return false; }
        if (BoardFanOff.Locked(fan) && pts.Any(p => p[1] < CurveEdit.PwmFromPercent(ThermalMap.BoardLockedMinPercent))) { Error = $"{fan}: CPU e bombas não descem de {ThermalMap.BoardLockedMinPercent}%."; return false; }
        Pending = "curvas"; Changed?.Invoke();
        queue.Add(() =>
        {
            try
            {
                Error = null;
                if (Mode != null || Off.IsOff(fan) || LoadSnap().ContainsKey(fan)) { Error = $"{fan}: modo mudou antes de gravar; curva não gravada."; return; }
                dynamic? c = Control(fan);
                if (c == null) { Error = $"{fan}: conector não encontrado no Armoury Crate."; return; }
                int[][] before = Points(c.Profiles.Item(4).FanCurve);
                if (before.Length != pts.Length) { Error = $"{fan}: o chip tem {before.Length} pontos; curva não gravada."; return; }
                Trace($"User {fan}: antes={Fmt(before)} pedida={Fmt(pts)}");
                if (!WriteUser(c, pts))
                {
                    Trace($"User {fan}: não conferiu (lida={Fmt(Points(c.Profiles.Item(4).FanCurve))}); devolvendo a anterior");
                    WriteUser(c, before);
                    Error = $"{fan}: a curva não conferiu no chip; a anterior foi devolvida.";
                }
            }
            catch (Exception ex) { Trace("ERRO curva " + ex); Error = $"{fan}: erro ao gravar a curva: {ex.Message}"; }
            finally { ReadCurves(); if (Pending == "curvas") Pending = null; Changed?.Invoke(); }
        });
        return true;
    }

    /// <summary>
    /// Fans desligados pelo popup (só memória). Os modos pulam os desligados; FULL religa todos (ver BoardFanOff).
    /// </summary>
    public BoardFanOff Off { get; } = new();
    /// <summary>A curva de proteção não conferiu no chip: o fan foi religado (a UI desmarca o card).</summary>
    public event Action<string>? OffRejected;

    /// <summary>Aplica um modo nas ventoinhas escolhidas. "curvas" = perfil User (o seu) intacto.</summary>
    /// <param name="remember">Escolha do usuário (guarda em fan-choices.json). O modo game e a restauração passam false.</param>
    public void Apply(string mode, IReadOnlyCollection<string> fans, bool remember = true)
    {
        var targets = Off.ModeTargets(mode, fans);
        if (remember) { FanChoices.Update(d => d with { BoardMode = mode }); RememberOffManual(); }
        Pending = mode; Changed?.Invoke();
        queue.Add(() =>
        {
            try { ApplyNow(mode, targets); }
            finally { ReadCurvesIfWatched(); if (Pending == mode) Pending = null; Changed?.Invoke(); }
        });
    }

    /// <summary>
    /// Desliga um fan (clique no card): grava no lugar da curva dele a curva de proteção (0% até 80 °C, 100% a partir de 85 °C),
    /// pelo mesmo caminho do FULL (curva User guardada antes, Turbo, ApplyFanCurveButNotSave). false = regras não deixam.
    /// </summary>
    public bool TurnOff(string fan, float? cpuC, float? gpuC)
    {
        if (!Available || !Off.TurnOff(fan, cpuC, gpuC)) return false;
        RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try { OffNow(fan); }
            catch (Exception ex) { Trace("ERRO off " + ex); Reject(fan, "erro ao desligar: " + ex.Message); }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
        return true;
    }

    /// <summary>Religa fans desligados: devolve a curva User e reaplica o modo atual neles.</summary>
    /// <param name="remember">false = religado pelo app (proteção, modo game): a escolha OFF do usuário fica guardada.</param>
    public void TurnOn(IReadOnlyCollection<string> fans, string why, bool remember = true)
    {
        foreach (var f in fans) Off.TurnOn(f);
        if (fans.Count == 0) return;
        if (remember) RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try { if (Available && mgr != null) { Trace($"On [{string.Join(",", fans)}] ({why})"); ApplyNow(Mode ?? "curvas", fans, keepMode: true); } }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
    }

    /// <summary>Segurança a cada leitura: sem leitura da CPU, CPU &gt; 85 °C ou GPU &gt; 80 °C religa todos os desligados.</summary>
    public void SafetyCheck(float? cpuC, float? gpuC)
    {
        if (!Off.Any) return;
        var back = Off.Check(cpuC, gpuC);
        if (back.Count == 0) return;
        LastSafety = new SafetyEvent(DateTimeOffset.Now, back, cpuC, gpuC);
        TurnOn(back, remember: false, why: $"segurança: CPU {cpuC?.ToString("0") ?? "—"} °C, GPU {gpuC?.ToString("0") ?? "—"} °C");
    }

    /// <summary>Último religamento pela proteção (faixa do popup); a UI zera ao dispensar.</summary>
    public SafetyEvent? LastSafety { get; set; }

    /// <summary>
    /// MANUAL (menu do card): o fan fica no % pedido até 80 °C e 100% a partir de 85 °C (curva gravada no chip pelo mesmo
    /// caminho do OFF/FULL; a curva User fica guardada). CPU_FAN não desce de 20%. false = recusado na hora.
    /// </summary>
    public bool SetManual(string fan, int percent)
    {
        if (!Available || mgr == null) { Error = $"{fan}: Armoury Crate indisponível."; return false; }
        int p = Off.SetManual(fan, BoardFanOff.Locked(fan) ? Math.Max(percent, ThermalMap.BoardLockedMinPercent) : percent);
        RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try
            {
                if (Off.ManualPercent(fan) != p) return; // outro pedido mais novo já está na fila
                Error = null;
                Trace($"Manual {fan} {p}%");
                if (!HoldNow(fan, CurveEdit.PwmFromPercent(p))) { Off.ClearManual(fan); ApplyNow(Mode ?? "curvas", [fan], keepMode: true); Error = $"{fan}: a curva do MANUAL não conferiu; voltou para a curva."; }
            }
            catch (Exception ex) { Trace("ERRO manual " + ex); Off.ClearManual(fan); Error = $"{fan}: erro no MANUAL: {ex.Message}"; }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
        return true;
    }

    /// <summary>Sai do MANUAL: devolve a curva User e reaplica o modo atual no fan.</summary>
    public void ClearManual(string fan)
    {
        if (!Off.ClearManual(fan)) return;
        RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try { if (Available && mgr != null) { Trace($"Curva {fan} (sai do MANUAL)"); ApplyNow(Mode ?? "curvas", [fan], keepMode: true); } }
            catch (Exception ex) { Trace("ERRO curva " + ex); Error = $"{fan}: erro ao voltar para a curva: {ex.Message}"; }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
    }

    private void OffNow(string fan)
    {
        if (!Available || mgr == null || !Off.IsOff(fan)) return;
        Error = null;
        Trace($"Off {fan}");
        if (HoldNow(fan, 0) && Off.IsOff(fan)) return;
        if (Off.IsOff(fan)) Reject(fan, "a curva de proteção não conferiu");
        else ApplyNow(Mode ?? "curvas", [fan], keepMode: true); // religado enquanto aplicava
    }

    /// <summary>
    /// Grava a curva de segurar (PWM pedido até 80 °C, 255 a partir de 85 °C) no lugar da curva do fan: guarda a User antes,
    /// passa por Turbo e ApplyFanCurveButNotSave, como o FULL. true = conferiu ponto a ponto.
    /// </summary>
    private bool HoldNow(string fan, int pwm)
    {
        RestoreUser([fan]);
        dynamic? c = Control(fan);
        if (c == null) { Error = $"{fan}: conector não encontrado no Armoury Crate."; return false; }
        var snap = LoadSnap();
        if (!snap.ContainsKey(fan)) snap[fan] = Points(c.Profiles.Item(4).FanCurve);
        Directory.CreateDirectory(Path.GetDirectoryName(snapPath)!);
        File.WriteAllText(snapPath, JsonSerializer.Serialize(snap)); // grava ANTES de mexer: sair/reabrir devolve a curva User
        c.ApplyIndex(3); // como no FULL: a curva atual só aceita mudança partindo de um perfil ASUS
        Thread.Sleep(900);
        dynamic cur = c.CurrentFanCurve;
        int n = (int)cur.Count;
        int[][] before = Points(cur); int d0 = (int)c.DutyCycle;
        Trace(fan + ": atual antes=" + Fmt(before) + " duty=" + d0);
        if (n < 2) return false;
        int[][] want = BoardFanOff.HoldCurve(n, pwm);
        for (int i = 0; i < n; i++) { cur.Item(i).Temperature = want[i][0]; cur.Item(i).Speed = want[i][1]; }
        c.ApplyFanCurveButNotSave(cur);
        int[][] got = Points(c.CurrentFanCurve); int d1 = (int)c.DutyCycle;
        Trace(fan + ": " + (pwm == 0 ? "proteção" : $"manual {pwm}") + " pedida=" + Fmt(want) + " lida=" + Fmt(got) + " duty=" + d1);
        return Same(got, want);
    }

    /// <summary>OFF falhou: religa (curva User + modo atual) e avisa a UI. Nunca fica uma curva pela metade.</summary>
    private void Reject(string fan, string why)
    {
        Off.TurnOn(fan);
        try { ApplyNow(Mode ?? "curvas", [fan], keepMode: true); } catch (Exception ex) { Trace("ERRO religar " + ex); }
        Error = $"{fan}: {why}; fan religado."; // depois do ApplyNow, que zera o erro
        OffRejected?.Invoke(fan);
    }

    private void ApplyNow(string mode, IReadOnlyCollection<string> fans, bool keepMode = false)
    {
        {
            if (!Available || mgr == null) return;
            Error = null;
            Trace($"Apply {mode} [{string.Join(",", fans)}]");
            // Qualquer modo começa devolvendo as curvas User de quem estava em FULL.
            RestoreUser(fans);
            if (mode == "full")
            {
                var snap = LoadSnap();
                foreach (var name in fans)
                {
                    dynamic? c = Control(name); if (c == null) continue;
                    if (!snap.ContainsKey(name)) snap[name] = Points(c.Profiles.Item(4).FanCurve);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(snapPath)!);
                File.WriteAllText(snapPath, JsonSerializer.Serialize(snap)); // grava ANTES de mexer
                // Medido: a curva atual só aceita a mudança partindo de um perfil ASUS (Turbo); partindo de User não tem efeito.
                // Troca todas para Turbo de uma vez e espera uma vez só (antes: 1,5 s por ventoinha).
                foreach (var name in fans) { dynamic? c0 = Control(name); c0?.ApplyIndex(3); }
                Thread.Sleep(900);
                foreach (var name in fans)
                {
                    dynamic? c = Control(name); if (c == null) continue;
                    dynamic cur = c.CurrentFanCurve;
                    int[][] p0 = Points(cur); int d0 = (int)c.DutyCycle;
                    Trace(name + ": atual antes=" + Fmt(p0) + " duty=" + d0);
                    for (int i = 0; i < (int)cur.Count; i++) cur.Item(i).Speed = 255;
                    c.ApplyFanCurveButNotSave(cur);
                    int[][] p1 = Points(c.CurrentFanCurve); int[][] pu = Points(c.Profiles.Item(4).FanCurve); int d1 = (int)c.DutyCycle;
                    Trace(name + ": depois atual=" + Fmt(p1) + " user=" + Fmt(pu) + " duty=" + d1);
                }
            }
            else if (ProfileIndex(mode) is int idx)
                foreach (var name in fans) { dynamic? c = Control(name); c?.ApplyIndex(idx); }
            if (!keepMode) Mode = mode == "curvas" ? null : mode;
        }
    }

    /// <summary>Guarda os fans em OFF/MANUAL (para reaplicar ao abrir o app).</summary>
    private void RememberOffManual()
    {
        var man = Off.Manual.Select(f => (f, Off.ManualPercent(f))).Where(x => x.Item2 != null).ToDictionary(x => x.f, x => x.Item2!.Value);
        FanChoices.Update(d => d with { Off = Off.Off.ToList(), Manual = man });
    }

    /// <summary>Saída do app: devolve tudo para as curvas do usuário e espera terminar.</summary>
    public void RestoreAndWait(TimeSpan timeout)
    {
        FanChoices.Enabled = false; // a escolha do usuário fica guardada; isto é só a devolução segura
        if (!Available) return;
        Off.ClearAll(); Off.ClearAllManual(); // desligado/manual não persistem: a curva User de cada um volta junto com as demais
        using var done = new ManualResetEventSlim();
        queue.Add(() =>
        {
            try
            {
                RestoreUser(null);
                if (Mode != null) foreach (var n in Fans) { dynamic? c = Control(n); c?.ApplyIndex(4); }
                Mode = null;
            }
            finally { done.Set(); }
        });
        done.Wait(timeout);
    }

    public void Dispose() { queue.CompleteAdding(); if (thread.IsAlive) thread.Join(3000); }
}
