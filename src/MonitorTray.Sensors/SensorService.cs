using System.Diagnostics;
using MonitorTray.Core.Fans;
using MonitorTray.Core.History;
using MonitorTray.Core.Model;
using MonitorTray.Core.Normalize;

namespace MonitorTray.Sensors;

/// <summary>Orchestrates collection on its own thread and publishes immutable snapshots.</summary>
public sealed class SensorService : IDisposable
{
    private readonly IHardwareSource hardware;
    private readonly ISummarySource? summarySource;
    private readonly SimulatedSummary? simSummary;
    private readonly KnownDevices known;
    private readonly bool isAdmin;
    private readonly SensorTracker tracker = new();
    private readonly RateHistory netDown = new(), netUp = new();
    private volatile int historySeconds = 60;
    /// <summary>Network chart window (s), up to 2 h.</summary>
    public int HistorySeconds { get => historySeconds; set => historySeconds = Math.Clamp(value, 10, RateHistory.MaxSeconds); }
    private static long NowSec => Environment.TickCount64 / 1000;
    private readonly RingBuffer<float?> cpuHist = new(SensorTracker.HistoryLength + 1), gpuHist = new(SensorTracker.HistoryLength + 1), memHist = new(SensorTracker.HistoryLength + 1);

    private readonly Volumes volumeReader = new();
    private IReadOnlyList<VolumeInfo> volumes = [];
    private DateTime volumesAt = DateTime.MinValue;

    private void PushUsage(SummaryData s)
    {
        cpuHist.Push(s.CpuLoad); gpuHist.Push(s.GpuLoad);
        memHist.Push(s.MemTotal > 0 ? (float)(100.0 * s.MemUsed / s.MemTotal) : null);
    }
    private readonly object trackerGate = new();
    private readonly object readGate = new(); // the hardware source is never read by two threads at the same time
    private Thread? thread;
    private readonly AutoResetEvent wake = new(false);
    private volatile bool stop;
    private volatile int refreshMs = 1000;
    private volatile bool fullCollection;
    private volatile bool detailsOpen;
    private volatile bool hardwareChanged = true;
    private IReadOnlyList<RawSensor> lastRaw = [];
    private IReadOnlyList<Device> lastDevices = [];
    private volatile bool lhmBusy;
    private readonly Stopwatch lhmStarted = new();

    /// <summary>LHM (the most expensive read) at most once per second, even at a 0.5 s refresh (small tolerance for timer jitter).</summary>
    public const int LhmMinIntervalMs = 1000;
    public static bool LhmDue(long? msSinceLast) => msSinceLast is not long ms || ms >= LhmMinIntervalMs - 60;
    private volatile bool hardwareReady;

    public event Action<Snapshot>? Published;
    public Snapshot? Latest { get; private set; }

    /// <summary>Read access to the per-sensor history (UI thread only via snapshot; the tracker is lock-protected).</summary>
    public T WithTracker<T>(Func<SensorTracker, T> f) { lock (trackerGate) return f(tracker); }

    public SensorService(IHardwareSource hardware, ISummarySource? summary, KnownDevices known, bool isAdmin)
    {
        this.hardware = hardware; this.known = known; this.isAdmin = isAdmin;
        if (hardware.IsSimulated) simSummary = new SimulatedSummary(); else summarySource = summary;
        if (hardware is LhmSource lhm) lhm.HardwareChanged += () => hardwareChanged = true;
    }

    public int RefreshMs { get => refreshMs; set { refreshMs = value; wake.Set(); } }

    /// <summary>Popup or Details open → full collection (LHM). Closed → only the cheap Summary.</summary>
    /// <summary>Keeps LHM active even with popup/Details closed (keyboard screens need sensors).</summary>
    /// <remarks>Wakes the loop only on the off→on edge. Callers set it on every service event (keyboard sends, cooler
    /// status ~1 s); waking on each set made the loop tick back-to-back (keyboard send → set → tick → send…) at 100%+ CPU.</remarks>
    public bool KeepHardware { get => keepHardware; set { var was = keepHardware; keepHardware = value; if (value && !was) wake.Set(); } }
    private volatile bool keepHardware;
    /// <summary>Last raw reading (immutable); lets the keyboard query any sensor by id.</summary>
    public IReadOnlyList<RawSensor> LastRaw => lastRaw;

    public bool FullCollection { get => fullCollection; set { var was = fullCollection; fullCollection = value; if (value && !was) wake.Set(); } }
    public bool DetailsOpen
    {
        get => detailsOpen;
        set { detailsOpen = value; if (hardware is LhmSource l) l.RescanAllowed = value; }
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "SensorService", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private void Run()
    {
        try { summarySource?.Open(); } catch (Exception) { }
        if (simSummary != null)
        {
            // Simulated: network already with 2 h of past (on this thread, before the first tick).
            var now = NowSec;
            for (int i = RateHistory.MaxSeconds; i > 0; i--) { var q = simSummary.Next(); netDown.Add(q.NetDownBps, now - i); netUp.Add(q.NetUpBps, now - i); }
        }
        var openThread = new Thread(() =>
        {
            try { hardware.Open(); } catch (Exception) { }
            if (hardware.IsSimulated) Prefill();
            hardwareReady = true; // only after the prefill: no other reader competes with it
            wake.Set();
        }) { IsBackground = true, Name = "HardwareOpen" };
        openThread.Start();
        var sw = Stopwatch.StartNew();
        while (!stop)
        {
            var started = sw.Elapsed;
            try { Tick(); } catch (Exception) { }
            var wait = TimeSpan.FromMilliseconds(PeriodMs(refreshMs, fullCollection)) - (sw.Elapsed - started);
            if (wait > TimeSpan.Zero) wake.WaitOne(wait);
        }
    }

    /// <summary>
    /// Loop period: the chosen refresh while the popup/Details is open; at least 1 s when they are closed (tray tooltip,
    /// tray icon temperature, keyboard screens and fan safety do not need 2 Hz).
    /// </summary>
    public static int PeriodMs(int refreshMs, bool windowsOpen) => windowsOpen ? refreshMs : Math.Max(refreshMs, 1000);

    private void Prefill()
    {
        // Simulated: network with 2 h of past and 120 sensor ticks, so the charts start out full.
        for (int i = 0; i < SensorTracker.HistoryLength; i++)
        {
            IReadOnlyList<RawSensor> raw;
            lock (readGate) raw = hardware.Read();
            lock (trackerGate) { tracker.BeginTick(); foreach (var r in raw) tracker.Push(r.Id, r.Value); }
            var s = simSummary!.Next();
            PushUsage(s);
            lastRaw = raw;
        }
    }

    private void Tick()
    {
        SummaryData summary;
        if (simSummary != null) summary = simSummary.Next();
        else
        {
            var s = summarySource?.Sample() ?? default;
            summary = new SummaryData(s.CpuLoad, null, null, null, null, null, s.MemUsed, s.MemTotal, s.DiskRead, s.DiskWrite, s.NetDown, s.NetUp);
        }
        netDown.Add(summary.NetDownBps, NowSec); netUp.Add(summary.NetUpBps, NowSec);

        if ((fullCollection || keepHardware) && hardwareReady)
        {
            if (hardware.IsSimulated) CollectHardware();
            else if (!lhmBusy && LhmDue(lhmStarted.IsRunning ? lhmStarted.ElapsedMilliseconds : (long?)null))
            {
                lhmStarted.Restart();
                // LHM is slow (Storage/Motherboard): runs off this thread; a late tick is skipped, not queued.
                lhmBusy = true;
                ThreadPool.UnsafeQueueUserWorkItem(_ => { try { CollectHardware(); } finally { lhmBusy = false; } }, null);
            }
        }

        var raw = lastRaw;
        if (simSummary == null && raw.Count > 0)
        {
            var (cl, ct, cp) = SummaryPicker.Cpu(raw);
            var (gl, gt, gp) = SummaryPicker.Gpu(raw);
            summary = summary with { CpuLoad = summary.CpuLoad ?? cl, CpuTemp = ct, CpuPower = cp, GpuLoad = gl, GpuTemp = gt, GpuPower = gp };
        }
        var status = new SourceStatus(isAdmin, hardwareReady, hardware.IsSimulated, hardware.Notice);
        PushUsage(summary);
        // Drive space: only with popup/Details open, every 5 s.
        if (fullCollection && DateTime.UtcNow - volumesAt > TimeSpan.FromSeconds(5))
        {
            volumesAt = DateTime.UtcNow;
            try { volumes = volumeReader.Read(); } catch (Exception) { }
        }
        var win = historySeconds;
        var tempSources = new Dictionary<string, string>();
        var snap = new Snapshot(DateTimeOffset.Now, summary, netDown.View(win), netUp.View(win), lastDevices, raw.Count, status)
        { MbFanRpm = MbRpm(raw), MbFanControl = MbRpm(raw, "Control"), MbTemps = MbTemp(raw), Temps = TempCards.Resolve(raw, tempSources), TempSources = tempSources,HistoryPoints = RateHistory.Points(win), CpuHistory = cpuHist.ToArray(), GpuHistory = gpuHist.ToArray(), MemHistory = memHist.ToArray(), Volumes = fullCollection && simSummary == null ? SafeRates(volumes) : volumes };
        Latest = snap;
        Published?.Invoke(snap);
    }

    private IReadOnlyList<VolumeInfo> SafeRates(IReadOnlyList<VolumeInfo> v) { try { return volumeReader.WithRates(v); } catch (Exception) { return v; } }

    private static readonly string[] MbTempNames = ["Motherboard", "Chipset", "VRM"];
    private static Dictionary<string, float> MbTemp(IReadOnlyList<RawSensor> raw)
    {
        var d = new Dictionary<string, float>();
        foreach (var r in raw)
            if (r.Family is "SuperIO" or "EmbeddedController" && r.KindRaw == "Temperature" && MbTempNames.Contains(r.Name) && r.Value is float v and > 1 and < 150) d.TryAdd(r.Name, v);
        return d;
    }

    private static Dictionary<int, float> MbRpm(IReadOnlyList<RawSensor> raw, string kind = "Fan")
    {
        var d = new Dictionary<int, float>();
        foreach (var r in raw)
            if (r.Family == "SuperIO" && r.KindRaw == kind && r.Value is float v && BoardFanMap.TrailingIndex(r.Id) is int i) d[i] = v;
        return d;
    }

    private void CollectHardware()
    {
        IReadOnlyList<RawSensor> raw;
        lock (readGate) raw = hardware.Read();
        IReadOnlyList<Device> devices;
        lock (trackerGate)
        {
            tracker.BeginTick();
            foreach (var r in raw) tracker.Push(r.Id, r.Value);
            if (hardwareChanged) { tracker.Prune(); hardwareChanged = false; }
            devices = detailsOpen || lastDevices.Count == 0 ? DeviceBuilder.Build(raw, tracker, known.IsNew) : lastDevices;
        }
        lastRaw = raw;
        lastDevices = devices;
    }

    public void SaveKnownDevices() => known.Save();

    /// <summary>Resume from hibernation: PDH and LHM counters become invalid.</summary>
    public void OnResume()
    {
        if (summarySource is SystemSummarySource s) s.Reset();
        if (hardware is LhmSource l) l.Reset();
        hardwareChanged = true;
    }

    public void Dispose()
    {
        stop = true; wake.Set();
        thread?.Join(3000);
        try { known.Save(); } catch (Exception) { }
        hardware.Dispose(); summarySource?.Dispose();
    }
}
