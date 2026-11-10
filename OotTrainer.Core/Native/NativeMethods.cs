using System.Runtime.InteropServices;

namespace OotTrainer.Core.Native;

internal static class NativeMethods
{
    [Flags]
    public enum ProcessAccess : uint
    {
        VmRead = 0x0010,
        VmWrite = 0x0020,
        VmOperation = 0x0008,
        QueryInformation = 0x0400,
    }

    public const uint MemCommit = 0x1000;
    public const uint PageNoAccess = 0x01;
    public const uint PageGuard = 0x100;
    public const uint PageReadWrite = 0x04;
    public const uint PageWriteCopy = 0x08;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint PageExecuteWriteCopy = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(ProcessAccess access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(nint process, nuint address, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadProcessMemory")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemoryRef(nint process, nuint address, ref byte buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteProcessMemory(nint process, nuint address, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nuint VirtualQueryEx(nint process, nuint address, out MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWow64Process(nint process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
