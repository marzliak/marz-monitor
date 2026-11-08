using System.Text.Json;

namespace MonitorTray.Gpu;

public enum FanMode { Unknown, Automatic, Manual, Mixed }

/// <summary>Immutable fan-control state for the UI.</summary>
public sealed record FanState(
    GpuReading? Reading, bool Holding, uint? RequestedPercent, string? Error, string? SafetyNotice,
    bool CanControl, string? ReadOnlyReason, bool Busy)
{
    public static FanState Starting { get; } = new(null, false, null, "Starting NVML read", null, false, null, false);

    public FanMode Mode => Reading == null || Reading.Fans.Count == 0 || Reading.Fans.Any(f => f.Policy is not (0 or 1)) ? FanMode.Unknown
        : Reading.Fans.All(f => f.Policy == 0) ? FanMode.Automatic : Reading.Fans.All(f => f.Policy == 1) ? FanMode.Manual : FanMode.Mixed;

    public bool IsFresh(DateTimeOffset now) => Reading != null && Error == null
        && now >= Reading.CapturedAtUtc.AddSeconds(-1) && now - Reading.CapturedAtUtc <= TimeSpan.FromSeconds(6);

    /// <summary>Reason why a preset below 100% is blocked right now (null = allowed).</summary>
    public string? BlockFor(uint percent, DateTimeOffset now)
    {
        if (!CanControl) return ReadOnlyReason ?? "Control unavailable.";
        if (Busy) return "Command in progress.";
        if (!IsFresh(now)) return "Reading missing or stale.";
        if (percent >= 100) return null;
        return GpuSafety.ReductionBlock(Reading!, percent);
    }
}

/// <summary>
/// Sole owner of NVML control in this app (same logic as CmpInf's GpuTray, without WinForms).
/// - Uses the same global mutex as CmpInf ("Global\CmpInf.NvidiaFans.v1"): only one process writes to the fans.
///   If CmpInf holds it, this app stays read-only.
/// - Startup is always read-only; a previous manual state is not taken over.
/// - Commands apply on click (no dialog); safety: GpuSafety + per-fan verification + identity.
/// - Quitting returns the fans to automatic (GpuFanController.Close).
/// </summary>
public sealed class GpuFanService : IDisposable
{
    public const string MutexName = @"Global\CmpInf.NvidiaFans.v1";
    private readonly object gate = new();
    private readonly Func<IGpuApi> apiFactory;
    private readonly string? statusPath;
    private Timer? poll;
    private Mutex? owner;
    private bool ownsMutex, closing, disposed;
    private volatile bool busy;
    private IGpuApi? api;
    private GpuFanController? controller;
    private FanState state = FanState.Starting;
    private string? readOnlyReason;
    public event Action<FanState>? Changed;

    public FanState State => Volatile.Read(ref state);

    public GpuFanService(Func<IGpuApi>? apiFactory = null, string? statusPath = null, bool takeMutex = true)
    {
        this.apiFactory = apiFactory ?? (() => new NvmlGpuApi());
        this.statusPath = statusPath;
        if (takeMutex)
        {
            try
            {
                owner = new Mutex(false, MutexName);
                try { ownsMutex = owner.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            }
            catch (Exception ex) { ownsMutex = false; readOnlyReason = "Control mutex unavailable: " + ex.Message; }
            if (!ownsMutex) readOnlyReason ??= "Another program (GPU by MarzOps/CmpInf) controls the fans. Read-only.";
        }
        else ownsMutex = true;
    }

    public void Start() => poll = new Timer(_ => Poll(), null, 0, 1000);

    private void Poll()
    {
        if (!Monitor.TryEnter(gate)) return; // no queued/overlapping samples
        try
        {
            if (closing) return;
            try
            {
                if (controller == null)
                {
                    api ??= apiFactory();
                    var found = api.Discover();
                    if (found.Count != 1) throw new InvalidOperationException(found.Count == 0 ? "No NVIDIA GPU found." : "Control requires exactly one NVIDIA GPU.");
                    if (found[0].FanCount < 1) throw new InvalidOperationException("GPU has no fans controllable by the driver.");
                    controller = new GpuFanController(api, found[0]);
                }
                if (controller.IsHoldingManual) controller.Maintain();
                UpdateReading();
            }
            catch (Exception ex) { SetError(ErrorText(ex)); }
        }
        finally { Monitor.Exit(gate); }
    }

    private void UpdateReading()
    {
        var reading = controller!.Read();
        string? notice = controller.SafetyNotice;
        if (!controller.IsHoldingManual && reading.Fans.Any(f => f.Policy != 0))
            notice = "Previous manual/unknown state, not taken over by this app. Return to automatic or apply a preset.";
        Publish(new FanState(reading, controller.IsHoldingManual, controller.RequestedPercent, null, notice, ownsMutex && !closing, readOnlyReason, busy));
    }

    private void SetError(string message)
    {
        var old = State;
        Publish(old with { Error = message, Holding = controller?.IsHoldingManual ?? old.Holding, RequestedPercent = controller?.RequestedPercent ?? old.RequestedPercent,
            SafetyNotice = controller?.SafetyNotice ?? old.SafetyNotice, CanControl = ownsMutex && !closing && controller != null, ReadOnlyReason = readOnlyReason, Busy = busy });
    }

    private void Publish(FanState s)
    {
        Volatile.Write(ref state, s);
        WriteStatus(s);
        Changed?.Invoke(s);
    }

    /// <summary>Status for the installer: it only forces shutdown if it proves no fan is in manual.</summary>
    private void WriteStatus(FanState s)
    {
        if (statusPath == null) return;
        try
        {
            var dir = Path.GetDirectoryName(statusPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new
            {
                WrittenAtUtc = DateTimeOffset.UtcNow.ToString("O"), Owner = ownsMutex,
                Gpu = new { HoldingManual = s.Holding, s.RequestedPercent, Fans = s.Reading?.Fans.Select(f => new { f.Index, f.Policy, f.SpeedPercent, f.TargetPercent }) },
                s.Error,
            });
            var tmp = Path.Combine(dir, ".fan-status." + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(tmp, json);
            File.Move(tmp, statusPath, overwrite: true);
        }
        catch (Exception) { }
    }

    /// <summary>target = null → automatic. Applies and verifies each fan; errors go to State.Error and are rethrown.</summary>
    public Task CommandAsync(uint? target) => Task.Run(() =>
    {
        if (busy || closing || disposed) throw new InvalidOperationException("Control busy or shutting down.");
        var snap = State;
        if (!ownsMutex) throw new InvalidOperationException(readOnlyReason ?? "Read-only.");
        if (snap.Reading == null) throw new InvalidOperationException("No GPU reading.");
        var expected = snap.Reading.Identity;
        busy = true;
        Publish(snap with { Busy = true });
        try
        {
            lock (gate)
            {
                try
                {
                    if (closing || controller == null || api == null) throw new InvalidOperationException("GPU control closed.");
                    var actual = api.Discover().Single(g => g.Uuid == expected.Uuid);
                    if (actual != expected) throw new InvalidOperationException("GPU identity changed; command refused.");
                    if (target.HasValue) controller.SetSpeed(target.Value); else controller.RestoreAutomatic();
                    busy = false;
                    UpdateReading();
                }
                catch (Exception ex) { busy = false; SetError(ErrorText(ex)); throw; }
            }
        }
        finally { busy = false; }
    });

    /// <summary>Before quitting: returns to automatic. false = restore not confirmed (the app must stay open).</summary>
    public bool TryClose(TimeSpan timeout)
    {
        if (closing) return true;
        var work = Task.Run(() =>
        {
            lock (gate)
            {
                try { controller?.Close(); if (controller != null) UpdateReading(); }
                catch (Exception ex) { SetError(ErrorText(ex)); throw; }
            }
        });
        try
        {
            if (!work.Wait(timeout)) throw new TimeoutException("NVML did not respond in time.");
            closing = true;
            poll?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            var old = State;
            Volatile.Write(ref state, old with { Error = "Restore not confirmed: " + ErrorText(ex) });
            return false;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (!closing) { try { controller?.Close(); } catch (Exception) { } closing = true; }
            poll?.Dispose(); poll = null;
            try { api?.Dispose(); } catch (Exception) { }
            api = null;
        }
        if (ownsMutex && owner != null) { try { owner.ReleaseMutex(); } catch (Exception) { } }
        ownsMutex = false;
        owner?.Dispose(); owner = null;
    }

    private static string ErrorText(Exception ex) => ex is AggregateException a
        ? string.Join(" | ", a.Flatten().InnerExceptions.Select(e => e.Message).Distinct()) : ex.Message;
}
