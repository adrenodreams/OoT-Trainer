using System.Runtime.InteropServices;
using OotTrainer.Core.Memory;

namespace OotTrainer.Panel;

/// <summary>
/// A fake item table in this process's own memory (pinned buffer behind a GuestMap), laid out like the real one. Used by
/// demo mode and the self-test so the panel logic can be exercised without the game. It proves the panel, not the real offsets.
/// </summary>
public sealed class SyntheticGame : IDisposable
{
    public const ulong G = 0x9000_0000UL;
    private const int Before = 9, Entries = 116; // entry -9 (rupees) .. entry 115

    private const int Pad = 0x1600; // room before the table for lone ints (Epona's carrots sit 0x14D4 before entry 0)

    private readonly byte[] _mem = new byte[Pad + (Before + Entries + 2) * (int)ItemLayout.Stride];
    private readonly GCHandle _pin;
    private readonly ProcessHandle _proc;

    public GuestMap Map { get; }
    public ItemTable Table { get; }

    public SyntheticGame()
    {
        _pin = GCHandle.Alloc(_mem, GCHandleType.Pinned);
        ulong addr = (ulong)_pin.AddrOfPinnedObject();
        _proc = ProcessHandle.Attach(Environment.ProcessId, "synthetic");
        Map = GuestMap.ForSelfTest(_proc, unchecked(addr - G));
        Table = new ItemTable(Map, G + Pad + (ulong)Before * ItemLayout.Stride);

        // a fake Epona actor: counter byte, then the bytes EponaCarrots looks for around it
        byte[] actor = [0, 0, 0xFF, 0, 0, 0, 0x80, 0, 0, 0, 0, 0, 0x0A, 0, 0, 0, 0x0D, 0, 0, 0];
        for (int i = 0; i < actor.Length; i++) Map.TryWriteGuest(RealCarrotAddress + (ulong)i, actor[i]);

        // marker on every entry; entries 0.. also carry "+8 = 0x20, +0xC = 1" like the real ones (the rupees entry does not)
        for (int k = -Before; k < Entries; k++)
        {
            ulong a = Table.EntryAddress(k);
            Map.TryWriteGuest(a, (ulong)ItemLayout.MarkerDword);
            if (k >= 0) { Map.TryWriteGuest(a + 8, 0x20); Map.TryWriteGuest(a + 0xC, 1); }
        }
    }

    /// <summary>Where the fake Epona actor's carrot byte sits (inside the padding before the table).</summary>
    public const ulong RealCarrotAddress = G + 0x400;

    /// <summary>Epona's real carrot counter. Writing it also moves the table's copy, like the game's refresh does (instantly here).</summary>
    public int RealCarrots
    {
        get { Map.TryReadGuest(RealCarrotAddress, out ushort v); return v & 0xFF; }
        set { Map.TryWriteGuest(RealCarrotAddress, (ushort)value); Table.TryWriteValue(ItemLayout.All.First(d => d.Key == "carrots"), value); }
    }

    /// <summary>Set an entry the way the game itself would (count and duplicate).</summary>
    public void GameSets(string key, int value) => Table.TryWriteValue(ItemLayout.All.First(d => d.Key == key), value);

    public int Read(string key)
    {
        Table.TryReadValue(ItemLayout.All.First(d => d.Key == key), out int v);
        return v;
    }

    public void SeedDemoValues()
    {
        GameSets("rupees", 523); GameSets("bombs", 12); GameSets("bombchu", 7); GameSets("nuts", 20); GameSets("seeds", 40);
        GameSets("sticks", 5); GameSets("scrub", 3); GameSets("beans", 2); GameSets("skulltula", 6); RealCarrots = 3;
        GameSets("health", 16); GameSets("max_health", 24); GameSets("magic", 120); GameSets("max_magic", 240);
        GameSets("bomb_limit", 40); GameSets("nut_limit", 30); GameSets("seed_limit", 50); GameSets("stick_limit", 20); GameSets("wallet", 500);
    }

    public void Dispose()
    {
        _proc.Dispose();
        _pin.Free();
    }
}
