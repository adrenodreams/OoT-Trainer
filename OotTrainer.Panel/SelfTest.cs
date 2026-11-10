namespace OotTrainer.Panel;

/// <summary>Panel logic against a synthetic table in this process. Proves the logic, not that the real offsets are right.</summary>
public static class SelfTest
{
    public static IReadOnlyList<(string Name, bool Pass, string Detail)> Run()
    {
        var results = new List<(string, bool, string)>();
        void Check(string name, bool pass, string detail = "") => results.Add((name, pass, detail));

        using var game = new SyntheticGame();
        game.SeedDemoValues();
        var table = game.Table;

        Check("layout looks right (marker on every entry)", table.LayoutLooksRight());
        Check("read matches what the game set", game.Read("bombs") == 12 && game.Read("rupees") == 523, $"bombs={game.Read("bombs")} rupees={game.Read("rupees")}");

        // FindRunStart: lone hit, then a long run, then a short run
        var hits = new List<ulong> { 0x1000 };
        for (int i = 0; i < 116; i++) hits.Add(0x9000 + (ulong)i * ItemLayout.Stride);
        hits.AddRange([0x20000, 0x20000 + ItemLayout.Stride]);
        Check("run start is the first entry of the long run", ItemTable.FindRunStart(hits) == 0x9000UL, $"{ItemTable.FindRunStart(hits):X}");
        Check("too-short runs are rejected", ItemTable.FindRunStart(hits.Where(h => h < 0x9000 + 20 * ItemLayout.Stride).ToList()) is null);

        // scan the synthetic memory for the real marker (the scan also sees this process's own pattern bytes; the run filter ignores those)
        var found = ItemTable.Find(game.Map);
        Check("Find locates the table by its marker", found is not null && found.Base == table.Base, found is null ? "not found" : $"found 0x{found.Base:X} expected 0x{table.Base:X}");

        // writes touch both copies and refuse a table without the marker
        Check("TryWrite changes count and copy", table.TryWrite(36, 77) && table.TryReadBoth(36, out int c, out int cp) && c == 77 && cp == 77);
        game.Map.TryWriteGuest(table.EntryAddress(40), 0u);
        Check("write is refused when the marker is gone", !table.TryWrite(40, 55));
        game.Map.TryWriteGuest(table.EntryAddress(40), (ulong)ItemLayout.MarkerDword);

        // hold engine
        var engine = new HoldEngine();
        game.GameSets("seeds", 12);
        engine.Set("seeds", 99);
        engine.Tick(table);
        Check("holds do nothing until Activate", game.Read("seeds") == 12, $"seeds={game.Read("seeds")}");
        engine.Active = true;
        engine.Tick(table);
        Check("Activate holds the box number", game.Read("seeds") == 99, $"seeds={game.Read("seeds")}");
        game.GameSets("seeds", 98); // the game counts it down
        engine.Tick(table);
        Check("a used item is topped back up", game.Read("seeds") == 99, $"seeds={game.Read("seeds")}");
        table.TryRead(ItemLayout.All.First(d => d.Key == "seeds").Index, out _);
        game.Map.TryWriteGuest(table.EntryAddress(49) + ItemLayout.CopyOffset, 5); // only the duplicate drifts
        engine.Tick(table);
        table.TryReadBoth(49, out int a, out int b);
        Check("a drifted duplicate is corrected too", a == 99 && b == 99, $"count={a} copy={b}");
        engine.Set("seeds", null);
        game.GameSets("seeds", 3);
        engine.Tick(table);
        Check("clearing Infinite stops holding", game.Read("seeds") == 3, $"seeds={game.Read("seeds")}");
        engine.Set("seeds", 99);
        engine.Active = false;
        game.GameSets("seeds", 50);
        engine.Tick(table);
        Check("Deactivate stops all holds", game.Read("seeds") == 50, $"seeds={game.Read("seeds")}");
        Check("a held key with no table is a no-op", engine.Tick(null) == 0);

        // Epona's carrots: a lone int before the table, held like any other number, never written when it looks wrong
        var carrots = ItemLayout.All.First(d => d.Key == "carrots");
        Check("carrots read through the byte offset", game.Read("carrots") == 3, $"carrots={game.Read("carrots")}");
        engine.Active = true;
        engine.Set("carrots", 6);
        game.GameSets("carrots", 2);
        engine.Tick(table);
        Check("HoldEngine leaves the carrots to EponaCarrots", game.Read("carrots") == 2, $"carrots={game.Read("carrots")}");
        engine.Set("carrots", null);
        engine.Active = false;

        // the real counter: found by its neighbouring bytes, held fast, left alone when the signature goes
        using (var epona = new EponaCarrots(() => table) { HitFilter = a => a >= SyntheticGame.G && a < SyntheticGame.G + 0x10000 })
        {
            static bool WaitFor(Func<bool> f, int ms = 6000) { var end = Environment.TickCount64 + ms; while (Environment.TickCount64 < end) { if (f()) return true; Thread.Sleep(10); } return f(); }

            game.RealCarrots = 3;
            epona.Target = 6; epona.Active = true;
            Check("the real carrot counter is found by its bytes", WaitFor(() => epona.Found), epona.Status);
            game.RealCarrots = 2; // sprinting spent some
            Check("the real counter is topped back up to 6", WaitFor(() => game.RealCarrots == 6, 500), $"real={game.RealCarrots}");
            Check("a manual set writes the real counter", epona.TrySetNow(4));
            epona.Target = null; Thread.Sleep(50); // let a pass that already read the old target finish
            game.RealCarrots = 1; Thread.Sleep(100);
            Check("clearing the target stops the hold", game.RealCarrots == 1, $"real={game.RealCarrots}");
            epona.Target = 6; epona.Active = false; Thread.Sleep(50);
            game.RealCarrots = 1; Thread.Sleep(100);
            Check("the hold waits for Activate", game.RealCarrots == 1, $"real={game.RealCarrots}");
            epona.Active = true;
            game.Map.TryWriteGuest(SyntheticGame.RealCarrotAddress + 2, (byte)0); // Epona despawned: neighbours no longer match
            game.Map.TryWriteGuest(SyntheticGame.RealCarrotAddress, (ushort)2);
            Thread.Sleep(100);
            Check("a changed signature is never written", game.RealCarrots == 2 && !epona.Found, $"real={game.RealCarrots} found={epona.Found}");
            game.Map.TryWriteGuest(SyntheticGame.RealCarrotAddress + 2, (byte)0xFF);
        }

        ulong carrotAddr = unchecked((ulong)((long)table.Base + carrots.ByteOffset!.Value));
        game.Map.TryWriteGuest(carrotAddr, 999);
        Check("a lone int outside 0..6 is never written", !table.TryWriteValue(carrots, 6) && game.Read("carrots") == 999, $"carrots={game.Read("carrots")}");
        game.Map.TryWriteGuest(carrotAddr, 3);

        // settings round trip
        string tmp = Path.Combine(Path.GetTempPath(), "panel-selftest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new Settings { TopMost = true };
            s.Values["bombs"] = 42; s.Infinite["bombs"] = true;
            s.Save(tmp);
            var back = Settings.Load(tmp);
            Check("settings round-trip", back.TopMost && back.Values["bombs"] == 42 && back.Infinite["bombs"]);
            File.WriteAllText(tmp, "not json");
            Check("a damaged settings file falls back to defaults", Settings.Load(tmp).Values.Count == 0);
        }
        finally { try { File.Delete(tmp); } catch { } }

        return results;
    }
}
