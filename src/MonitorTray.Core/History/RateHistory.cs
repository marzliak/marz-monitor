namespace MonitorTray.Core.History;

/// <summary>
/// Histórico de taxa (rede) por segundo, até 2 h. Cada segundo guarda o pico dos ticks daquele segundo.
/// View agrupa em baldes alinhados ao relógio (o gráfico só rola quando fecha um balde) usando o pico de cada balde,
/// para picos curtos não sumirem em janelas longas. ~115 KB por série.
/// </summary>
public sealed class RateHistory
{
    public const int MaxSeconds = 7200;
    public const int MaxPoints = 120;
    private readonly RingBuffer<double> secs = new(MaxSeconds + 1);
    private long cur = -1;
    private double curMax;

    public void Add(double value, long second)
    {
        if (!double.IsFinite(value) || value < 0) value = 0;
        if (cur < 0) { cur = second; curMax = value; return; }
        if (second < cur) return; // fora de ordem: ignora (nunca apaga o histórico)
        if (second == cur) { curMax = Math.Max(curMax, value); return; }
        secs.Push(curMax);
        for (long s = cur + 1; s < second && s - cur <= MaxSeconds; s++) secs.Push(0); // lacuna (suspensão): sem tráfego
        cur = second; curMax = value;
    }

    /// <summary>Segundos por ponto para a janela (1 s até 120 s; depois janela/120 arredondado para cima).</summary>
    public static int BucketSeconds(int window) => Math.Max(1, (int)Math.Ceiling(window / (double)MaxPoints));
    public static int Points(int window) => Math.Max(2, window / BucketSeconds(window));

    private bool TryGet(long s, out double v)
    {
        if (s == cur) { v = curMax; return true; }
        long back = cur - s; // 1 = último segundo fechado
        if (back >= 1 && back <= secs.Count) { v = secs[secs.Count - (int)back]; return true; }
        v = 0; return false;
    }

    /// <summary>Até Points+1 valores (o mais antigo fica fora da área, para a rolagem não abrir buraco); o último é o balde em andamento.</summary>
    public double[] View(int window)
    {
        if (cur < 0) return [];
        int b = BucketSeconds(window), n = Points(window);
        long last = cur / b;
        var list = new List<double>(n + 1);
        for (long k = last - n; k <= last; k++)
        {
            bool any = false; double m = 0;
            for (long s = k * b; s < (k + 1) * b; s++)
                if (TryGet(s, out var v)) { any = true; if (v > m) m = v; }
            if (any) list.Add(m); else if (list.Count > 0) list.Add(0);
        }
        return list.ToArray();
    }
}
