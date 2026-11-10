using System.Diagnostics;
using System.Globalization;
using OotTrainer.Core.Config;
using OotTrainer.Core.Features;
using OotTrainer.Core.Memory;
using OotTrainer.Core.Scanning;

// Trainer console. Usage: OotTrainer.Cli [path-to-image.bin] [path-to-offsets.json]
//                        OotTrainer.Cli --selftest     (runs the feature logic against synthetic memory; game not needed)
if (args.Contains("--selftest"))
{
    var results = SelfTest.Run();
    foreach (var (name, pass, detail) in results)
        Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}{(pass || detail.Length == 0 ? "" : "  [" + detail + "]")}");
    int failed = results.Count(r => !r.Pass);
    Console.WriteLine(failed == 0 ? $"All {results.Count} checks passed." : $"{failed} of {results.Count} checks FAILED.");
    return failed == 0 ? 0 : 3;
}

// --serve <cmdfile> <outfile>: read commands appended to cmdfile, write console output to outfile (lets another
// process drive one long-lived scan session).
string? serveCmd = null, serveOut = null;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--serve" && i + 2 < args.Length) { serveCmd = args[i + 1]; serveOut = args[i + 2]; i += 2; }
    else positional.Add(args[i]);
}

string imagePath = Path.GetFullPath(positional.Count > 0 ? positional[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "image.bin"));
string offsetsPath = Path.GetFullPath(positional.Count > 1 ? positional[1] : Path.Combine(Path.GetDirectoryName(imagePath)!, "trainer", "offsets.json"));

if (!File.Exists(imagePath))
{
    Console.Error.WriteLine($"image.bin not found at {imagePath}. Pass its path as the first argument.");
    return 1;
}

var signature = new byte[64];
using (var fs = File.OpenRead(imagePath)) fs.ReadExactly(signature);
string buildSig = Convert.ToHexString(signature);

Func<string?> readLine = Console.ReadLine;
if (serveCmd is not null)
{
    File.WriteAllText(serveCmd, "");
    Console.SetOut(new StreamWriter(new FileStream(serveOut!, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
    int consumed = 0;
    readLine = () =>
    {
        while (true)
        {
            string[] lines;
            try { lines = File.ReadAllLines(serveCmd); }
            catch (IOException) { Thread.Sleep(50); continue; }
            if (lines.Length > consumed) return lines[consumed++];
            Thread.Sleep(100);
        }
    };
}

try
{
    using var proc = ProcessHandle.Attach("hookshot");
    var map = GuestMap.Locate(proc, signature);
    Console.WriteLine($"Attached to {proc.Name} (pid {proc.Pid}), HostBase = 0x{map.HostBase:X}");
    Console.WriteLine("Type 'help' for commands.");

    var file = OffsetStore.Load(offsetsPath);
    if (file.BuildSignature is not null && file.BuildSignature != buildSig)
        Console.WriteLine("WARNING: offsets.json was saved for a different build; saved chains may be wrong.");
    file.BuildSignature = buildSig;

    var scanner = new ValueScanner(map, ValueKind.Int32);

    var ctx = new FeatureContext(map, file); // shares 'file', so 'save' is visible to features immediately
    using var runner = new FeatureRunner()
        .Add(new HealthFeature(ctx)).Add(new MagicFeature(ctx)).Add(new RupeesFeature(ctx))
        .Add(new InventoryFeature(ctx)).Add(new PositionFeature(ctx));
    runner.Start();
    var position = runner.Get<PositionFeature>();
    var inventory = runner.Get<InventoryFeature>();

    while (true)
    {
        if (!proc.IsAlive) { Console.WriteLine("Game exited."); break; }
        Console.Write("> ");
        string? line = readLine();
        if (line is null) break;
        var t = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0) continue;

        try
        {
            switch (t[0].ToLowerInvariant())
            {
                case "help": PrintHelp(); break;
                case "quit": case "exit": return 0;

                case "scan": // scan <kind> <value> | scan <kind> between <a> <b>
                {
                    Need(t.Length >= 3, "scan <kind> <value>   or   scan <kind> between <a> <b>");
                    if (!ValueKindInfo.TryParseKind(t[1], out var kind)) throw new FormatException($"Unknown kind '{t[1]}'.");
                    scanner = new ValueScanner(map, kind);
                    var q = t[2].Equals("between", StringComparison.OrdinalIgnoreCase)
                        ? new ScanQuery(ScanFilter.Between, Num(t[3]), Num(t[4]))
                        : new ScanQuery(ScanFilter.Exact, Num(t[2]), 0, ValueScanner.DefaultTolerance(kind));
                    Timed(() => scanner.FirstScan(q));
                    Console.WriteLine($"{scanner.Count:N0} candidates{(scanner.Truncated ? " (TRUNCATED at cap; use a narrower value)" : "")}");
                    break;
                }

                case "next": // next <value> | inc | dec | changed | same | between <a> <b>
                {
                    Need(scanner.HasFirstScan && t.Length >= 2, "next <value|inc|dec|changed|same|between a b>");
                    var q = t[1].ToLowerInvariant() switch
                    {
                        "inc" => new ScanQuery(ScanFilter.Increased),
                        "dec" => new ScanQuery(ScanFilter.Decreased),
                        "changed" => new ScanQuery(ScanFilter.Changed),
                        "same" => new ScanQuery(ScanFilter.Unchanged),
                        "between" => new ScanQuery(ScanFilter.Between, Num(t[2]), Num(t[3])),
                        _ => new ScanQuery(ScanFilter.Exact, Num(t[1]), 0, ValueScanner.DefaultTolerance(scanner.Kind)),
                    };
                    Timed(() => scanner.NextScan(q));
                    Console.WriteLine($"{scanner.Count:N0} candidates");
                    if (scanner.Count is > 0 and <= 20) PrintHits(scanner, 0, 20);
                    break;
                }

                case "list":
                {
                    int skip = t.Length > 1 ? int.Parse(t[1]) : 0, take = t.Length > 2 ? int.Parse(t[2]) : 20;
                    PrintHits(scanner, skip, take);
                    break;
                }

                case "reset": scanner.Reset(); Console.WriteLine("Scan cleared."); break;

                case "write": // write <addr|#idx> <value> [kind]
                {
                    Need(t.Length >= 3, "write <0xaddr|#index> <value> [kind]");
                    var kind = t.Length > 3 && ValueKindInfo.TryParseKind(t[3], out var k2) ? k2 : scanner.Kind;
                    ulong addr = Addr(t[1]);
                    bool ok = proc.TryWriteBytes(map.ToHost(addr), ValueKindInfo.Encode(Num(t[2]), kind));
                    Console.WriteLine(ok ? $"wrote {t[2]} ({kind}) to 0x{addr:X}" : "write failed");
                    break;
                }

                case "read": // read <addr|#idx> [kind]
                {
                    Need(t.Length >= 2, "read <0xaddr|#index> [kind]");
                    var kind = t.Length > 2 && ValueKindInfo.TryParseKind(t[2], out var k2) ? k2 : scanner.Kind;
                    ulong addr = Addr(t[1]);
                    var b = map.ReadGuestBytes(addr, kind.SizeOf());
                    Console.WriteLine(b is null ? "unreadable" : $"0x{addr:X} = {ValueKindInfo.Read(b, kind).ToString(CultureInfo.InvariantCulture)} ({kind})");
                    break;
                }

                case "ptr": // ptr <addr|#idx> [maxOffsetHex]
                {
                    Need(t.Length >= 2, "ptr <0xaddr|#index> [maxOffsetHex]");
                    ulong target = Addr(t[1]);
                    ulong maxOff = t.Length > 2 ? Convert.ToUInt64(t[2].Replace("0x", ""), 16) : 0x1000;
                    List<PointerRef> refs = [];
                    Timed(() => refs = PointerScanner.FindReferences(map, target, maxOff, 200));
                    Console.WriteLine($"{refs.Count} pointer(s) to 0x{target:X} (+0..0x{maxOff:X}); [S] = static image slot, a stable chain base");
                    foreach (var r in refs.Take(40))
                        Console.WriteLine($"  {(r.IsStatic ? "[S]" : "   ")} slot 0x{r.Slot:X} -> 0x{r.Value:X}  offset +0x{r.Offset:X}");
                    break;
                }

                case "aob": // aob <pattern...>
                {
                    Need(t.Length >= 2, "aob <hex bytes with ?? wildcards>");
                    var p = Pattern.Parse(string.Join(' ', t.Skip(1)));
                    List<ulong> hits = [];
                    Timed(() => hits = PatternScanner.Scan(map, p));
                    Console.WriteLine($"{hits.Count} match(es)");
                    foreach (var h in hits.Take(30)) Console.WriteLine($"  0x{h:X}");
                    break;
                }

                case "chain": // chain <text> [kind]  e.g. chain 0x82345678 -> 0x10 -> 0x48 i32
                {
                    Need(t.Length >= 2, "chain <0xbase -> 0xoff -> ...> [kind]");
                    var kind = ValueKind.Int32;
                    var parts = t.Skip(1).ToList();
                    if (ValueKindInfo.TryParseKind(parts[^1], out var kk)) { kind = kk; parts.RemoveAt(parts.Count - 1); }
                    var chain = PointerChain.Parse(string.Join(' ', parts));
                    var addr = chain.Resolve(map);
                    if (addr is null) { Console.WriteLine("chain does not resolve"); break; }
                    var b = map.ReadGuestBytes(addr.Value, kind.SizeOf());
                    Console.WriteLine($"-> 0x{addr:X} = {(b is null ? "unreadable" : ValueKindInfo.Read(b, kind).ToString(CultureInfo.InvariantCulture))} ({kind})");
                    break;
                }

                case "save": // save <name> <kind> <chain text>
                {
                    Need(t.Length >= 4 && ValueKindInfo.TryParseKind(t[2], out _), "save <name> <kind> <0xbase -> 0xoff ...>");
                    ValueKindInfo.TryParseKind(t[2], out var kind);
                    var chain = PointerChain.Parse(string.Join(' ', t.Skip(3)));
                    OffsetStore.Upsert(file, new OffsetEntry { Name = t[1], Kind = kind, Chain = chain.ToString() });
                    OffsetStore.Save(offsetsPath, file);
                    Console.WriteLine($"saved '{t[1]}' = {chain} ({kind}) -> {offsetsPath}");
                    break;
                }

                case "vars": // show every saved variable's live value
                {
                    foreach (var e in file.Entries)
                        Console.WriteLine($"  {e.Name,-14} {e.Kind,-7} {e.Chain,-40} = {(e.TryRead(map, out var v) ? v.ToString(CultureInfo.InvariantCulture) : "unresolved")}");
                    if (file.Entries.Count == 0) Console.WriteLine("  (none saved)");
                    break;
                }

                case "features":
                    foreach (var f in runner.Features)
                        Console.WriteLine($"  {f.Name,-10} {f.Describe()}{(f.LastError is null ? "" : "   ERROR: " + f.LastError)}");
                    break;

                case "on": case "off": // on|off <health|magic|rupees|inventory|levitate>
                {
                    Need(t.Length >= 2, "on|off <health|magic|rupees|inventory|levitate>");
                    var f = runner.Get(t[1]) ?? throw new ArgumentException($"No feature '{t[1]}'. Try: features");
                    if (t[0].Equals("on", StringComparison.OrdinalIgnoreCase)) f.Enable(); else f.Disable();
                    Console.WriteLine($"{f.Name}: {f.Describe()}");
                    break;
                }

                case "target": // target <health|magic|rupees> <value>  (fallback when no *_max variable is saved)
                {
                    Need(t.Length >= 3, "target <health|magic|rupees> <value>");
                    if (runner.Get(t[1]) is not FloorLockFeature fl) throw new ArgumentException($"'{t[1]}' has no target.");
                    fl.FixedTarget = Num(t[2]);
                    Console.WriteLine($"{fl.Name}: {fl.Describe()}");
                    break;
                }

                case "set": // set <variable> <value>   one-shot write of a saved variable
                    Need(t.Length >= 3, "set <variable> <value>");
                    Console.WriteLine(inventory.Set(t[1], Num(t[2])) ? $"{t[1]} = {t[2]}" : "write failed (chain does not resolve now)");
                    break;

                case "lock": // lock <variable> <value>   re-written every tick while 'on inventory'
                    Need(t.Length >= 3, "lock <variable> <value>");
                    inventory.Lock(t[1], Num(t[2]));
                    Console.WriteLine($"locked {t[1]} = {t[2]}  (enable with: on inventory)");
                    break;

                case "unlock":
                    Need(t.Length >= 2, "unlock <variable>");
                    Console.WriteLine(inventory.Unlock(t[1]) ? $"unlocked {t[1]}" : $"{t[1]} was not locked");
                    break;

                case "pos":
                    Console.WriteLine(position.Read() is { } cur ? $"position {cur}" : "position unreadable");
                    break;

                case "tp": // tp <x> <y> <z>
                    Need(t.Length >= 4, "tp <x> <y> <z>");
                    Console.WriteLine(position.Teleport(new Vec3((float)Num(t[1]), (float)Num(t[2]), (float)Num(t[3]))) ? "teleported" : "teleport failed");
                    break;

                case "up": case "down": // up|down [amount]  (hover height while levitating, otherwise one-shot nudge)
                {
                    float amt = t.Length > 1 ? (float)Num(t[1]) : 1f;
                    Console.WriteLine(position.Adjust(t[0] == "up" ? amt : -amt) ? "ok" : "failed");
                    break;
                }

                case "savepos": Console.WriteLine(position.SavePosition() ? "position saved" : "position unreadable"); break;
                case "loadpos": Console.WriteLine(position.RestorePosition() ? "position restored" : "no saved position / failed"); break;

                default: Console.WriteLine($"Unknown command '{t[0]}'. Type 'help'."); break;
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            Console.WriteLine($"error: {ex.Message}");
        }
    }
    return 0;

    // ---- local helpers ----
    double Num(string s) => ValueKindInfo.TryParseNumber(s, out var v) ? v : throw new FormatException($"Not a number: '{s}'");

    ulong Addr(string s)
    {
        if (s.StartsWith('#'))
        {
            int idx = int.Parse(s[1..]);
            if (idx < 0 || idx >= scanner.Count) throw new ArgumentException($"No result #{idx} (have {scanner.Count}).");
            return scanner.AddressAt(idx);
        }
        return Convert.ToUInt64(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
    }

    static void Need(bool cond, string usage) { if (!cond) throw new ArgumentException("usage: " + usage); }

    static void Timed(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        Console.WriteLine($"({sw.Elapsed.TotalSeconds:F1}s)");
    }

    static void PrintHits(ValueScanner s, int skip, int take)
    {
        int i = skip;
        foreach (var h in s.Results(skip, take))
            Console.WriteLine($"  #{i++,-6} 0x{h.Guest:X}  was {h.Previous.ToString(CultureInfo.InvariantCulture)}  now {h.Current.ToString(CultureInfo.InvariantCulture)}");
    }

    static void PrintHelp() => Console.WriteLine("""
        scan <kind> <value>              first scan; kinds: u8 i16 i32 i64 f32 f64   (also: scan <kind> between <a> <b>)
        next <value|inc|dec|changed|same|between a b>   narrow candidates after the game value changes
        list [skip] [take]               show candidates with previous/current values
        read  <0xaddr|#idx> [kind]       read a value
        write <0xaddr|#idx> <value> [kind]   write a value (single-player game memory)
        ptr   <0xaddr|#idx> [maxOffHex]  find pointers to an address; [S] marks static slots usable as chain bases
        aob   <hex with ?? wildcards>    byte-pattern search across the guest window
        chain <0xbase -> 0xoff ...> [kind]   resolve and read a pointer chain
        save  <name> <kind> <chain>      store a variable in offsets.json
        vars                             show saved variables with live values
        reset | quit

        features                         list features, state and errors
        on|off <health|magic|rupees|inventory|levitate>
        target <health|magic|rupees> <v> fixed floor used when no health_max/magic_max/rupees_max variable is saved
        set <var> <value>                one-shot write of a saved variable (items, sword, shield ...)
        lock|unlock <var> [value]        hold a variable at a value (needs: on inventory)
        pos | tp <x> <y> <z> | up|down [n] | savepos | loadpos     (needs pos_x, pos_y, pos_z as f32)
        Variable names features look for: health health_max magic magic_max rupees rupees_max pos_x pos_y pos_z
        """);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 2;
}
