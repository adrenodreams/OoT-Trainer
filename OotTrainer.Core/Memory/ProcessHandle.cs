using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OotTrainer.Core.Native;

namespace OotTrainer.Core.Memory;

/// <summary>
/// Owns a read/write handle to the game process and exposes bounds-checked typed access.
/// All addresses here are HOST (trainer-visible) addresses; see <see cref="GuestMap"/> for guest translation.
/// </summary>
public sealed class ProcessHandle : IDisposable
{
    private nint _handle;

    public int Pid { get; }
    public string Name { get; }

    private ProcessHandle(nint handle, int pid, string name)
    {
        _handle = handle;
        Pid = pid;
        Name = name;
    }

    /// <summary>Attach to the first running process with the given name (without ".exe").</summary>
    public static ProcessHandle Attach(string processName = "hookshot")
    {
        // Prefer the process with the largest working set: if a launcher and the runner share a name, the game is the big one.
        var proc = Process.GetProcessesByName(processName).OrderByDescending(p => p.WorkingSet64).FirstOrDefault()
                   ?? throw new InvalidOperationException($"Process '{processName}' is not running. Start the game first.");
        return Attach(proc.Id, proc.ProcessName);
    }

    public static ProcessHandle Attach(int pid, string name = "")
    {
        const NativeMethods.ProcessAccess access =
            NativeMethods.ProcessAccess.VmRead | NativeMethods.ProcessAccess.VmWrite |
            NativeMethods.ProcessAccess.VmOperation | NativeMethods.ProcessAccess.QueryInformation;

        nint h = NativeMethods.OpenProcess(access, false, pid);
        if (h == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"OpenProcess({pid}) failed. If access is denied, run the trainer elevated if the game is.");

        if (NativeMethods.IsWow64Process(h, out bool wow) && wow)
        {
            NativeMethods.CloseHandle(h);
            throw new NotSupportedException("32-bit target not supported.");
        }
        return new ProcessHandle(h, pid, name);
    }

    public bool IsAlive
    {
        get
        {
            try { return !Process.GetProcessById(Pid).HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

    /// <summary>Enumerate committed, readable regions in address order.</summary>
    public IEnumerable<MemoryRegion> EnumerateRegions(bool writableOnly = false)
    {
        nuint addr = 0;
        int sz = Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();
        while (NativeMethods.VirtualQueryEx(_handle, addr, out var mbi, (nuint)sz) != 0)
        {
            if (mbi.State == NativeMethods.MemCommit)
            {
                var r = new MemoryRegion(mbi.BaseAddress, mbi.RegionSize, mbi.Protect, mbi.Type);
                if (r.IsReadable && (!writableOnly || r.IsWritable))
                    yield return r;
            }
            ulong next = (ulong)mbi.BaseAddress + mbi.RegionSize;
            if (next <= addr || next > 0x7FFF_FFFF_FFFFUL) yield break;
            addr = (nuint)next;
        }
    }

    public bool TryRead(ulong address, Span<byte> destination)
    {
        if (destination.Length == 0) return true;
        return NativeMethods.ReadProcessMemoryRef(_handle, (nuint)address,
                   ref MemoryMarshal.GetReference(destination), (nuint)destination.Length, out var read) &&
               read == (nuint)destination.Length;
    }

    public byte[]? ReadBytes(ulong address, int length)
    {
        var buf = new byte[length];
        return NativeMethods.ReadProcessMemory(_handle, (nuint)address, buf, (nuint)length, out var read) &&
               read == (nuint)length ? buf : null;
    }

    public bool TryRead<T>(ulong address, out T value) where T : unmanaged
    {
        value = default;
        var buf = ReadBytes(address, Marshal.SizeOf<T>());
        if (buf is null) return false;
        value = MemoryMarshal.Read<T>(buf);
        return true;
    }

    public bool TryWrite<T>(ulong address, T value) where T : unmanaged
    {
        var buf = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(buf, in value);
        return TryWriteBytes(address, buf);
    }

    public bool TryWriteBytes(ulong address, byte[] data) =>
        NativeMethods.WriteProcessMemory(_handle, (nuint)address, data, (nuint)data.Length, out var w) &&
        w == (nuint)data.Length;

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = 0;
        }
    }
}
