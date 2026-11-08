using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorTray.Ryujin;

/// <summary>One LCD item: sensor (LHM id, "calc:..." or "/ryujin2/..."), ASCII label and group (unit/icon).</summary>
public sealed record LcdSlot
{
    public string SensorId { get; init; } = "";
    public string Name { get; init; } = "";
    public LcdGroup Group { get; init; } = LcdGroup.Temperature;
}

/// <summary>One LCD screen: 1 to 4 items and the time on screen.</summary>
public sealed record LcdPage
{
    public int DurationMs { get; init; } = 10000;
    public IReadOnlyList<LcdSlot> Slots { get; init; } = [];
}

/// <summary>Cooler LCD settings (%APPDATA%\MonitorTray\ryujin-lcd.json). Off by default.</summary>
public sealed record LcdConfig
{
    public const int MaxPages = 20, MaxSlots = 4;
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; }
    public IReadOnlyList<LcdPage> Pages { get; init; } = DefaultPages;

    public static IReadOnlyList<LcdPage> DefaultPages { get; } =
    [
        new() { Slots = [S("calc:cpu-temp", "CPU Temp", LcdGroup.Temperature), S(RyujinSensorIds.Water, "Water", LcdGroup.Temperature),
                         S(RyujinSensorIds.Pump, "Pump", LcdGroup.FanSpeed), S("calc:cpu-clock-max", "CPU Clock", LcdGroup.Frequency)] },
        new() { Slots = [S("calc:gpu-temp", "GPU Temp", LcdGroup.Temperature), S(RyujinSensorIds.RadiatorFan(0), "Radiator", LcdGroup.FanSpeed),
                         S(RyujinSensorIds.Embedded, "Fan AIO", LcdGroup.FanSpeed), S("calc:cpu-clock-avg", "Clock avg", LcdGroup.Frequency)] },
    ];

    public static LcdSlot S(string id, string name, LcdGroup g) => new() { SensorId = id, Name = name, Group = g };

    /// <summary>Old default slot names (up to 0.1.33) → new defaults. Names the user typed are kept.</summary>
    private static readonly Dictionary<string, string> LegacyNames = new(StringComparer.Ordinal)
    { ["Agua"] = "Water", ["Bomba"] = "Pump", ["Radiador"] = "Radiator", ["Clock medio"] = "Clock avg" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Invalid → default (off). Out-of-range values are clamped.</summary>
    public static LcdConfig Parse(string json)
    {
        LcdConfig? c;
        try { c = JsonSerializer.Deserialize<LcdConfig>(json, Json); } catch (Exception) { return new(); }
        if (c is null || c.Version != 1) return new();
        return c.Normalized();
    }

    public LcdConfig Normalized()
    {
        var pages = (Pages ?? []).Take(MaxPages).Select(p => p with
        {
            DurationMs = Math.Clamp(p.DurationMs, 2000, 60000),
            Slots = (p.Slots ?? []).Take(MaxSlots).Select(s => s with
            {
                SensorId = s.SensorId ?? "",
                Name = LcdText.Ascii(LegacyNames.GetValueOrDefault(s.Name ?? "", s.Name ?? ""), RyujinProtocol.LcdNameMax),
                Group = Enum.IsDefined(s.Group) ? s.Group : LcdGroup.Temperature,
            }).ToList(),
        }).Where(p => p.Slots.Count > 0).ToList();
        if (pages.Count == 0) pages = DefaultPages.ToList();
        return this with { Pages = pages };
    }
}

/// <summary>Ids of the cooler's own sensors (published alongside the LHM ones).</summary>
public static class RyujinSensorIds
{
    public const string Device = "/ryujin2";
    public const string Water = Device + "/temperature/0";
    public const string Pump = Device + "/fan/0";
    public const string Embedded = Device + "/fan/1";
    public static string RadiatorFan(int i) => $"{Device}/fan/{2 + i}";
    public const string RadiatorDuty = Device + "/control/0";
    public const string PumpDuty = Device + "/control/1";
    public const string EmbeddedDuty = Device + "/control/2";
}

/// <summary>Builds each item's text like Armoury Crate (value + 1 unit character).</summary>
public static class LcdComposer
{
    public static string Unit(LcdGroup g) => g switch
    {
        LcdGroup.Temperature => LcdText.Celsius,
        LcdGroup.Frequency => LcdText.GHz,
        LcdGroup.FanSpeed => LcdText.Rpm,
        LcdGroup.Voltage => LcdText.Volt,
        _ => LcdText.Flow,
    };

    public static string Value(LcdGroup g, double? v)
    {
        var inv = CultureInfo.InvariantCulture;
        if (v is not double x || double.IsNaN(x) || double.IsInfinity(x)) return "N/A" + Unit(g);
        return g switch
        {
            LcdGroup.Temperature => x.ToString("0.0", inv),
            LcdGroup.Frequency => (x > 100 ? x / 1000 : x).ToString("0.000", inv), // accepts MHz from LHM or GHz from the calculated ones
            LcdGroup.FanSpeed => Math.Round(x).ToString("0", inv),
            LcdGroup.Voltage => x.ToString("0.000", inv),
            _ => x.ToString("0.0", inv),
        } + Unit(g);
    }
}

public sealed record RyujinSnapshot(DateTimeOffset At, string? Firmware, RyujinPumpSensors? Pump, RyujinFanReading? Fans, int? PumpPercent, int? EmbeddedPercent);

public sealed record LcdStatus(bool Enabled, bool DeviceFound, int Page, int PageCount, string? Error, DateTimeOffset? SentAt, IReadOnlyList<string> Lines);

/// <summary>Cooler fan-control state for the UI.</summary>
/// <param name="SourceC">Source temperature the curve uses (~5 s average, <see cref="TempSmoother"/>).</param>
public sealed record CoolerStatus(bool Enabled, bool Controlling, CoolerTargets? Applied, double? SourceC, string? Notice, string? Tuning)
{
    /// <summary>Auto Tuning: fraction done (0..1); null = not running.</summary>
    public double? TuneProgress { get; init; }
    /// <summary>Auto Tuning: seconds left (estimate).</summary>
    public int? TuneSecondsLeft { get; init; }
}

/// <summary>
/// Owner of the RYUJIN II in Marz: reads pump/water/fans (always, read-only) and, if the LCD is on, draws the screens.
/// Writes ONLY 0x53 (text) and 0x52 (dark layout); never fans, pump, brightness or media.
/// Armoury Crate also writes to the LCD every ~2 s: with both active, the screens fight.
/// </summary>
public sealed class RyujinService : IDisposable
{
    private readonly string path, fansPath;
    private readonly Func<string, double?> values;
    private CoolerConfig fanConfig;
    private int? sentPump, sentEmb, sentRadDuty;
    private int curPump = -1, curRad = -1, curEmb = -1;
    private long lastFanWrite;
    private int foreignHits;
    private readonly TempSmoother srcSmooth = new();
    private string? smoothId;
    private long smoothAt;
    private volatile bool tuning;
    private CoolerStatus fanStatus = new(false, false, null, null, null, null);
    public CoolerStatus FanStatus => fanStatus;
    public CoolerConfig FanConfig { get { lock (gate) return fanConfig; } }
    public event Action<CoolerStatus>? FanChanged;
    private readonly RyujinHidReader hid = new();
    private readonly object gate = new();
    private LcdConfig config;
    private Thread? thread;
    private volatile bool stop;
    private readonly AutoResetEvent wake = new(false);
    private int page;
    private long pageStarted;
    private string? layoutKey;
    private string? firmware;
    private LcdStatus status = new(false, false, 0, 0, null, null, []);

    public event Action<LcdStatus>? Changed;
    public RyujinSnapshot? Latest { get; private set; }
    public LcdConfig Config { get { lock (gate) return config; } }
    public LcdStatus Status => status;
    public bool DeviceFound => hid.IsOpen || simulated;
    private bool simulated;

    /// <summary>Preview only: made-up reading, no USB, control "on" in Standard.</summary>
    public void Simulate(RyujinSnapshot s, CoolerTargets applied, double source)
    {
        simulated = true; Latest = s;
        lock (gate) fanConfig = fanConfig with { Enabled = true };
        fanStatus = new CoolerStatus(true, true, applied, source, null, null);
    }

    public RyujinService(string path, Func<string, double?> values, string? fansPath = null)
    {
        this.path = path; this.values = values;
        this.fansPath = fansPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "ryujin-fans.json");
        config = Load(path);
        fanConfig = LoadFans(this.fansPath);
    }

    public static CoolerConfig LoadFans(string p)
    {
        try { if (File.Exists(p)) return CoolerConfig.Parse(File.ReadAllText(p)); } catch (Exception) { }
        return new CoolerConfig();
    }

    public void SaveFans(CoolerConfig c)
    {
        c = c.Normalized();
        lock (gate) { fanConfig = c; foreignHits = 0; }
        WriteAtomic(fansPath, c.ToJson());
        wake.Set();
    }

    /// <summary>Custom setup (curves/fixed %) from before the last preset: CURVES goes back to it, even after restarting the app.</summary>
    private string KeptPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(fansPath))!, "ryujin-curves.json");

    private CoolerConfig? LoadKept()
    {
        try { return File.Exists(KeptPath) ? CoolerConfig.Parse(File.ReadAllText(KeptPath)) : null; } catch (Exception) { return null; }
    }

    /// <summary>Single preset on pump and radiator (first saves the custom setup in use). <paramref name="enable"/> turns on control by Marz.</summary>
    public void ApplyPreset(RyujinCurves.Preset p, bool enable)
    {
        var c = FanConfig;
        if (CoolerModes.ToKeep(c, LoadKept()) is { } keep) WriteAtomic(KeptPath, keep.Normalized().ToJson());
        var next = CoolerModes.WithPreset(c, p);
        SaveFans(enable ? next with { Enabled = true } : next);
    }

    /// <summary>CURVES: goes back to the saved custom setup (or the default curves) and turns on control by Marz.</summary>
    public void ApplyCurves() => SaveFans(CoolerModes.Curves(FanConfig, LoadKept()));

    /// <summary>
    /// Fix on startup (outside game mode): an embedded fan stuck on FULL from a game mode that was not undone goes back to STD,
    /// in the setup in use and in the one saved for CURVES. true = fixed something.
    /// </summary>
    public bool UnstickEmbedded()
    {
        bool any = false;
        if (CoolerModes.UnstickEmbedded(FanConfig) is { } c) { SaveFans(c); any = true; }
        if (LoadKept() is { } k && CoolerModes.UnstickEmbedded(k) is { } k2) { WriteAtomic(KeptPath, k2.Normalized().ToJson()); any = true; }
        return any;
    }

    private static void WriteAtomic(string p, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p))!);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, p, overwrite: true);
        }
        catch (Exception) { }
    }

    /// <summary>Radiator % to rpm table in use (Marz Auto Tuning or the Armoury Crate default).</summary>
    public IReadOnlyList<RpmPoint> RadiatorTable => FanConfig.RadiatorTable.Count >= 2 ? FanConfig.RadiatorTable : DefaultRadiatorTable;
    public static readonly RpmPoint[] DefaultRadiatorTable = [new(0, 0), new(10, 300), new(20, 420), new(30, 630), new(40, 810), new(50, 1020), new(60, 1140), new(70, 1320), new(80, 1500), new(90, 1680), new(100, 1890)];
    public static readonly RpmPoint[] PumpTable = [new(0, 840), new(10, 870), new(20, 900), new(30, 1140), new(40, 1470), new(50, 1650), new(60, 1890), new(70, 2130), new(80, 2310), new(90, 2520), new(100, 2800)];
    public static readonly RpmPoint[] EmbeddedTable = [new(0, 0), new(10, 390), new(20, 1200), new(30, 1860), new(40, 2460), new(50, 3000), new(60, 3450), new(70, 3870), new(80, 4230), new(90, 4590), new(100, 4800)];

    public static LcdConfig Load(string path)
    {
        try { if (File.Exists(path)) return LcdConfig.Parse(File.ReadAllText(path)); } catch (Exception) { }
        return new LcdConfig();
    }

    public void Save(LcdConfig c)
    {
        c = c.Normalized();
        lock (gate) { config = c; layoutKey = null; page = Math.Min(page, c.Pages.Count - 1); }
        WriteAtomic(path, c.ToJson());
        wake.Set();
    }

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "Ryujin", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    private void Run()
    {
        long nextOpen = 0;
        while (!stop)
        {
            var started = Environment.TickCount64;
            try
            {
                if (!hid.IsOpen && started >= nextOpen)
                {
                    if (hid.TryOpen()) firmware ??= hid.ReadFirmware();
                    else nextOpen = started + 10000;
                }
                if (hid.IsOpen) Poll();
                if (!tuning) ControlFans();
                Draw(started);
            }
            catch (Exception ex) { Publish(ex.Message); }
            var wait = 1000 - (int)(Environment.TickCount64 - started);
            if (wait > 0) wake.WaitOne(wait);
        }
    }

    private void Poll()
    {
        var pump = hid.ReadPump();
        var fans = hid.ReadFans();
        var cfgFrame = hid.Query(RyujinProtocol.CmdPumpConfig);
        (int, int)? pc = cfgFrame is { } f ? RyujinProtocol.ParsePumpConfig(f) : null;
        Latest = new RyujinSnapshot(DateTimeOffset.Now, firmware, pump, fans, pc?.Item1, pc?.Item2);
    }

    /// <summary>
    /// Control loop (~1 s): curve on the host like Armoury Crate. Writes only when the % changes or every 10 s (refresh).
    /// If the reading shows a value other than the last one written (another program writing), it stops and warns.
    /// </summary>
    private void ControlFans()
    {
        var c = FanConfig;
        if (!c.Enabled || !hid.IsOpen)
        {
            if (fanStatus.Controlling || fanStatus.Enabled != c.Enabled) PublishFans(new(c.Enabled, false, null, null, hid.IsOpen ? null : "cooler not found", null));
            sentPump = sentEmb = sentRadDuty = null; curPump = curRad = curEmb = -1; foreignHits = 0;
            srcSmooth.Reset(); smoothAt = 0;
            return;
        }
        var snap = Latest;
        // Another writer? Compares the reading with the last value written by Marz (3 s grace).
        if (foreignHits < 3 && sentRadDuty is int sd && snap?.Fans is { } f && Environment.TickCount64 - lastFanWrite > 3000)
        {
            bool foreign = Math.Abs(f.DutyRaw - sd) > 3 || (sentPump is int sp && snap.PumpPercent is int rp && Math.Abs(rp - sp) > 1);
            foreignHits = foreign ? foreignHits + 1 : 0;
            if (foreignHits >= 3)
            {
                PublishFans(new(true, false, fanStatus.Applied, fanStatus.SourceC, "another program is controlling the cooler (Armoury Crate?). Marz stopped writing.", null));
                return;
            }
        }
        if (foreignHits >= 3) return;
        double? raw = Value(c.SourceId);
        if (raw is double s && (s < 1 || s > 125)) raw = null;
        // The curve follows the source average (~5 s), not the instant value: the CPU swings a lot in short spikes.
        var nowMs = Environment.TickCount64;
        if (smoothId != c.SourceId) { srcSmooth.Reset(); smoothId = c.SourceId; }
        double? src = srcSmooth.Next(raw, smoothAt == 0 ? 1 : (nowMs - smoothAt) / 1000.0);
        smoothAt = nowMs;
        var t = CoolerLogic.Compute(c, src, snap?.Pump?.WaterTempC);
        bool panic = t.Reason?.StartsWith("water", StringComparison.Ordinal) == true;
        curPump = curPump < 0 || panic ? t.PumpPercent : CoolerLogic.Smooth(curPump, t.PumpPercent);
        curRad = curRad < 0 || panic ? t.RadiatorPercent : CoolerLogic.Smooth(curRad, t.RadiatorPercent);
        curEmb = curEmb < 0 || panic ? t.EmbeddedPercent : CoolerLogic.Smooth(curEmb, t.EmbeddedPercent);
        var applied = t with { PumpPercent = curPump, RadiatorPercent = curRad, EmbeddedPercent = curEmb };
        int duty = RyujinCurves.RadiatorDuty(curRad);
        bool refresh = Environment.TickCount64 - lastFanWrite > 10000;
        if (refresh || sentPump != curPump || sentEmb != curEmb)
        {
            if (hid.SendFan(RyujinProtocol.BuildPump(curPump, curEmb)) != false) { sentPump = curPump; sentEmb = curEmb; lastFanWrite = Environment.TickCount64; }
        }
        if (refresh || sentRadDuty != duty)
        {
            if (hid.SendFan(RyujinProtocol.BuildRadiator((byte)duty)) != false) { sentRadDuty = duty; lastFanWrite = Environment.TickCount64; }
        }
        PublishFans(new(true, true, applied, src, t.Reason, null));
    }

    private void PublishFans(CoolerStatus s) { fanStatus = s; FanChanged?.Invoke(s); }

    /// <summary>Resumes control after another-writer warning (the user closed the other program).</summary>
    public void ResumeControl() { foreignHits = 0; lastFanWrite = 0; sentRadDuty = null; wake.Set(); }

    /// <summary>
    /// Radiator Auto Tuning like Armoury Crate: 0..100 % in 10 % steps, ~5.5 s each, average rpm of the 3 fans.
    /// Pump untouched. Aborts (and restores) if the water goes above 45 °C. Saves the table to ryujin-fans.json.
    /// </summary>
    public Task<IReadOnlyList<RpmPoint>?> TuneRadiatorAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (!hid.IsOpen || tuning) return (IReadOnlyList<RpmPoint>?)null;
        tuning = true;
        var before = Latest?.Fans?.DutyRaw;
        var table = new List<RpmPoint>();
        string? abort = null;
        try
        {
            for (int pct = 0; pct <= 100; pct += 10)
            {
                ct.ThrowIfCancellationRequested();
                int step = pct / 10 + 1;
                PublishFans(fanStatus with { Tuning = $"radiator at {pct}% · step {step}/11", TuneProgress = (step - 1) / 11.0, TuneSecondsLeft = (int)Math.Ceiling((12 - step) * 5.5) });
                hid.SendFan(RyujinProtocol.BuildRadiator(RyujinCurves.RadiatorDuty(pct)));
                ct.WaitHandle.WaitOne(5500);
                ct.ThrowIfCancellationRequested();
                var p = hid.ReadPump();
                if (p is { WaterTempC: > 45 }) { abort = $"water {p.WaterTempC:0.0} °C"; break; }
                var f = hid.ReadFans();
                if (f == null) { abort = "no radiator reading"; break; }
                table.Add(new RpmPoint(pct, (int)Math.Round(f.Rpm.Average() / 10.0) * 10));
            }
        }
        catch (OperationCanceledException) { abort = "canceled"; }
        finally
        {
            hid.SendFan(RyujinProtocol.BuildRadiator((byte)(sentRadDuty ?? before ?? RyujinCurves.RadiatorDuty(60))));
            lastFanWrite = Environment.TickCount64;
            tuning = false;
        }
        if (abort != null || table.Count != 11) { PublishFans(fanStatus with { Tuning = null, TuneProgress = null, TuneSecondsLeft = null, Notice = "auto tuning aborted: " + (abort ?? "incomplete") }); return null; }
        SaveFans(FanConfig with { RadiatorTable = table, RadiatorTunedAt = DateTimeOffset.Now });
        PublishFans(fanStatus with { Tuning = null, TuneProgress = null, TuneSecondsLeft = null, Notice = null });
        return (IReadOnlyList<RpmPoint>?)table;
    }, CancellationToken.None);

    public bool Tuning => tuning;

    /// <summary>Exit: the cooler freezes at the last value, so leave a safe value (only if Marz was in control).</summary>
    public void ReleaseFans()
    {
        if (!FanConfig.Enabled || !hid.IsOpen || foreignHits >= 3) return;
        hid.SendFan(RyujinProtocol.BuildPump(Math.Max(CoolerSafety.ExitPumpPercent, curPump), Math.Max(CoolerSafety.ExitEmbeddedPercent, curEmb)));
        hid.SendFan(RyujinProtocol.BuildRadiator(RyujinCurves.RadiatorDuty(Math.Max(CoolerSafety.ExitRadiatorPercent, curRad))));
    }

    /// <summary>Value of a sensor: the cooler's come from the last reading; the others from the app's provider.</summary>
    public double? Value(string id)
    {
        var s = Latest;
        bool fresh = s != null && DateTimeOffset.Now - s.At < TimeSpan.FromSeconds(5);
        if (id.StartsWith(RyujinSensorIds.Device + "/", StringComparison.Ordinal))
        {
            if (!fresh) return null;
            if (id == RyujinSensorIds.Water) return s!.Pump?.WaterTempC;
            if (id == RyujinSensorIds.Pump) return s!.Pump?.PumpRpm;
            if (id == RyujinSensorIds.Embedded) return s!.Pump?.EmbeddedFanRpm;
            if (id == RyujinSensorIds.RadiatorDuty) return s!.Fans?.DutyPercent;
            if (id == RyujinSensorIds.PumpDuty) return s!.PumpPercent;
            if (id == RyujinSensorIds.EmbeddedDuty) return s!.EmbeddedPercent;
            for (int i = 0; i < 3; i++) if (id == RyujinSensorIds.RadiatorFan(i)) return s!.Fans?.Rpm.ElementAtOrDefault(i);
            return null;
        }
        return values(id);
    }

    private void Draw(long now)
    {
        LcdConfig c; int p;
        lock (gate)
        {
            c = config;
            if (!c.Enabled || c.Pages.Count == 0) { Publish(null); return; }
            if (pageStarted == 0) pageStarted = now;
            if (now - pageStarted >= c.Pages[Math.Clamp(page, 0, c.Pages.Count - 1)].DurationMs) { page = (page + 1) % c.Pages.Count; pageStarted = now; }
            p = page = Math.Clamp(page, 0, c.Pages.Count - 1);
        }
        if (!hid.IsOpen) { Publish("cooler not found"); return; }
        var pg = c.Pages[p];
        var lines = new List<string>();
        string? error = null;
        for (int i = 0; i < pg.Slots.Count; i++)
        {
            var sl = pg.Slots[i];
            var text = LcdComposer.Value(sl.Group, Value(sl.SensorId));
            lines.Add($"{sl.Name} {text}");
            if (hid.SendDisplay(RyujinProtocol.BuildText(i, sl.Name, text)) == false) error = "failed to write to the LCD";
        }
        // Like Armoury Crate: texts first; the layout only when the count or the 1st slot's group changes.
        var key = $"{pg.Slots.Count}:{pg.Slots[0].Group}";
        if (key != layoutKey)
        {
            if (hid.SendDisplay(RyujinProtocol.BuildLayoutDark(pg.Slots.Count, pg.Slots[0].Group)) == false) error = "failed to write to the LCD";
            else layoutKey = key;
        }
        status = new LcdStatus(true, true, p, c.Pages.Count, error, DateTimeOffset.Now, lines);
        Changed?.Invoke(status);
    }

    private void Publish(string? error)
    {
        var c = Config;
        var s = new LcdStatus(c.Enabled, hid.IsOpen, page, c.Pages.Count, error, status.SentAt, c.Enabled ? status.Lines : []);
        if (s.Enabled == status.Enabled && s.DeviceFound == status.DeviceFound && s.Error == status.Error && s.PageCount == status.PageCount) { status = s; return; }
        status = s;
        Changed?.Invoke(s);
    }

    public void Next() { lock (gate) { var n = config.Pages.Count; if (n > 0) { page = (page + 1) % n; pageStarted = Environment.TickCount64; } } wake.Set(); }

    public void Dispose()
    {
        stop = true; wake.Set();
        thread?.Join(3000);
        try { ReleaseFans(); } catch (Exception) { }
        hid.Dispose();
    }
}
