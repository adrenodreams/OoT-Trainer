using OotTrainer.Core.Memory;

namespace OotTrainer.Panel;

public enum StatusKind { Waiting, Working, Ready, Error }

/// <summary>Where the panel gets its item table from: the real game, or a fixed table in demo/self-test.</summary>
public interface ITableSource
{
    ItemTable? Table { get; }
    string Status { get; }
    StatusKind Kind { get; }
}

/// <summary>Fixed table, used by demo mode and the self-test.</summary>
public sealed class FixedSource(ItemTable? table, string status) : ITableSource
{
    public ItemTable? Table { get; } = table;
    public string Status { get; } = status;
    public StatusKind Kind => Table is null ? StatusKind.Waiting : StatusKind.Ready;
}

/// <summary>
/// Background connection to hookshot.exe: waits for the game, attaches, locates the guest memory window, finds the item table,
/// and keeps checking it. If the game closes or the table moves it goes back to waiting and finds it again by itself.
/// </summary>
public sealed class GameLink : ITableSource, IDisposable
{
    private readonly byte[] _signature;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private volatile ItemTable? _table;
    private volatile string _status = "Starting...";
    private volatile StatusKind _kind = StatusKind.Waiting;

    public ItemTable? Table => _table;
    public string Status => _status;
    public StatusKind Kind => _kind;

    public GameLink(string imageBinPath)
    {
        _signature = new byte[64];
        using (var fs = File.OpenRead(imageBinPath)) fs.ReadExactly(_signature);
        _loop = Task.Run(() => Run(_cts.Token));
    }

    private void Set(string status, StatusKind kind) { _status = status; _kind = kind; }

    private void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ProcessHandle? proc = null;
            try
            {
                _table = null;
                Set("Waiting for the game (hookshot.exe)...", StatusKind.Waiting);
                while (proc is null && !ct.IsCancellationRequested)
                {
                    try { proc = ProcessHandle.Attach("hookshot"); }
                    catch (InvalidOperationException) { ct.WaitHandle.WaitOne(1000); }
                }
                if (proc is null) return;

                GuestMap? map = null;
                while (map is null && !ct.IsCancellationRequested && proc.IsAlive)
                {
                    Set("Game found, attaching...", StatusKind.Working);
                    try { map = GuestMap.Locate(proc, _signature); }
                    catch (InvalidOperationException) { ct.WaitHandle.WaitOne(2000); } // still booting
                }
                if (map is null) continue;

                int missed = 0;
                while (!ct.IsCancellationRequested && proc.IsAlive)
                {
                    var table = _table;
                    if (table is not null && table.LayoutLooksRight())
                    {
                        missed = 0;
                        Set($"Connected to hookshot (pid {proc.Pid}). Item table found.", StatusKind.Ready);
                        ct.WaitHandle.WaitOne(1500);
                        continue;
                    }
                    if (table is not null) { _table = null; Set("Item table moved, searching again...", StatusKind.Working); }

                    Set("Looking for the item table (load a save and get into the world)...", StatusKind.Working);
                    var found = ItemTable.Find(map);
                    if (found is not null) { _table = found; continue; }
                    missed++;
                    Set($"Item table not found yet (try {missed}). Load a save and get into the world.", StatusKind.Waiting);
                    ct.WaitHandle.WaitOne(3000);
                }
            }
            catch (Exception ex)
            {
                Set("Error: " + ex.Message, StatusKind.Error);
                ct.WaitHandle.WaitOne(3000);
            }
            finally
            {
                _table = null;
                proc?.Dispose();
            }
            if (!ct.IsCancellationRequested) { Set("Game closed. Waiting for it to start again...", StatusKind.Waiting); ct.WaitHandle.WaitOne(1500); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(3000); } catch { /* shutting down */ }
        _cts.Dispose();
    }
}
