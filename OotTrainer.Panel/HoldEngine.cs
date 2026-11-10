namespace OotTrainer.Panel;

/// <summary>
/// Keeps chosen entries at a fixed number ("Infinite"). Nothing is written unless <see cref="Active"/> is on, so the
/// panel's Activate button is a real master switch. Safe to call from any thread.
/// </summary>
public sealed class HoldEngine
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _holds = new();

    public bool Active { get; set; }

    /// <summary>Hold <paramref name="key"/> at <paramref name="value"/>, or stop holding it when null.</summary>
    public void Set(string key, int? value)
    {
        lock (_gate)
        {
            if (value is int v) _holds[key] = v; else _holds.Remove(key);
        }
    }

    public int HeldCount { get { lock (_gate) return _holds.Count; } }

    /// <summary>One pass. Returns how many entries had to be corrected.</summary>
    public int Tick(ItemTable? table)
    {
        if (!Active || table is null) return 0;
        KeyValuePair<string, int>[] snapshot;
        lock (_gate) snapshot = _holds.ToArray();

        int fixedCount = 0;
        foreach (var (key, value) in snapshot)
        {
            var def = ItemLayout.All.FirstOrDefault(d => d.Key == key);
            if (def is null || def.ByteOffset is not null) continue; // lone ints (Epona's carrots) are held by EponaCarrots, which is much faster
            int target = value;
            if (def.TopUpTo is not null) // "stay full": follow the live maximum instead of the box number
            {
                var max = ItemLayout.All.FirstOrDefault(d => d.Key == def.TopUpTo);
                if (max is null || !table.TryReadValue(max, out target) || target <= 0) continue;
            }
            if (!table.TryReadPair(def, out int count, out int copy)) continue;
            if (count == target && copy == target) continue;
            if (table.TryWriteValue(def, target)) fixedCount++;
        }
        return fixedCount;
    }
}
