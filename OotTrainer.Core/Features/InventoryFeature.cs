namespace OotTrainer.Core.Features;

/// <summary>
/// Inventory modifier. Any named variable (an item slot, "sword", "shield", ...) can be set once with <see cref="Set"/>,
/// or held at a value with <see cref="Lock"/> while the feature is enabled (re-written every tick, so the game cannot
/// revert it). Which numbers mean which item is game data we discover; this class just writes what it is told.
/// </summary>
public sealed class InventoryFeature(FeatureContext ctx) : FeatureBase("inventory", ctx)
{
    private readonly Dictionary<string, double> _locks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, double> Locks
    {
        get { lock (Sync) return new Dictionary<string, double>(_locks, StringComparer.OrdinalIgnoreCase); }
    }

    public override string Describe() =>
        $"{(Enabled ? "on" : "off")}  {_locks.Count} lock(s)" +
        (_locks.Count > 0 ? ": " + string.Join(", ", _locks.Select(l => $"{l.Key}={l.Value:G}")) : "");

    /// <summary>One-shot write. Returns false if the variable's chain does not resolve right now.</summary>
    public bool Set(string variable, double value) => Ctx.Require(Name, variable).TryWrite(Ctx.Map, value);

    public void Lock(string variable, double value)
    {
        Ctx.Require(Name, variable); // fail fast on typos
        lock (Sync) _locks[variable] = value;
    }

    public bool Unlock(string variable)
    {
        lock (Sync) return _locks.Remove(variable);
    }

    protected override void OnTick()
    {
        foreach (var (variable, value) in _locks)
            Ctx.Require(Name, variable).TryWrite(Ctx.Map, value);
    }
}
