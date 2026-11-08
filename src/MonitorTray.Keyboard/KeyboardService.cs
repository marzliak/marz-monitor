using System.Runtime.InteropServices;
using System.Diagnostics;
using MonitorTray.Core;
using MonitorTray.Core.Keyboard;

namespace MonitorTray.Oled;

public sealed record KeyboardState(bool Enabled, bool WheelEnabled, bool WheelReady, bool MuteCompensation, bool MuteReady,
    int Page, int PageCount, bool Pinned, OledDelivery Delivery, string? GgStatus);

/// <summary>
/// OLED loop (ported from GPU by MarzOps): composes the current page from a value provider,
/// sends it through OledOutput, honors the per-page duration and reacts immediately to wheel gestures.
/// Wheel: Raw Input only on the Apex Consumer Control (VID_1038 PID_1640 MI_03 COL01); 1 click advances, 2 clicks pin.
/// Capture needs a message window on the UI thread: the App calls <see cref="AttachWindow"/> and forwards WM_INPUT.
/// </summary>
public sealed class KeyboardService : IDisposable
{
    private readonly string path;
    private readonly Func<string, double?> values;
    private readonly Func<string, string> names;
    private readonly OledOutput output;
    private readonly WheelState wheel = new();
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private KeyboardConfig config;
    private CancellationTokenSource? loop;
    private R33Input? input;
    private R33Audio? audio;
    private IntPtr window;
    private bool disposed;
    private int shownRevision = -1;
    public event Action<KeyboardState>? Changed;
    public KeyboardConfig Config => config;
    private long Now => watch.ElapsedMilliseconds;

    public KeyboardService(string path, Func<string, double?> values, Func<string, string> names, IOledTransport? transport = null)
    {
        this.path = path; this.values = values; this.names = names;
        output = new OledOutput(transport ?? new GameSenseClient());
        config = Load(path);
        wheel.Configure(config.Pages.Select(p => p.DurationMs).ToArray(), Now);
    }

    public static KeyboardConfig Load(string path)
    {
        try { if (File.Exists(path)) return KeyboardConfig.Parse(File.ReadAllText(path)); } catch (Exception) { }
        var migrated = MigrateFromCmpInf();
        return migrated ?? new KeyboardConfig();
    }

    /// <summary>First run: imports the pages from GPU by MarzOps (profile-rodinha.json), if present.
    /// "profile-rodinha.json" under CmpInf-Apex is the other program's own file name: keep it unchanged.</summary>
    private static KeyboardConfig? MigrateFromCmpInf()
    {
        try
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CmpInf-Apex", sid ?? "x", "state", "profile-rodinha.json");
            if (!File.Exists(f)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
            var pages = new List<OledPage>();
            foreach (var p in doc.RootElement.GetProperty("Pages").EnumerateArray())
            {
                var lines = p.GetProperty("Sensors").EnumerateArray().Take(2).Select(s => KeyboardConfig.L(
                    CalculatedSensors.MapLegacy(s.GetProperty("Identifier").GetString() ?? ""),
                    s.TryGetProperty("Prefix", out var px) ? px.GetString() ?? "" : "",
                    s.TryGetProperty("Suffix", out var sx) ? sx.GetString() ?? "" : "",
                    s.TryGetProperty("DecimalPlaces", out var d) ? d.GetInt32() : 0)).ToList();
                pages.Add(new OledPage { DurationMs = p.GetProperty("DurationMs").GetInt32(), IconId = p.TryGetProperty("IconId", out var ic) ? ic.GetInt32() : 0, Lines = lines });
            }
            return pages.Count == 0 ? null : new KeyboardConfig { Pages = pages }.Normalized();
        }
        catch (Exception) { return null; }
    }

    public void Save(KeyboardConfig c)
    {
        config = c.Normalized();
        try { AtomicFile.WriteAllText(path, config.ToJson()); } catch (Exception ex) { Log.Warn("keyboard.json: " + ex.Message); }
        wheel.Configure(config.Pages.Select(p => p.DurationMs).ToArray(), Now);
        _ = output.SetEnabledAsync(config.Enabled, CancellationToken.None);
        if (window != IntPtr.Zero) { if (config.WheelEnabled) EnableWheel(); else DisableWheel(); }
        UpdateAudio();
        Publish(force: true);
    }

    public void Start()
    {
        loop = new CancellationTokenSource();
        var token = loop.Token;
        _ = output.SetEnabledAsync(config.Enabled, token);
        _ = Task.Run(() => Run(token), token);
    }

    private async Task Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            long started = Now;
            var v = wheel.Snapshot(started);
            try
            {
                if (config.Enabled)
                {
                    var page = config.Pages[Math.Clamp(v.Page, 0, config.Pages.Count - 1)];
                    var lines = OledComposer.DecoratePinned(OledComposer.Compose(page, values, names), v.Pinned);
                    using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                    bounded.CancelAfter(TimeSpan.FromSeconds(4));
                    await output.PresentAsync(lines, page.IconId, bounded.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warn("OLED: " + ex.Message); }
            Publish(force: true);
            // Waits up to 1 s, waking immediately if the wheel changes the page.
            while (!token.IsCancellationRequested)
            {
                var cur = wheel.Snapshot(Now);
                if (cur.Revision != v.Revision) break;
                long remaining = started + Math.Min(cur.Interval, 1000) - Now;
                if (remaining <= 0) break;
                try { await Task.Delay((int)Math.Min(25, remaining), token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                audio?.Pump();
            }
        }
    }

    public void Next() { wheel.Next(Now); Publish(true); }
    public void TogglePin() { wheel.TogglePin(Now); Publish(true); }
    public void Select(int page)
    {
        var n = config.Pages.Count;
        if (page < 0 || page >= n) return;
        for (int i = 0; i < n && wheel.Snapshot(Now).Page != page; i++) wheel.Next(Now);
        Publish(true);
    }

    // ---- Wheel (UI thread)
    public void AttachWindow(IntPtr hwnd) { window = hwnd; if (config.WheelEnabled) EnableWheel(); }

    public void OnWindowMessage(int msg, IntPtr lParam)
    {
        try
        {
            if (msg == 0x00FF) input?.Read(lParam);
            else if (msg == 0x00FE) input?.DeviceChanged(lParam);
            audio?.Pump();
        }
        catch (Exception ex) { Log.Warn("Wheel: " + ex.Message); DisableWheel(); }
    }

    private void Media(bool target, bool down)
    {
        if (!target) { if (down) audio?.ForeignPress(); return; }
        if (down) { if (wheel.Press(Now)) audio?.ApexPress(); }
        else wheel.Release();
        Publish(false);
    }

    private void EnableWheel()
    {
        if (input != null || window == IntPtr.Zero || disposed) return;
        try { input = new R33Input(window, Media, () => wheel.CancelInput(Now, false)); Log.Info("Wheel active (Apex Consumer Control)."); }
        catch (Exception ex) { input = null; Log.Warn("Wheel capture unavailable: " + ex.Message); }
        UpdateAudio();
    }

    private void DisableWheel()
    {
        try { if (input != null && input.TryDisposeWithRetries(3)) input = null; } catch (Exception ex) { Log.Warn("Wheel: " + ex.Message); }
        wheel.CancelInput(Now, true);
        UpdateAudio();
    }

    private void UpdateAudio()
    {
        bool want = input != null && config.MuteCompensation;
        if (want && audio == null) { try { audio = new R33Audio(); } catch (Exception ex) { Log.Warn("Mute: " + ex.Message); } }
        else if (!want && audio != null) { audio.Dispose(); audio = null; }
    }

    private void Publish(bool force)
    {
        var v = wheel.Snapshot(Now);
        if (!force && v.Revision == shownRevision) return;
        shownRevision = v.Revision;
        string? gg = GameSenseClient.CorePropsPath() == null ? "SteelSeries GG not found" : null;
        WriteStatus(v);
        Changed?.Invoke(new KeyboardState(config.Enabled, config.WheelEnabled, input != null, config.MuteCompensation, audio?.Ready == true,
            v.Page, config.Pages.Count, v.Pinned, output.Snapshot, gg));
    }

    private DateTime statusAt = DateTime.MinValue;
    /// <summary>keyboard-status.json next to keyboard.json (at most every 2 s): diagnostics; an accepted HTTP call does not prove pixels on the keyboard.</summary>
    private void WriteStatus(View v)
    {
        if ((DateTime.UtcNow - statusAt).TotalSeconds < 2) return;
        statusAt = DateTime.UtcNow;
        try
        {
            var d = output.Snapshot;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                WrittenAtUtc = DateTimeOffset.UtcNow.ToString("O"), config.Enabled, config.WheelEnabled, WheelReady = input != null,
                MuteCompensation = config.MuteCompensation, MuteReady = audio?.Ready == true, Page = v.Page + 1, Pages = config.Pages.Count, v.Pinned,
                v.Singles, v.Doubles, d.Status, d.Error, SentAtUtc = d.SentAt?.ToString("O"), d.Lines,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            AtomicFile.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "state", "keyboard-status.json"), json);
        }
        catch (Exception) { }
    }

    public KeyboardState State
    {
        get
        {
            var v = wheel.Snapshot(Now);
            return new(config.Enabled, config.WheelEnabled, input != null, config.MuteCompensation, audio?.Ready == true, v.Page, config.Pages.Count, v.Pinned, output.Snapshot,
                GameSenseClient.CorePropsPath() == null ? "SteelSeries GG not found" : null);
        }
    }

    /// <summary>Exit: releases the OLED (GG shows its own screen again) and lets go of the wheel.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        loop?.Cancel();
        try { using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3)); output.SetEnabledAsync(false, t.Token).Wait(3500); } catch (Exception) { }
        DisableWheel();
        audio?.Dispose(); audio = null;
    }
}
