using OotTrainer.Core.Memory;
using OotTrainer.Core.Scanning;

namespace OotTrainer.Panel;

/// <summary>Typed access to the game's live item table (see <see cref="ItemLayout"/>), anchored at guest address <see cref="Base"/> = entry 0.</summary>
public sealed class ItemTable
{
    public GuestMap Map { get; }
    public ulong Base { get; }

    public ItemTable(GuestMap map, ulong baseGuest)
    {
        Map = map;
        Base = baseGuest;
    }

    public ulong EntryAddress(int index) => unchecked((ulong)((long)Base + index * (long)ItemLayout.Stride));

    /// <summary>True when the entry still starts with the table marker. Checked before every write batch so a moved table is never written blindly.</summary>
    public bool HasMarker(int index) => Map.TryReadGuest<uint>(EntryAddress(index), out var m) && m == ItemLayout.MarkerDword;

    public bool TryRead(int index, out int value) => Map.TryReadGuest(EntryAddress(index) + ItemLayout.CountOffset, out value);

    /// <summary>Both the count and its duplicate; true only if the two reads succeeded.</summary>
    public bool TryReadBoth(int index, out int count, out int copy)
    {
        copy = 0;
        ulong a = EntryAddress(index);
        return Map.TryReadGuest(a + ItemLayout.CountOffset, out count) && Map.TryReadGuest(a + ItemLayout.CopyOffset, out copy);
    }

    /// <summary>Write the count and its duplicate. Refuses (returns false) if the entry has lost its marker.</summary>
    public bool TryWrite(int index, int value)
    {
        if (!HasMarker(index)) return false;
        ulong a = EntryAddress(index);
        bool ok1 = Map.TryWriteGuest(a + ItemLayout.CountOffset, value);
        bool ok2 = Map.TryWriteGuest(a + ItemLayout.CopyOffset, value);
        return ok1 && ok2;
    }

    /// <summary>True when every table entry in <see cref="ItemLayout.All"/> carries the marker (lone ints at a byte offset have none to check).</summary>
    public bool LayoutLooksRight() => ItemLayout.All.Where(d => d.ByteOffset is null).All(d => HasMarker(d.Index));

    private ulong OffsetAddress(ItemDef d) => unchecked((ulong)((long)Base + d.ByteOffset!.Value));

    /// <summary>Current value of <paramref name="d"/>, whether it is a table entry or a lone int.</summary>
    public bool TryReadValue(ItemDef d, out int value) =>
        d.ByteOffset is null ? TryRead(d.Index, out value) : Map.TryReadGuest(OffsetAddress(d), out value);

    /// <summary>Value and its duplicate. A lone int has no duplicate, so both come back equal.</summary>
    public bool TryReadPair(ItemDef d, out int count, out int copy)
    {
        if (d.ByteOffset is null) return TryReadBoth(d.Index, out count, out copy);
        copy = 0;
        bool ok = Map.TryReadGuest(OffsetAddress(d), out count);
        copy = count;
        return ok;
    }

    /// <summary>Write <paramref name="value"/>. A lone int is only written while its current value is in 0..Max, which stops a write to a moved address.</summary>
    public bool TryWriteValue(ItemDef d, int value)
    {
        if (d.ByteOffset is null) return TryWrite(d.Index, value);
        ulong a = OffsetAddress(d);
        if (!Map.TryReadGuest(a, out int current) || current < 0 || current > d.Max * d.Scale) return false;
        return Map.TryWriteGuest(a, value);
    }

    /// <summary>Start (lowest address) of the longest run of marker matches spaced exactly one stride apart, or null if none is long enough.</summary>
    public static ulong? FindRunStart(IReadOnlyList<ulong> hits)
    {
        var set = new HashSet<ulong>(hits);
        ulong? bestStart = null;
        int bestLen = 0;
        foreach (ulong h in hits.OrderBy(x => x))
        {
            if (h >= ItemLayout.Stride && set.Contains(h - ItemLayout.Stride)) continue; // not the start of a run
            int len = 1;
            while (set.Contains(h + (ulong)len * ItemLayout.Stride)) len++;
            if (len > bestLen) { bestLen = len; bestStart = h; }
        }
        return bestLen >= ItemLayout.MinRun ? bestStart : null;
    }

    /// <summary>Scan the game's writable memory for the table. Returns null until a save is loaded and the table exists.</summary>
    public static ItemTable? Find(GuestMap map)
    {
        var hits = PatternScanner.Scan(map, Pattern.Parse(ItemLayout.MarkerText), writableOnly: true, maxResults: 600);
        if (hits.Count < ItemLayout.MinRun) return null;
        var start = FindRunStart(hits);
        if (start is null) return null;
        var table = new ItemTable(map, start.Value);
        return table.LayoutLooksRight() ? table : null;
    }
}
