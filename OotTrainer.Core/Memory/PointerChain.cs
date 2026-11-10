using System.Globalization;

namespace OotTrainer.Core.Memory;

/// <summary>
/// Multi-level guest pointer chain. <see cref="Base"/> is the guest address of the first pointer slot.
/// For each offset: read a guest pointer at the current address, validate it, then add the offset.
/// Zero offsets means <see cref="Base"/> itself is the target address (no dereference).
/// Text form: "0x82345678 -> 0x10 -> 0x48".
/// </summary>
public sealed record PointerChain(ulong Base, IReadOnlyList<long> Offsets)
{
    public const int MaxDepth = 12;

    /// <summary>Resolve to the final guest address, or null if any hop is unreadable or not a plausible guest pointer.</summary>
    public ulong? Resolve(GuestMap map)
    {
        if (Offsets.Count > MaxDepth) return null;
        ulong cur = Base;
        foreach (long off in Offsets)
        {
            if (!map.TryReadGuest<ulong>(cur, out ulong ptr) || !GuestMap.IsValidGuestPointer(ptr)) return null;
            cur = unchecked((ulong)((long)ptr + off));
            if (!GuestMap.IsValidGuestPointer(cur)) return null;
        }
        return cur;
    }

    public static PointerChain Parse(string text)
    {
        var parts = text.Split("->", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new FormatException("Empty chain.");
        ulong baseAddr = ParseHex(parts[0]);
        var offsets = parts.Skip(1).Select(p => (long)ParseSigned(p)).ToList();
        return new PointerChain(baseAddr, offsets);
    }

    public override string ToString() =>
        "0x" + Base.ToString("X") + string.Concat(Offsets.Select(o => " -> " + (o < 0 ? "-0x" + (-o).ToString("X") : "0x" + o.ToString("X"))));

    private static ulong ParseHex(string s) =>
        ulong.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static long ParseSigned(string s)
    {
        bool neg = s.StartsWith('-');
        long v = (long)ParseHex(neg ? s[1..] : s);
        return neg ? -v : v;
    }
}
