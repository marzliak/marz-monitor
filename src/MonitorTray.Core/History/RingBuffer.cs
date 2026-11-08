namespace MonitorTray.Core.History;

/// <summary>Buffer circular de tamanho fixo; ToArray devolve do mais antigo para o mais novo.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] items;
    private int start, count;
    public RingBuffer(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        items = new T[capacity];
    }
    public int Capacity => items.Length;
    public int Count => count;
    public void Push(T value)
    {
        if (count < items.Length) { items[(start + count) % items.Length] = value; count++; }
        else { items[start] = value; start = (start + 1) % items.Length; }
    }
    public T this[int i] => i < 0 || i >= count ? throw new ArgumentOutOfRangeException(nameof(i)) : items[(start + i) % items.Length];
    public T[] ToArray(int last = int.MaxValue)
    {
        var n = Math.Min(count, Math.Max(0, last));
        var arr = new T[n];
        for (int i = 0; i < n; i++) arr[i] = this[count - n + i];
        return arr;
    }
    public T? Last => count == 0 ? default : this[count - 1];
}
