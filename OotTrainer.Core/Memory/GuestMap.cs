namespace OotTrainer.Core.Memory;

/// <summary>
/// Translates guest (Switch aarch64) addresses to host addresses inside hookshot.exe:
/// host = HostBase + guest. The hookshot log reports the guest image at 0x80000000 and the guest heap at 0x1000000000.
/// HostBase is discovered by locating the known image header in host memory.
/// </summary>
public sealed class GuestMap
{
    public const ulong GuestImageBase = 0x8000_0000UL;
    public const ulong GuestHeapBase = 0x10_0000_0000UL;

    public ProcessHandle Process { get; }
    public ulong HostBase { get; }

    private GuestMap(ProcessHandle process, ulong hostBase)
    {
        Process = process;
        HostBase = hostBase;
    }

    /// <summary>Build a map with a known base. Used by the self-test, which runs against the trainer's own memory, not the game.</summary>
    public static GuestMap ForSelfTest(ProcessHandle process, ulong hostBase) => new(process, hostBase);

    /// <summary>Approximate image size from the hookshot log (96 MB at 0x80000000); addresses here are static.</summary>
    public const ulong GuestImageSize = 0x0602_0000UL;

    /// <summary>Exclusive upper bound we treat as valid guest user memory.</summary>
    public const ulong GuestLimit = 0x40_0000_0000UL;

    public static bool IsValidGuestPointer(ulong p) => p >= GuestImageBase && p < GuestLimit;

    public static bool IsStaticAddress(ulong guest) => guest >= GuestImageBase && guest < GuestImageBase + GuestImageSize;

    /// <summary>Readable (optionally writable-only) host regions clamped to the guest address window.</summary>
    public IEnumerable<MemoryRegion> GuestRegions(bool writableOnly)
    {
        ulong lo = HostBase + GuestImageBase, hi = HostBase + GuestLimit;
        foreach (var r in Process.EnumerateRegions(writableOnly))
        {
            if (r.End <= lo || r.Base >= hi) continue;
            ulong b = Math.Max(r.Base, lo), e = Math.Min(r.End, hi);
            yield return r with { Base = b, Size = e - b };
        }
    }

    public ulong ToHost(ulong guest) => HostBase + guest;

    public ulong ToGuest(ulong host) => host - HostBase;

    public bool TryReadGuest<T>(ulong guest, out T value) where T : unmanaged => Process.TryRead(ToHost(guest), out value);

    public bool TryWriteGuest<T>(ulong guest, T value) where T : unmanaged => Process.TryWrite(ToHost(guest), value);

    public byte[]? ReadGuestBytes(ulong guest, int length) => Process.ReadBytes(ToHost(guest), length);

    /// <summary>
    /// Find HostBase by searching for <paramref name="signature"/> (the first bytes of image.bin, which the log says
    /// is mapped at guest 0x80000000). Candidate hosts are validated by checking that the guest heap start is readable.
    /// </summary>
    public static GuestMap Locate(ProcessHandle process, byte[] signature, Action<string>? log = null)
    {
        if (signature.Length < 16) throw new ArgumentException("Signature too short to be reliable.", nameof(signature));

        var buf = new byte[signature.Length];
        const ulong MinRegion = 16UL << 20; // guest image is ~96 MB; ignore tiny regions
        var hits = new List<ulong>();

        foreach (var region in process.EnumerateRegions().Where(r => r.Size >= MinRegion))
        {
            // Fast path: the image starts at the region base. Slow path: probe every 1 MB inside the region
            // in case adjacent guest mappings were merged into one host region.
            for (ulong off = 0; off < region.Size; off += 1UL << 20)
            {
                ulong host = region.Base + off;
                if (!process.TryRead(host, buf) || !buf.AsSpan().SequenceEqual(signature)) continue;
                if (host < GuestImageBase) continue;
                ulong hostBase = host - GuestImageBase;
                if (process.TryRead(hostBase + GuestHeapBase, out byte _))
                {
                    log?.Invoke($"candidate image @0x{host:X} => HostBase 0x{hostBase:X}");
                    hits.Add(hostBase);
                }
            }
        }

        var distinct = hits.Distinct().ToList();
        return distinct.Count switch
        {
            0 => throw new InvalidOperationException(
                "Guest image signature not found. Is the game fully booted, and does image.bin match this build?"),
            1 => new GuestMap(process, distinct[0]),
            _ => throw new InvalidOperationException(
                "Multiple host bases matched: " + string.Join(", ", distinct.Select(h => $"0x{h:X}"))),
        };
    }
}
