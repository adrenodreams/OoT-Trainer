using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Scanning;

public enum ScanFilter { Exact, Between, Increased, Decreased, Changed, Unchanged }

public readonly record struct ScanQuery(ScanFilter Filter, double Value = 0, double Value2 = 0, double Tolerance = 0)
{
    public bool Matches(double current, double previous) => Filter switch
    {
        ScanFilter.Exact => Math.Abs(current - Value) <= Tolerance,
        ScanFilter.Between => current >= Math.Min(Value, Value2) && current <= Math.Max(Value, Value2),
        ScanFilter.Increased => current > previous,
        ScanFilter.Decreased => current < previous,
        ScanFilter.Changed => current != previous,
        ScanFilter.Unchanged => current == previous,
        _ => false,
    };
}

public readonly record struct ScanHit(ulong Guest, double Previous, double Current);

/// <summary>
/// Cheat-Engine-style first/next scan over the guest address window. Addresses are guest addresses.
/// A first scan must be Exact or Between (an "unknown initial value" scan would hold billions of hits).
/// </summary>
public sealed class ValueScanner
{
    public const int MaxResults = 10_000_000;

    private readonly GuestMap _map;
    private ulong[] _addrs = [];
    private double[] _values = [];

    public ValueScanner(GuestMap map, ValueKind kind)
    {
        _map = map;
        Kind = kind;
    }

    public ValueKind Kind { get; }
    public int Count => _addrs.Length;
    public bool HasFirstScan { get; private set; }
    public bool Truncated { get; private set; }

    /// <summary>Default tolerance for float exact scans, so "3.5" matches 3.5000002.</summary>
    public static double DefaultTolerance(ValueKind k) => k.IsFloat() ? 1e-3 : 0;

    public void Reset()
    {
        _addrs = [];
        _values = [];
        HasFirstScan = false;
        Truncated = false;
    }

    public void FirstScan(ScanQuery q, bool writableOnly = true)
    {
        if (q.Filter is not (ScanFilter.Exact or ScanFilter.Between))
            throw new ArgumentException("First scan must be Exact or Between.");

        int size = Kind.SizeOf();
        var plan = Chunks.Plan(_map.GuestRegions(writableOnly), size - 1).ToList();
        var results = new List<(ulong Guest, double Value)[]>();
        int total = 0;
        bool truncated = false;

        Parallel.ForEach(plan,
            () => (Buf: new byte[Chunks.Size + 8], Hits: new List<(ulong, double)>()),
            (chunk, _, local) =>
            {
                if (Volatile.Read(ref total) >= MaxResults) { truncated = true; return local; }
                if (!_map.Process.TryRead(chunk.Host, local.Buf.AsSpan(0, chunk.Length))) return local;

                // Align to the value size relative to guest address space so we only test aligned slots.
                ulong guestStart = _map.ToGuest(chunk.Host);
                int first = (int)((size - (long)(guestStart % (ulong)size)) % size);
                for (int i = first; i < chunk.Owned && i + size <= chunk.Length; i += size)
                {
                    double v = ValueKindInfo.Read(local.Buf.AsSpan(i, size), Kind);
                    if (!q.Matches(v, 0)) continue;
                    local.Hits.Add((guestStart + (ulong)i, v));
                }
                return local;
            },
            local =>
            {
                lock (results)
                {
                    if (local.Hits.Count == 0) return;
                    results.Add(local.Hits.ToArray());
                    total += local.Hits.Count;
                }
            });

        var merged = results.SelectMany(r => r).OrderBy(h => h.Guest).Take(MaxResults).ToArray();
        Truncated = truncated || total > MaxResults;
        _addrs = merged.Select(h => h.Guest).ToArray();
        _values = merged.Select(h => h.Value).ToArray();
        HasFirstScan = true;
    }

    /// <summary>Re-read every remaining candidate and keep those that satisfy the query.</summary>
    public void NextScan(ScanQuery q)
    {
        if (!HasFirstScan) throw new InvalidOperationException("Run a first scan before a next scan.");

        int size = Kind.SizeOf();
        const int Window = 64 * 1024;
        var win = new byte[Window];
        ulong winStart = 0;
        int winLen = 0;

        var keepA = new List<ulong>(_addrs.Length / 4 + 16);
        var keepV = new List<double>(_addrs.Length / 4 + 16);

        for (int n = 0; n < _addrs.Length; n++)
        {
            ulong g = _addrs[n];
            if (winLen == 0 || g < winStart || g + (ulong)size > winStart + (ulong)winLen)
            {
                winStart = g;
                if (_map.Process.TryRead(_map.ToHost(g), win)) winLen = Window;
                else if (_map.Process.TryRead(_map.ToHost(g), win.AsSpan(0, size))) winLen = size;
                else { winLen = 0; continue; } // unreadable now (page freed): drop candidate
            }

            double cur = ValueKindInfo.Read(win.AsSpan((int)(g - winStart), size), Kind);
            if (!q.Matches(cur, _values[n])) continue;
            keepA.Add(g);
            keepV.Add(cur);
        }

        _addrs = keepA.ToArray();
        _values = keepV.ToArray();
    }

    /// <summary>Current live values for a slice of results (does not alter the candidate set).</summary>
    public IEnumerable<ScanHit> Results(int skip = 0, int take = 50)
    {
        int size = Kind.SizeOf();
        for (int n = skip; n < Math.Min(_addrs.Length, skip + take); n++)
        {
            var b = _map.ReadGuestBytes(_addrs[n], size);
            double cur = b is null ? double.NaN : ValueKindInfo.Read(b, Kind);
            yield return new ScanHit(_addrs[n], _values[n], cur);
        }
    }

    public ulong AddressAt(int index) => _addrs[index];
}
