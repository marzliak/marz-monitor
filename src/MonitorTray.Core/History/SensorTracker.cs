using MonitorTray.Core.Model;

namespace MonitorTray.Core.History;

/// <summary>Per-sensor state over the session: history, quality window, allZero, observed min/max.</summary>
public sealed class SensorTracker
{
    public const int HistoryLength = 120;
    private const int QualityWindow = 10;

    public sealed class Entry
    {
        internal Entry() { }
        public RingBuffer<float?> History { get; } = new(HistoryLength);
        public RingBuffer<bool> Nulls { get; } = new(QualityWindow);
        public bool AllZero { get; internal set; } = true;
        public bool SeenValue { get; internal set; }
        public float? Min { get; internal set; }
        public float? Max { get; internal set; }
        public long LastTick { get; internal set; }
        public SensorQuality Quality
        {
            get
            {
                int nulls = 0;
                for (int i = 0; i < Nulls.Count; i++) if (Nulls[i]) nulls++;
                return nulls == Nulls.Count ? SensorQuality.Unavailable : nulls > 0 ? SensorQuality.Partial : SensorQuality.Ok;
            }
        }
    }

    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long tick;

    public long Tick => tick;

    public void BeginTick() => tick++;

    public Entry Push(string id, float? value)
    {
        if (!entries.TryGetValue(id, out var e))
        {
            e = new Entry();
            entries[id] = e;
            // Sensor that appeared later: pre-fill with gaps so no history is invented.
            for (long i = Math.Max(0, tick - HistoryLength); i < tick - 1; i++) e.History.Push(null);
        }
        if (value is float f && (float.IsNaN(f) || float.IsInfinity(f))) value = null;
        e.History.Push(value);
        e.Nulls.Push(value is null);
        if (value is float v)
        {
            e.SeenValue = true;
            if (v != 0) e.AllZero = false;
            e.Min = e.Min is float mn ? Math.Min(mn, v) : v;
            e.Max = e.Max is float mx ? Math.Max(mx, v) : v;
        }
        e.LastTick = tick;
        return e;
    }

    public Entry? Get(string id) => entries.TryGetValue(id, out var e) ? e : null;

    /// <summary>Removes sensors not seen for more than N ticks (hardware removed).</summary>
    public void Prune(int staleTicks = 5)
    {
        foreach (var k in entries.Where(kv => tick - kv.Value.LastTick > staleTicks).Select(kv => kv.Key).ToList()) entries.Remove(k);
    }

    public int Count => entries.Count;
}
