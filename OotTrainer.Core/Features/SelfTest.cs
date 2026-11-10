using System.Diagnostics;
using System.Runtime.InteropServices;
using OotTrainer.Core.Config;
using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Features;

/// <summary>
/// Exercises every feature against a synthetic "game" living in this process's own memory (a pinned buffer exposed
/// through a GuestMap whose guest base is 0x90000000). It never touches the real game, so it proves the logic
/// (floor locks, chains, levitation, fault handling, disable) but NOT that real-game offsets are right.
/// </summary>
public static class SelfTest
{
    private const ulong G = 0x9000_0000UL;

    public static IReadOnlyList<(string Name, bool Pass, string Detail)> Run()
    {
        var results = new List<(string, bool, string)>();
        void Check(string name, bool pass, string detail = "") => results.Add((name, pass, detail));

        var mem = new byte[4096];
        var pin = GCHandle.Alloc(mem, GCHandleType.Pinned);
        try
        {
            ulong addr = (ulong)pin.AddrOfPinnedObject();
            using var proc = ProcessHandle.Attach(Environment.ProcessId, "selftest");
            var map = GuestMap.ForSelfTest(proc, unchecked(addr - G));

            OffsetEntry E(string n, ValueKind k, string chain) => new() { Name = n, Kind = k, Chain = chain };
            string P(ulong off) => $"0x{G + off:X}";
            var file = new OffsetFile
            {
                Entries =
                [
                    E("health", ValueKind.Int32, P(0x00)), E("health_max", ValueKind.Int32, P(0x04)),
                    E("magic", ValueKind.Byte, P(0x08)), E("magic_max", ValueKind.Byte, P(0x09)),
                    E("rupees", ValueKind.Int16, P(0x0C)), E("sword", ValueKind.Byte, P(0x10)),
                    E("item_slot_0", ValueKind.Byte, P(0x14)),
                    E("pos_x", ValueKind.Single, P(0x100) + " -> 0x0"),
                    E("pos_y", ValueKind.Single, P(0x100) + " -> 0x4"),
                    E("pos_z", ValueKind.Single, P(0x100) + " -> 0x8"),
                ],
            };

            // helpers writing the "game" directly, as the game itself would
            void W<T>(ulong off, T v) where T : unmanaged => map.TryWriteGuest(G + off, v);
            T R<T>(ulong off) where T : unmanaged { map.TryReadGuest<T>(G + off, out var v); return v; }
            static bool Wait(Func<bool> cond, int ms = 600)
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(5); }
                return cond();
            }

            W<int>(0x00, 4); W<int>(0x04, 24); W<byte>(0x08, 0); W<byte>(0x09, 96);
            W<short>(0x0C, 5); W<byte>(0x10, 1); W<byte>(0x14, 0xFF);
            W<ulong>(0x100, G + 0x200);
            W(0x200, 10f); W(0x204, 5f); W(0x208, -3f);

            var ctx = new FeatureContext(map, file);
            var overflow = new FloorLockFeature("overflow", ctx, "sword", null, 999); // 999 cannot fit a byte
            using var runner = new FeatureRunner()
                .Add(new HealthFeature(ctx)).Add(new MagicFeature(ctx)).Add(new RupeesFeature(ctx))
                .Add(new InventoryFeature(ctx)).Add(new PositionFeature(ctx)).Add(overflow);
            runner.Start();

            // Health: floor to live health_max, ignore higher values, stop on disable.
            var health = runner.Get("health")!;
            health.Enable();
            Check("health refills to health_max", Wait(() => R<int>(0x00) == 24), $"health={R<int>(0)}");
            W<int>(0x00, 3);
            Check("health refills after damage", Wait(() => R<int>(0x00) == 24), $"health={R<int>(0)}");
            health.Disable();
            W<int>(0x00, 2); Thread.Sleep(80);
            Check("health disable stops writing", R<int>(0x00) == 2, $"health={R<int>(0)}");
            W<int>(0x00, 30); health.Enable(); Thread.Sleep(80);
            Check("floor lock leaves higher values alone", R<int>(0x00) == 30, $"health={R<int>(0)}");
            health.Disable();

            // Magic (byte) and rupees (int16, no rupees_max -> fixed 999)
            runner.Get("magic")!.Enable(); runner.Get("rupees")!.Enable();
            Check("magic fills to magic_max", Wait(() => R<byte>(0x08) == 96), $"magic={R<byte>(8)}");
            Check("rupees held at fixed 999", Wait(() => R<short>(0x0C) == 999), $"rupees={R<short>(0x0C)}");

            // Out-of-range target must fault the feature, not crash the runner.
            overflow.Enable();
            Check("overflow disables feature with error", Wait(() => !overflow.Enabled && overflow.LastError is not null), overflow.LastError ?? "no error");
            Check("runner survives a faulted feature", runner.Get("magic")!.Enabled);

            // Inventory: one-shot set and tick-enforced lock.
            var inv = runner.Get<InventoryFeature>();
            Check("inventory one-shot set", inv.Set("sword", 3) && R<byte>(0x10) == 3, $"sword={R<byte>(0x10)}");
            inv.Lock("item_slot_0", 7); inv.Enable();
            Check("inventory lock applies", Wait(() => R<byte>(0x14) == 7), $"slot={R<byte>(0x14)}");
            W<byte>(0x14, 0xFF);
            Check("inventory lock re-applies", Wait(() => R<byte>(0x14) == 7), $"slot={R<byte>(0x14)}");
            Check("inventory unlock", inv.Unlock("item_slot_0"));
            inv.Disable();

            // Levitation through a pointer chain.
            var pos = runner.Get<PositionFeature>();
            var p0 = pos.Read();
            Check("position read through chain", p0 is { X: 10f, Y: 5f, Z: -3f }, p0?.ToString() ?? "null");
            pos.Enable();
            W(0x204, 1f); // "gravity" pulls Link down
            Check("levitate holds height", Wait(() => Math.Abs(R<float>(0x204) - 5f) < 0.01f), $"y={R<float>(0x204)}");
            pos.Adjust(+2);
            Check("levitate raise", Wait(() => Math.Abs(R<float>(0x204) - 7f) < 0.01f), $"y={R<float>(0x204)}");
            Check("teleport while levitating", pos.Teleport(new Vec3(1, 2, 3)) && Wait(() => R<float>(0x204) == 2f) && R<float>(0x200) == 1f && R<float>(0x208) == 3f,
                $"pos=({R<float>(0x200)},{R<float>(0x204)},{R<float>(0x208)})");
            pos.Disable();
            W(0x204, 0f); Thread.Sleep(80);
            Check("levitate disable lets gravity resume", R<float>(0x204) == 0f, $"y={R<float>(0x204)}");
            pos.Teleport(new Vec3(4, 4, 4)); pos.SavePosition(); pos.Teleport(new Vec3(9, 9, 9));
            Check("save/restore position", pos.RestorePosition() && R<float>(0x200) == 4f, $"x={R<float>(0x200)}");

            // Chain breakage (null pointer, e.g. loading screen): no exception, no stray writes.
            pos.Enable();
            W<ulong>(0x100, 0);
            Thread.Sleep(80);
            Check("null pointer chain is tolerated", pos.Enabled && pos.LastError is null);
            Check("null pointer chain reads as null", pos.Read() is null);
            W<ulong>(0x100, G + 0x200);
            pos.Disable();

            // Missing variables give an actionable error.
            var empty = new HealthFeature(new FeatureContext(map, new OffsetFile()));
            string? msg = null;
            try { empty.Enable(); } catch (MissingVariableException ex) { msg = ex.Message; }
            Check("missing variable raises helpful error", msg is not null && msg.Contains("health"), msg ?? "no exception");
            Check("failed enable leaves feature off", !empty.Enabled);
        }
        finally
        {
            pin.Free();
        }
        return results;
    }
}
