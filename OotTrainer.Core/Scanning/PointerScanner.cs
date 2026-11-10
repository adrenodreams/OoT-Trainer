using System.Buffers.Binary;
using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Scanning;

public readonly record struct PointerRef(ulong Slot, ulong Value, ulong Offset)
{
    public bool IsStatic => GuestMap.IsStaticAddress(Slot);
}

/// <summary>
/// Level-by-level pointer search. Given a target address, finds 8-byte slots whose value lies in
/// [target - maxOffset, target], i.e. "slot -> object, target = object + offset".
/// Repeat on a slot's own address until a slot lands in the static image range; that is a stable chain base.
/// </summary>
public static class PointerScanner
{
    public static List<PointerRef> FindReferences(GuestMap map, ulong target, ulong maxOffset = 0x1000, int maxResults = 5000)
    {
        ulong lo = target >= maxOffset ? target - maxOffset : 0;
        var plan = Chunks.Plan(map.GuestRegions(writableOnly: true), 7).ToList();
        var refs = new List<PointerRef>();

        Parallel.ForEach(plan,
            () => new byte[Chunks.Size + 8],
            (chunk, state, buf) =>
            {
                // Collect well past the cap so static (image) slots are not crowded out by heap noise before sorting.
                if (refs.Count >= maxResults * 20) { state.Stop(); return buf; }
                if (!map.Process.TryRead(chunk.Host, buf.AsSpan(0, chunk.Length))) return buf;

                ulong guestStart = map.ToGuest(chunk.Host);
                int first = (int)((8 - (long)(guestStart % 8)) % 8);
                for (int i = first; i < chunk.Owned && i + 8 <= chunk.Length; i += 8)
                {
                    ulong v = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i, 8));
                    if (v < lo || v > target) continue;
                    lock (refs) refs.Add(new PointerRef(guestStart + (ulong)i, v, target - v));
                }
                return buf;
            },
            _ => { });

        // Static slots first, then smallest offset: those make the most useful chain bases.
        return refs.OrderByDescending(r => r.IsStatic).ThenBy(r => r.Offset).ThenBy(r => r.Slot).Take(maxResults).ToList();
    }
}
