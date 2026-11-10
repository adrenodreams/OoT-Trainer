using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OotTrainer.Panel;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Usage: OotTrainer.Panel [path\to\image.bin]
    ///        OotTrainer.Panel --selftest              panel logic against a synthetic table (no game needed)
    ///        OotTrainer.Panel --demo [--screenshot f] open the window on a fake table; optionally save a picture and exit
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return RunSelfTest();
        if (args.Contains("--epona-live")) return RunEponaLive(args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindImageBin());

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        string settingsPath = Settings.DefaultPath;
        var settings = Settings.Load(settingsPath);

        if (args.Contains("--uitest"))
        {
            using var game = new SyntheticGame();
            game.SeedDemoValues();
            string tmp = Path.Combine(Path.GetTempPath(), "panel-uitest-settings.json");
            File.Delete(tmp);
            using var form = new MainForm(new FixedSource(game.Table, "UI test"), new Settings(), tmp, "Hookshot item panel (ui test)");
            int failed = 0;
            form.Shown += async (_, _) =>
            {
                var results = await form.RunUiChecks(game);
                var lines = results.Select(r => $"  {(r.Pass ? "PASS" : "FAIL")}  {r.Name}{(r.Pass || r.Detail.Length == 0 ? "" : "  [" + r.Detail + "]")}").ToList();
                failed = results.Count(r => !r.Pass);
                lines.Add(failed == 0 ? $"All {results.Count} UI checks passed." : $"{failed} of {results.Count} UI checks FAILED.");
                File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "uitest.txt"), lines);
                form.Close();
            };
            Application.Run(form);
            return failed == 0 ? 0 : 3;
        }

        if (args.Contains("--demo"))
        {
            using var game = new SyntheticGame();
            game.SeedDemoValues();
            using var form = new MainForm(new FixedSource(game.Table, "Demo mode: a fake item table, no game attached."), settings, settingsPath + ".demo", "Hookshot item panel (demo)");
            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                string path = args[shot + 1];
                form.Shown += (_, _) =>
                {
                    var t = new System.Windows.Forms.Timer { Interval = 700 };
                    t.Tick += (_, _) => { t.Stop(); form.SaveScreenshot(path); form.Close(); };
                    t.Start();
                };
            }
            Application.Run(form);
            return 0;
        }

        string? image = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindImageBin();
        if (image is null && settings.ImagePath is { } remembered && File.Exists(remembered)) image = remembered;
        if (image is null || !File.Exists(image))
        {
            // not next to the program: ask once. image.bin is only read for its first 64 bytes, to find the game's memory.
            using var dialog = new OpenFileDialog
            {
                Title = "Find image.bin in your Hookshot game folder",
                Filter = "image.bin|image.bin|All files|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog() != DialogResult.OK) return 1;
            image = dialog.FileName;
        }
        if (settings.ImagePath != image) { settings.ImagePath = image; settings.Save(settingsPath); }

        using var link = new GameLink(image);
        Application.Run(new MainForm(link, settings, settingsPath));
        return 0;
    }

    /// <summary>Walk up from the program's folder until a folder containing image.bin is found.</summary>
    private static string? FindImageBin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "image.bin");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Against the real game, no window: attach, find the item table and Epona's real carrot counter, hold it at 6 for 40 seconds and log
    /// every change of the table's displayed copy (each change is a HUD tick). Sprint on Epona while it runs. Writes epona-live.txt.
    /// </summary>
    private static int RunEponaLive(string? image)
    {
        AttachConsole(-1);
        var log = new List<string>();
        void L(string s) { log.Add(s); Console.WriteLine(s); }
        if (image is null) { L("image.bin not found"); File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "epona-live.txt"), log); return 1; }
        using var link = new GameLink(image);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (link.Table is null && sw.Elapsed.TotalSeconds < 90) Thread.Sleep(200);
        if (link.Table is null) { L("no item table: " + link.Status); File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "epona-live.txt"), log); return 2; }
        L($"table at 0x{link.Table.Base:X}");
        var mirror = ItemLayout.All.First(d => d.Key == "carrots");
        using var epona = new EponaCarrots(() => link.Table) { Target = 6, Active = true };
        sw.Restart();
        while (!epona.Found && sw.Elapsed.TotalSeconds < 120) Thread.Sleep(200);
        L($"found={epona.Found} after {sw.Elapsed.TotalSeconds:F1}s  status='{epona.Status}'");
        if (!epona.Found) { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "epona-live.txt"), log); return 3; }
        L($"real counter at 0x{epona.Address:X}");
        L("holding for 40 s - sprint now");
        sw.Restart();
        int last = -1, lastReal = -1, changes = 0, copyChanges = 0;
        double t0 = EponaCarrots.Clock.Elapsed.TotalSeconds;
        L($"(hold log times are on a shared clock; this log starts at {t0:F3}s on it)");
        while (sw.Elapsed.TotalSeconds < 40)
        {
            var t = link.Table;
            if (t is null) continue;
            int real = t.Map.TryReadGuest(epona.Address, out ushort r) ? (r & 0xFF) : -1;
            bool hasCopy = t.TryReadValue(mirror, out int v);
            bool copyChanged = hasCopy && v != last, realChanged = real != lastReal;
            if (copyChanged) copyChanges++;
            if ((copyChanged || realChanged) && ++changes <= 70) L($"{sw.Elapsed.TotalSeconds:F3}s real={real} copy={v}{(epona.Found ? "" : "  (address dropped)")}");
            last = v; lastReal = real;
            Thread.Sleep(1);
        }
        foreach (var d in epona.Diag.Take(12)) L(d);
        L($"copy changed {copyChanges} times in 40 s; fast loop passes={epona.Loops}, longest pause={epona.MaxGapMs:F1} ms");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "epona-live.txt"), log);
        return 0;
    }

    private static int RunSelfTest()
    {
        AttachConsole(-1);
        var results = SelfTest.Run();
        var lines = results.Select(r => $"  {(r.Pass ? "PASS" : "FAIL")}  {r.Name}{(r.Pass || r.Detail.Length == 0 ? "" : "  [" + r.Detail + "]")}").ToList();
        int failed = results.Count(r => !r.Pass);
        lines.Add(failed == 0 ? $"All {results.Count} checks passed." : $"{failed} of {results.Count} checks FAILED.");
        foreach (var l in lines) Console.WriteLine(l);
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "selftest.txt"), lines); // WinExe has no console of its own
        return failed == 0 ? 0 : 3;
    }
}
