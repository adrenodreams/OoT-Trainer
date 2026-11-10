using OotTrainer.Core.Config;
using OotTrainer.Core.Features;
using OotTrainer.Core.Memory;
using OotTrainer.Gui;

// F1 mod-menu overlay. Usage: OotTrainer.Gui [path-to-image.bin] [path-to-offsets.json]
//                             OotTrainer.Gui --selftest   (menu logic against synthetic memory; game not needed)
// External only: attaches with ReadProcessMemory/WriteProcessMemory like OotTrainer.Cli; nothing is injected into the game.
if (args.Contains("--selftest"))
{
    var results = GuiSelfTest.Run();
    foreach (var (name, pass, detail) in results)
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}{(pass || detail.Length == 0 ? "" : "  [" + detail + "]")}");
    int failed = results.Count(r => !r.Pass);
    Console.WriteLine(failed == 0 ? $"All {results.Count} checks passed." : $"{failed} of {results.Count} checks FAILED.");
    return failed == 0 ? 0 : 3;
}

string imagePath = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "image.bin"));
string offsetsPath = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(Path.GetDirectoryName(imagePath)!, "trainer", "offsets.json"));
string itemsPath = Path.Combine(Path.GetDirectoryName(offsetsPath)!, "items.json");

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"image.bin not found at {imagePath}. Pass its path as the first argument.");
    return 1;
}

var signature = new byte[64];
using (var fs = File.OpenRead(imagePath)) fs.ReadExactly(signature);

try
{
    using var proc = ProcessHandle.Attach("hookshot");
    var map = GuestMap.Locate(proc, signature);
    Console.WriteLine($"Attached to {proc.Name} (pid {proc.Pid}), HostBase = 0x{map.HostBase:X}");

    var file = OffsetStore.Load(offsetsPath);
    if (file.BuildSignature is not null && file.BuildSignature != Convert.ToHexString(signature))
        Console.WriteLine("WARNING: offsets.json was saved for a different build; saved chains may be wrong.");
    Console.WriteLine(File.Exists(offsetsPath) ? $"Offsets: {offsetsPath} ({file.Entries.Count} entries)" : $"No offsets file at {offsetsPath}; buttons stay disabled until variables are saved.");

    var ctx = new FeatureContext(map, file);
    var inventory = new InventoryFeature(ctx);
    var rupees = new RupeesFeature(ctx);
    using var runner = new FeatureRunner().Add(rupees).Add(inventory);
    runner.Start();

    var catalog = ItemCatalog.Load(itemsPath);
    using var menu = new DebugMenu(new MenuActions(ctx, inventory), rupees, catalog, $"{proc.Name} (pid {proc.Pid})");
    Console.WriteLine("Overlay running. Press F2 in the game window to show/hide the menu. Close this console to quit.");
    menu.Start().Wait();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 2;
}
