namespace OotTrainer.Core.Features;

/// <summary>Background loop that ticks enabled features (~60 Hz). A feature that throws is disabled and keeps its error.</summary>
public sealed class FeatureRunner : IDisposable
{
    private readonly List<IFeature> _features = [];
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public IReadOnlyList<IFeature> Features => _features;

    public FeatureRunner Add(IFeature f)
    {
        _features.Add(f);
        return this;
    }

    public IFeature? Get(string name) =>
        _features.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public T Get<T>() where T : class, IFeature => _features.OfType<T>().First();

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FeatureRunner" };
        _thread.Start();
    }

    private void Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            foreach (var f in _features)
            {
                if (!f.Enabled) continue;
                try { f.Tick(); }
                catch (Exception ex) { f.Fault(ex); }
            }
            token.WaitHandle.WaitOne(8);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(500);
        _cts.Dispose();
    }
}
