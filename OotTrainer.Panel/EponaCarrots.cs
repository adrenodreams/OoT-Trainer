using System.Runtime.InteropServices;
using OotTrainer.Core.Scanning;

namespace OotTrainer.Panel;

/// <summary>
/// Keeps Epona's REAL carrot counter full. The number in the item-table area (ItemLayout "carrots") is only a copy the game
/// refreshes from the real one about 20 ms after each change; holding that copy makes the game rewrite it about 8 times a second
/// while sprinting, and the HUD plays its carrot tick every time. Holding the real counter instead, checked about once a
/// millisecond, restores it before the copy ever changes, so there is no ticking.
///
/// The real counter is one byte at the start of Epona's actor data. That data moves each time Epona is spawned, so it is found by
/// its neighbouring bytes (see <see cref="Signature"/>), accepting only a hit whose number equals the copy, and checked again before
/// every write. If the signature stops matching (Epona despawned) the search starts over, at most once every few seconds.
/// </summary>
public sealed class EponaCarrots : IDisposable
{
    /// <summary>Counter byte, then bytes that stayed fixed around it in this build: ?? 00 FF 00 00 00 80 00 00 00 00 00 ?? 00 00 00 ?? 00 00 00.</summary>
    public const string Signature = "?? 00 FF 00 00 00 80 00 00 00 00 00 ?? 00 00 00 ?? 00 00 00";

    /// <summary>Bytes 1..7 of the signature read as one little-endian number (byte 0, the counter, is masked out).</summary>
    public const ulong TailMask = 0xFFFF_FFFF_FFFF_FF00UL;
    public const ulong TailValue = 0x0080_0000_00FF_0000UL;

    public const int MaxCarrots = 6;

    private static readonly ItemDef Mirror = ItemLayout.All.First(d => d.Key == "carrots");

    private readonly Func<ItemTable?> _table;
    private readonly Thread _fast;
    private readonly Thread _finder;
    private volatile bool _stop;
    private volatile bool _active;
    private volatile int _target = -1; // -1 = not holding
    private long _addr;                // guest address of the real counter, 0 = not found
    private long _nextScanTicks;
    private volatile string _status = "";

    public EponaCarrots(Func<ItemTable?> table)
    {
        _table = table;
        _fast = new Thread(FastLoop) { IsBackground = true, Name = "carrot-hold" };
        _finder = new Thread(FinderLoop) { IsBackground = true, Name = "carrot-find" };
        _fast.Start();
        _finder.Start();
    }

    /// <summary>Tests only: ignore hits outside the fake game's memory (the scanner also sees copies of it in this process's own buffers).</summary>
    internal Func<ulong, bool>? HitFilter { get; set; }

    /// <summary>Master switch (the panel's Activate button).</summary>
    public bool Active { get => _active; set => _active = value; }

    /// <summary>Keep at least this many carrots, or null to stop holding.</summary>
    public int? Target { set => _target = value is int v ? Math.Clamp(v, 0, MaxCarrots) : -1; }

    public bool Found => Interlocked.Read(ref _addr) != 0;

    private long _loops;
    /// <summary>Diagnostics: passes of the fast loop so far, and the longest pause between two passes in milliseconds.</summary>
    public long Loops => Interlocked.Read(ref _loops);
    public double MaxGapMs;

    /// <summary>Guest address of the real counter, 0 if not found (for logs).</summary>
    public ulong Address => (ulong)Interlocked.Read(ref _addr);

    /// <summary>Short text for the window, empty when there is nothing to say.</summary>
    public string Status => _status;

    /// <summary>Write the real counter once. False if it has not been found.</summary>
    public bool TrySetNow(int value)
    {
        var table = _table();
        ulong addr = (ulong)Interlocked.Read(ref _addr);
        if (table is null || addr == 0) return false;
        if (!table.Map.TryReadGuest(addr, out ulong a) || (a & TailMask) != TailValue) return false;
        return table.Map.TryWriteGuest(addr, (ushort)Math.Clamp(value, 0, MaxCarrots));
    }

    /// <summary>One check-and-fix pass on the real counter. Returns true if it wrote. Public so the self-test can drive it without the thread.</summary>
    public bool HoldOnce()
    {
        int target = _target;
        var table = _table();
        ulong addr = (ulong)Interlocked.Read(ref _addr);
        if (!_active || target < 0 || table is null || addr == 0) return false;
        if (!table.Map.TryReadGuest(addr, out ulong a)) return false;
        if ((a & TailMask) != TailValue) { Interlocked.Exchange(ref _addr, 0); _status = "Epona changed, searching again..."; return false; }
        int count = (int)(a & 0xFF);
        if (count >= target || count > MaxCarrots) return false;
        bool ok = table.Map.TryWriteGuest(addr, (ushort)target);
        if (Diag.Count < 60) Diag.Enqueue($"{Clock.Elapsed.TotalSeconds:F3}s hold saw {count}, wrote {target}: {(ok ? "ok" : "FAILED")}");
        return ok;
    }

    /// <summary>Diagnostics for the live test: when the hold acted.</summary>
    public static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Diag = new();

    private void FastLoop()
    {
        timeBeginPeriod(1);
        OptOutOfThrottling(); // Windows 11 slows timers and threads of background / hidden programs a lot (150 ms was measured)
        Thread.CurrentThread.Priority = ThreadPriority.Highest; // a busy game must not delay a 1 microsecond check by a whole time slice (36 to 100 ms measured at lower priority)
        try
        {
            var gap = System.Diagnostics.Stopwatch.StartNew();
            while (!_stop)
            {
                double ms = gap.Elapsed.TotalMilliseconds; gap.Restart();
                Interlocked.Increment(ref _loops);
                if (ms > MaxGapMs) MaxGapMs = ms;
                bool armed = _active && _target >= 0 && Interlocked.Read(ref _addr) != 0;
                try { HoldOnce(); } catch { /* process gone: the finder or the panel notices */ }
                // While armed, loop as fast as the system allows (about one CPU core); a sleep can last 15 ms or more, which is long enough
                // for the game to copy the lowered number to the display and tick. Otherwise stay idle.
                if (armed) Thread.Yield(); else Thread.Sleep(20);
            }
        }
        finally { timeEndPeriod(1); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version; public uint ControlMask; public uint StateMask; }

    /// <summary>Tell Windows not to throttle this program (execution speed and timer resolution), best effort.</summary>
    private static void OptOutOfThrottling()
    {
        try
        {
            var s = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch { /* older Windows: nothing to opt out of */ }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, uint size);

    private void FinderLoop()
    {
        var pattern = Pattern.Parse(Signature);
        while (!_stop)
        {
            Thread.Sleep(400);
            try
            {
                var table = _table();
                if (table is null || !_active || _target < 0 || Interlocked.Read(ref _addr) != 0) continue;
                if (Environment.TickCount64 < Interlocked.Read(ref _nextScanTicks)) continue;
                if (!table.TryReadValue(Mirror, out int copy) || copy < 1 || copy > MaxCarrots) { Retry(2000); continue; }

                _status = "Looking for Epona's carrots (ride Epona)...";
                var hits = PatternScanner.Scan(table.Map, pattern, writableOnly: true, maxResults: 400);
                if (!table.TryReadValue(Mirror, out copy)) { Retry(2000); continue; }
                // the copy lags the real counter by about 20 ms, so while sprinting they can differ by one for a moment: accept that, prefer an exact match
                var near = hits.Where(h => HitFilter?.Invoke(h) ?? true)
                               .Where(h => table.Map.TryReadGuest(h, out ulong a) && (a & TailMask) == TailValue && Math.Abs((int)(a & 0xFF) - copy) <= 1)
                               .ToList();
                var exact = near.Where(h => table.Map.TryReadGuest(h, out ulong a) && (int)(a & 0xFF) == copy).ToList();
                var good = exact.Count > 0 ? exact : near;
                if (good.Count == 1)
                {
                    Interlocked.Exchange(ref _addr, (long)good[0]);
                    _status = "Epona's carrots found.";
                }
                else
                {
                    _status = good.Count == 0 ? "Epona's carrots not found yet (are you riding?)." : $"Several matches for Epona's carrots ({string.Join(", ", good.Take(4).Select(g => "0x" + g.ToString("X")))}), trying again...";
                    Retry(good.Count == 0 ? 6000 : 3000); // a scan reads all of the game's memory (a few seconds), so do not repeat it too often
                }
            }
            catch { Retry(5000); }
        }
    }

    private void Retry(int ms) => Interlocked.Exchange(ref _nextScanTicks, Environment.TickCount64 + ms);

    public void Dispose()
    {
        _stop = true;
        _fast.Join(1000);
        _finder.Join(1000); // a scan in progress is allowed to finish; the thread is a background thread anyway
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
