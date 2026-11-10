using System.Runtime.InteropServices;
using OotTrainer.Core.Config;
using OotTrainer.Core.Features;
using OotTrainer.Core.Memory;

namespace OotTrainer.Gui;

/// <summary>Drives <see cref="MenuActions"/> against a synthetic "game" in this process's memory. Proves the menu logic, not real offsets.</summary>
public static class GuiSelfTest
{
    private const ulong G = 0x9000_0000UL;

    public static IReadOnlyList<(string Name, bool Pass, string Detail)> Run()
    {
        var results = new List<(string, bool, string)>();
        void Check(string name, bool pass, string detail = "") => results.Add((name, pass, detail));

        var mem = new byte[256];
        var pin = GCHandle.Alloc(mem, GCHandleType.Pinned);
        try
        {
            using var proc = ProcessHandle.Attach(Environment.ProcessId, "guiselftest");
            var map = GuestMap.ForSelfTest(proc, unchecked((ulong)pin.AddrOfPinnedObject() - G));
            OffsetEntry E(string n, ValueKind k, ulong off) => new() { Name = n, Kind = k, Chain = $"0x{G + off:X}" };
            T R<T>(ulong off) where T : unmanaged { map.TryReadGuest<T>(G + off, out var v); return v; }

            var file = new OffsetFile { Entries = [E("rupees", ValueKind.Int16, 0x00), E("item_hookshot", ValueKind.Byte, 0x08), E("sword", ValueKind.Byte, 0x10)] };
            var ctx = new FeatureContext(map, file);
            var inv = new InventoryFeature(ctx);
            var actions = new MenuActions(ctx, inv);

            actions.MaxRupees();
            Check("Max Rupees writes fallback 999", R<short>(0x00) == 999, $"rupees={R<short>(0)}");

            file.Entries.Add(E("rupees_max", ValueKind.Int16, 0x04));
            map.TryWriteGuest<short>(G + 0x04, 500);
            actions.MaxRupees();
            Check("Max Rupees honours live rupees_max", R<short>(0x00) == 500, $"rupees={R<short>(0)}");

            var hook = new CatalogItem("Hookshot", "item_hookshot", 10);
            Check("item with saved var + value is usable", actions.Blocker(hook) is null);
            Check("Give writes the item value", actions.Give(hook).StartsWith("gave") && R<byte>(0x08) == 10, $"slot={R<byte>(0x08)}");

            var noValue = new CatalogItem("Master", "sword", null);
            Check("item without an ID is blocked", actions.Blocker(noValue) is not null);
            actions.Give(noValue);
            Check("blocked item writes nothing", R<byte>(0x10) == 0, $"sword={R<byte>(0x10)}");

            var missingVar = new CatalogItem("Mirror", "shield", 3);
            Check("item with unsaved variable is blocked", actions.Blocker(missingVar) is { } b && b.Contains("shield"));

            string overflow = actions.Give(new CatalogItem("Bad", "sword", 300)); // 300 does not fit a byte
            Check("out-of-range value reports an error instead of throwing", overflow.StartsWith("error"), overflow);

            var emptyActions = new MenuActions(new FeatureContext(map, new OffsetFile()), inv);
            Check("no rupees variable -> clear message", !emptyActions.HasRupees && emptyActions.MaxRupees().Contains("not saved"));
        }
        finally
        {
            pin.Free();
        }
        return results;
    }
}
