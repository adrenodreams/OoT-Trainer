using OotTrainer.Core.Native;

namespace OotTrainer.Core.Memory;

/// <summary>A committed virtual-memory region in the target process.</summary>
public readonly record struct MemoryRegion(ulong Base, ulong Size, uint Protect, uint Type)
{
    public ulong End => Base + Size;

    public bool IsReadable =>
        (Protect & NativeMethods.PageGuard) == 0 && Protect != NativeMethods.PageNoAccess && Protect != 0;

    public bool IsWritable =>
        (Protect & (NativeMethods.PageReadWrite | NativeMethods.PageWriteCopy |
                    NativeMethods.PageExecuteReadWrite | NativeMethods.PageExecuteWriteCopy)) != 0;

    public override string ToString() => $"0x{Base:X}-0x{End:X} ({Size >> 20} MB, prot 0x{Protect:X})";
}
