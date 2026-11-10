using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Scanning;

internal static class Chunks
{
    public const int Size = 4 << 20;

    /// <summary>Split regions into (hostAddress, length) pieces; each piece reads <paramref name="overlap"/> extra bytes so matches spanning a boundary are found once.</summary>
    public static IEnumerable<(ulong Host, int Length, int Owned)> Plan(IEnumerable<MemoryRegion> regions, int overlap)
    {
        foreach (var r in regions)
        {
            for (ulong off = 0; off < r.Size; off += Size)
            {
                int owned = (int)Math.Min((ulong)Size, r.Size - off);
                int len = (int)Math.Min((ulong)owned + (ulong)overlap, r.Size - off);
                yield return (r.Base + off, len, owned);
            }
        }
    }
}
