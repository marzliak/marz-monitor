using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using MonitorTray.Core.Fans;
using MonitorTray.Core.Format;

namespace MonitorTray.App;

/// <summary>
/// Board fan modes via Armoury Crate (COM of AsusFanControlService). Marz NEVER writes to the chip:
/// it asks the ASUS service to switch profile (Silent/Standard/Turbo/User=curves) or to apply a curve at 100% (FULL).
/// Caution (measured): ApplyFanCurveButNotSave writes to the User curve. So before FULL the User points of each
/// fan go to state\ac-user-curves.json and are given back (SaveUserProfile) when returning to CURVES, when quitting the app
/// and when opening the app (if left pending). All COM calls run on a dedicated thread.
/// The curve editor (thermal control) reads the profile curves and writes the User curve through the same WriteUser as that restore.
/// </summary>
public sealed class AsusFanModes : IDisposable
{
    public static readonly string[] AllModes = MonitorTray.Core.Model.Settings.AcModeIds;
    public static string Label(string m) => m switch { "curves" => "CURVES", "silent" => "SILENT", "standard" => "STD", "turbo" => "TURBO", "full" => "FULL", _ => m.ToUpperInvariant() };
    private static int? ProfileIndex(string m) => m switch { "curves" => 4, "standard" => 1, "silent" => 2, "turbo" => 3, _ => null };
    private static readonly Guid Clsid = new("14083c53-b8e7-48e4-9320-811f3478c4a4");

    private readonly string snapPath;
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private dynamic? mgr;

    public bool Available { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Fan names as Armoury Crate calls them ("Chassis Fan 3").</summary>
    public IReadOnlyList<string> Fans { get; private set; } = [];
    /// <summary>Mode applied by Marz (null = untouched; whatever Armoury Crate is using applies).</summary>
    public string? Mode { get; private set; }
    /// <summary>Mode requested and still being applied: the UI lights the button up right on click.</summary>
    public string? Pending { get; private set; }
    public event Action? Changed;
    public static Action<string>? Log;
    private void Trace(string s) { Log?.Invoke(s); try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(snapPath)!, "ac-modes.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\r\n"); } catch (Exception) { } }

    /// <summary>Preview only: no COM, fixed list (and fixed curves per fan/mode, if given).</summary>
    public static AsusFanModes Fake(string dir, IReadOnlyList<string> fans, string? mode = "full", IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[][]>>? curves = null) =>
        new(dir, fans) { Mode = mode, Curves = curves ?? new Dictionary<string, IReadOnlyDictionary<string, int[][]>>() };
    private AsusFanModes(string dir, IReadOnlyList<string> fans) { snapPath = Path.Combine(dir, "x.json"); thread = new Thread(() => { }); Fans = fans; Available = true; }

    public AsusFanModes(string stateDir)
    {
        snapPath = Path.Combine(stateDir, "ac-user-curves.json");
        thread = new Thread(() => { foreach (var a in queue.GetConsumingEnumerable()) { try { a(); } catch (Exception ex) { Error = ex.Message; Trace("ERROR " + ex); } } })
        { IsBackground = true, Name = "AsusFanModes" };
        thread.SetApartmentState(ApartmentState.MTA); // STA without a message pump: calls to the COM server had no effect
        thread.Start();
        queue.Add(Init);
    }

    private void Init()
    {
        try
        {
            var t = Type.GetTypeFromCLSID(Clsid, throwOnError: false);
            if (t == null) { Error = "Armoury Crate (fan service) not found."; return; }
            mgr = Activator.CreateInstance(t);
            var names = new List<string>();
            dynamic cs = mgr!.Controls;
            for (int i = 0; i < (int)cs.Count; i++) names.Add((string)cs.Item(i).Name);
            Fans = names; Available = names.Count > 0;
            if (File.Exists(snapPath)) RestoreUser(null); // FULL was left pending (app closed/crashed): restore the curves
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

    /// <summary>Restores the saved User curves (only for the fans in <paramref name="only"/>, or all) and reapplies the User profile.</summary>
    private void RestoreUser(IEnumerable<string>? only)
    {
        var snap = LoadSnap();
        var keys = only == null ? snap.Keys.ToList() : snap.Keys.Intersect(only).ToList();
        foreach (var name in keys)
        {
            dynamic? c = Control(name); if (c == null) continue;
            if (!WriteUser(c, snap[name])) Error = $"{ThermalMap.BoardHeader(name)}: User curve did not match after restoring.";
            snap.Remove(name);
        }
        if (snap.Count == 0) { try { File.Delete(snapPath); } catch (Exception) { } }
        else File.WriteAllText(snapPath, JsonSerializer.Serialize(snap));
    }

    /// <summary>Writes the points to the User curve (SaveUserProfile), applies the User profile and reads it back. true = matched point by point.</summary>
    private static bool WriteUser(dynamic c, int[][] pts)
    {
        dynamic u = c.Profiles.Item(4).FanCurve;
        for (int i = 0; i < Math.Min((int)u.Count, pts.Length); i++) { u.Item(i).Temperature = pts[i][0]; u.Item(i).Speed = pts[i][1]; }
        c.Profiles.SaveUserProfile(u);
        c.ApplyIndex(4);
        return Same(Points(c.Profiles.Item(4).FanCurve), pts);
    }

    /// <summary>
    /// Curves of each fan per mode (°C, PWM 0..255), read from Armoury Crate (read only): curves/standard/silent/turbo.
    /// In "curves" the User curve stored before FULL/OFF applies, if any (the one on the chip is the temporary one).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int[][]>> Curves { get; private set; } = new Dictionary<string, IReadOnlyDictionary<string, int[][]>>();
    /// <summary>Fans whose User curve is stored off the chip (FULL/OFF): editing waits for it to come back.</summary>
    public IReadOnlyCollection<string> SavedPending { get; private set; } = [];

    /// <summary>Rereads the profile curves (COM queue). Nothing is written.</summary>
    public void RefreshCurves()
    {
        watchCurves = true;
        queue.Add(() => { ReadCurves(); Changed?.Invoke(); });
    }

    /// <summary>The thermal control window has been opened: each applied mode/OFF rereads the curves (without the window, nothing is read).</summary>
    private volatile bool watchCurves;
    private void ReadCurvesIfWatched() { if (watchCurves) try { ReadCurves(); } catch (Exception ex) { Trace("ERROR reading curves " + ex.Message); } }

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
                    try { m[mode] = mode == "curves" && snap.TryGetValue(name, out var saved) ? saved : Points(c.Profiles.Item(idx).FanCurve); }
                    catch (Exception ex) { Trace($"curve {name}/{mode}: {ex.Message}"); }
            all[name] = m;
        }
        Curves = all; SavedPending = snap.Keys.ToList();
    }

    /// <summary>
    /// The editor's Apply button: writes a fan's User curve through the same path that restores the curves after FULL/OFF
    /// (SaveUserProfile + User profile + reread). Only in CURVES, with the fan on, no stored curve pending and with the
    /// same number of points as the chip. false = refused right away (the reason goes to Error).
    /// </summary>
    public bool SaveUserCurve(string fan, int[][] pts)
    {
        if (!Available || mgr == null || Mode != null || Pending is not (null or "curves") || Off.IsOff(fan) || Off.ManualPercent(fan) != null) { Error = $"{ThermalMap.BoardHeader(fan)}: curve not written (only in CURVES, with the fan on and not in MANUAL)."; return false; }
        if (pts.Length < 2 || pts.Any(p => p.Length != 2 || p[0] is < 0 or > 120 || p[1] is < 0 or > 255) || pts.Zip(pts.Skip(1)).Any(z => z.Second[0] <= z.First[0])) { Error = $"{ThermalMap.BoardHeader(fan)}: invalid curve."; return false; }
        if (BoardFanOff.Locked(fan) && pts.Any(p => p[1] < CurveEdit.PwmFromPercent(ThermalMap.BoardLockedMinPercent))) { Error = $"{ThermalMap.BoardHeader(fan)}: CPU and pumps don't go below {ThermalMap.BoardLockedMinPercent}%."; return false; }
        Pending = "curves"; Changed?.Invoke();
        queue.Add(() =>
        {
            try
            {
                Error = null;
                if (Mode != null || Off.IsOff(fan) || LoadSnap().ContainsKey(fan)) { Error = $"{ThermalMap.BoardHeader(fan)}: mode changed before writing; curve not written."; return; }
                dynamic? c = Control(fan);
                if (c == null) { Error = $"{ThermalMap.BoardHeader(fan)}: header not found in Armoury Crate."; return; }
                int[][] before = Points(c.Profiles.Item(4).FanCurve);
                if (before.Length != pts.Length) { Error = $"{ThermalMap.BoardHeader(fan)}: the chip has {before.Length} points; curve not written."; return; }
                Trace($"User {fan}: before={Fmt(before)} requested={Fmt(pts)}");
                if (!WriteUser(c, pts))
                {
                    Trace($"User {fan}: did not match (read={Fmt(Points(c.Profiles.Item(4).FanCurve))}); restoring the previous one");
                    WriteUser(c, before);
                    Error = $"{ThermalMap.BoardHeader(fan)}: the curve did not match on the chip; the previous one was restored.";
                }
            }
            catch (Exception ex) { Trace("ERROR curve " + ex); Error = $"{ThermalMap.BoardHeader(fan)}: error writing the curve: {ex.Message}"; }
            finally { ReadCurves(); if (Pending == "curves") Pending = null; Changed?.Invoke(); }
        });
        return true;
    }

    /// <summary>
    /// Fans turned off from the popup (memory only). Modes skip the ones that are off; FULL turns them all back on (see BoardFanOff).
    /// </summary>
    public BoardFanOff Off { get; } = new();
    /// <summary>The protection curve did not match on the chip: the fan was turned back on (the UI unchecks the card).</summary>
    public event Action<string>? OffRejected;

    /// <summary>Applies a mode to the chosen fans. "curves" = User profile (yours) untouched.</summary>
    /// <param name="remember">User choice (stored in fan-choices.json). Game mode and the restore pass false.</param>
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
    /// Turns a fan off (click on the card): writes the protection curve in place of its curve (0% up to 80 °C, 100% from 85 °C on),
    /// through the same path as FULL (User curve stored first, Turbo, ApplyFanCurveButNotSave). false = the rules don't allow it.
    /// </summary>
    public bool TurnOff(string fan, float? cpuC, float? gpuC)
    {
        if (!Available || !Off.TurnOff(fan, cpuC, gpuC)) return false;
        RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try { OffNow(fan); }
            catch (Exception ex) { Trace("ERROR off " + ex); Reject(fan, "error turning off: " + ex.Message); }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
        return true;
    }

    /// <summary>Turns fans back on: restores the User curve and reapplies the current mode to them.</summary>
    /// <param name="remember">false = turned back on by the app (protection, game mode): the user's OFF choice stays stored.</param>
    public void TurnOn(IReadOnlyCollection<string> fans, string why, bool remember = true)
    {
        foreach (var f in fans) Off.TurnOn(f);
        if (fans.Count == 0) return;
        if (remember) RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try { if (Available && mgr != null) { Trace($"On [{string.Join(",", fans)}] ({why})"); ApplyNow(Mode ?? "curves", fans, keepMode: true); } }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
    }

    /// <summary>Safety on every reading: no CPU reading, CPU &gt; 85 °C or GPU &gt; 80 °C turns every fan that is off back on.</summary>
    public void SafetyCheck(float? cpuC, float? gpuC)
    {
        if (!Off.Any) return;
        var back = Off.Check(cpuC, gpuC);
        if (back.Count == 0) return;
        LastSafety = new SafetyEvent(DateTimeOffset.Now, back, cpuC, gpuC);
        TurnOn(back, remember: false, why: $"safety: CPU {cpuC?.ToString("0") ?? "—"} °C, GPU {gpuC?.ToString("0") ?? "—"} °C");
    }

    /// <summary>Last turn-back-on by the protection (popup banner); the UI clears it on dismiss.</summary>
    public SafetyEvent? LastSafety { get; set; }

    /// <summary>
    /// MANUAL (card menu): the fan stays at the requested % up to 80 °C and 100% from 85 °C on (curve written to the chip through the
    /// same path as OFF/FULL; the User curve is kept). CPU_FAN does not go below 20%. false = refused right away.
    /// </summary>
    public bool SetManual(string fan, int percent)
    {
        if (!Available || mgr == null) { Error = $"{ThermalMap.BoardHeader(fan)}: Armoury Crate unavailable."; return false; }
        int p = Off.SetManual(fan, BoardFanOff.Locked(fan) ? Math.Max(percent, ThermalMap.BoardLockedMinPercent) : percent);
        RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try
            {
                if (Off.ManualPercent(fan) != p) return; // a newer request is already queued
                Error = null;
                Trace($"Manual {fan} {p}%");
                if (!HoldNow(fan, CurveEdit.PwmFromPercent(p))) { Off.ClearManual(fan); ApplyNow(Mode ?? "curves", [fan], keepMode: true); Error = $"{ThermalMap.BoardHeader(fan)}: the MANUAL curve did not match; back to the curve."; }
            }
            catch (Exception ex) { Trace("ERROR manual " + ex); Off.ClearManual(fan); Error = $"{ThermalMap.BoardHeader(fan)}: MANUAL error: {ex.Message}"; }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
        return true;
    }

    /// <summary>Leaves MANUAL: restores the User curve and reapplies the current mode to the fan.</summary>
    public void ClearManual(string fan)
    {
        if (!Off.ClearManual(fan)) return;
        RememberOffManual();
        Changed?.Invoke();
        queue.Add(() =>
        {
            try { if (Available && mgr != null) { Trace($"Curve {fan} (leaving MANUAL)"); ApplyNow(Mode ?? "curves", [fan], keepMode: true); } }
            catch (Exception ex) { Trace("ERROR curve " + ex); Error = $"{ThermalMap.BoardHeader(fan)}: error returning to the curve: {ex.Message}"; }
            finally { ReadCurvesIfWatched(); Changed?.Invoke(); }
        });
    }

    private void OffNow(string fan)
    {
        if (!Available || mgr == null || !Off.IsOff(fan)) return;
        Error = null;
        Trace($"Off {fan}");
        if (HoldNow(fan, 0) && Off.IsOff(fan)) return;
        if (Off.IsOff(fan)) Reject(fan, "the protection curve did not match");
        else ApplyNow(Mode ?? "curves", [fan], keepMode: true); // turned back on while applying
    }

    /// <summary>
    /// Writes the hold curve (requested PWM up to 80 °C, 255 from 85 °C on) in place of the fan's curve: stores the User curve first,
    /// goes through Turbo and ApplyFanCurveButNotSave, like FULL. true = matched point by point.
    /// </summary>
    private bool HoldNow(string fan, int pwm)
    {
        RestoreUser([fan]);
        dynamic? c = Control(fan);
        if (c == null) { Error = $"{ThermalMap.BoardHeader(fan)}: header not found in Armoury Crate."; return false; }
        var snap = LoadSnap();
        if (!snap.ContainsKey(fan)) snap[fan] = Points(c.Profiles.Item(4).FanCurve);
        Directory.CreateDirectory(Path.GetDirectoryName(snapPath)!);
        File.WriteAllText(snapPath, JsonSerializer.Serialize(snap)); // write BEFORE touching anything: quitting/reopening restores the User curve
        c.ApplyIndex(3); // as in FULL: the current curve only accepts a change starting from an ASUS profile
        Thread.Sleep(900);
        dynamic cur = c.CurrentFanCurve;
        int n = (int)cur.Count;
        int[][] before = Points(cur); int d0 = (int)c.DutyCycle;
        Trace(fan + ": current before=" + Fmt(before) + " duty=" + d0);
        if (n < 2) return false;
        int[][] want = BoardFanOff.HoldCurve(n, pwm);
        for (int i = 0; i < n; i++) { cur.Item(i).Temperature = want[i][0]; cur.Item(i).Speed = want[i][1]; }
        c.ApplyFanCurveButNotSave(cur);
        int[][] got = Points(c.CurrentFanCurve); int d1 = (int)c.DutyCycle;
        Trace(fan + ": " + (pwm == 0 ? "protection" : $"manual {pwm}") + " requested=" + Fmt(want) + " read=" + Fmt(got) + " duty=" + d1);
        return Same(got, want);
    }

    /// <summary>OFF failed: turns it back on (User curve + current mode) and notifies the UI. A curve is never left half-written.</summary>
    private void Reject(string fan, string why)
    {
        Off.TurnOn(fan);
        try { ApplyNow(Mode ?? "curves", [fan], keepMode: true); } catch (Exception ex) { Trace("ERROR turning on " + ex); }
        Error = $"{ThermalMap.BoardHeader(fan)}: {why}; fan turned back on."; // after ApplyNow, which clears the error
        OffRejected?.Invoke(fan);
    }

    private void ApplyNow(string mode, IReadOnlyCollection<string> fans, bool keepMode = false)
    {
        {
            if (!Available || mgr == null) return;
            Error = null;
            Trace($"Apply {mode} [{string.Join(",", fans)}]");
            // Any mode starts by restoring the User curves of the fans that were on FULL.
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
                File.WriteAllText(snapPath, JsonSerializer.Serialize(snap)); // write BEFORE touching anything
                // Measured: the current curve only accepts the change starting from an ASUS profile (Turbo); starting from User it has no effect.
                // Switches all of them to Turbo at once and waits only once (before: 1.5 s per fan).
                foreach (var name in fans) { dynamic? c0 = Control(name); c0?.ApplyIndex(3); }
                Thread.Sleep(900);
                foreach (var name in fans)
                {
                    dynamic? c = Control(name); if (c == null) continue;
                    dynamic cur = c.CurrentFanCurve;
                    int[][] p0 = Points(cur); int d0 = (int)c.DutyCycle;
                    Trace(name + ": current before=" + Fmt(p0) + " duty=" + d0);
                    for (int i = 0; i < (int)cur.Count; i++) cur.Item(i).Speed = 255;
                    c.ApplyFanCurveButNotSave(cur);
                    int[][] p1 = Points(c.CurrentFanCurve); int[][] pu = Points(c.Profiles.Item(4).FanCurve); int d1 = (int)c.DutyCycle;
                    Trace(name + ": after current=" + Fmt(p1) + " user=" + Fmt(pu) + " duty=" + d1);
                }
            }
            else if (ProfileIndex(mode) is int idx)
                foreach (var name in fans) { dynamic? c = Control(name); c?.ApplyIndex(idx); }
            if (!keepMode) Mode = mode == "curves" ? null : mode;
        }
    }

    /// <summary>Stores the fans in OFF/MANUAL (to reapply when the app opens).</summary>
    private void RememberOffManual()
    {
        var man = Off.Manual.Select(f => (f, Off.ManualPercent(f))).Where(x => x.Item2 != null).ToDictionary(x => x.f, x => x.Item2!.Value);
        FanChoices.Update(d => d with { Off = Off.Off.ToList(), Manual = man });
    }

    /// <summary>App exit: returns everything to the user's curves and waits for it to finish.</summary>
    public void RestoreAndWait(TimeSpan timeout)
    {
        FanChoices.Enabled = false; // the user's choice stays stored; this is only the safe restore
        if (!Available) return;
        Off.ClearAll(); Off.ClearAllManual(); // off/manual do not persist: each one's User curve comes back along with the others
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
