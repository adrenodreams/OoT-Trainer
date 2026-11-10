using OotTrainer.Core.Config;
using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Features;

/// <summary>Thrown when a feature needs a named variable that is missing from offsets.json.</summary>
public sealed class MissingVariableException(string feature, IEnumerable<string> missing)
    : InvalidOperationException($"'{feature}' needs variable(s) not in offsets.json: {string.Join(", ", missing)}. " +
                                "Locate them with scan/next/ptr and 'save <name> <kind> <chain>'.");

/// <summary>Shared state for features: the guest map and the live (mutable) offsets file.</summary>
public sealed class FeatureContext(GuestMap map, OffsetFile offsets)
{
    public GuestMap Map { get; } = map;
    public OffsetFile Offsets { get; } = offsets;

    public OffsetEntry? Find(string name) =>
        Offsets.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public OffsetEntry Require(string feature, string name) =>
        Find(name) ?? throw new MissingVariableException(feature, [name]);

    public void RequireAll(string feature, params string[] names)
    {
        var missing = names.Where(n => Find(n) is null).ToList();
        if (missing.Count > 0) throw new MissingVariableException(feature, missing);
    }
}

public interface IFeature
{
    string Name { get; }
    bool Enabled { get; }
    string? LastError { get; }
    string Describe();
    void Enable();
    void Disable();
    void Tick();
    void Fault(Exception ex);
}

/// <summary>Thread-safe base: Tick/Enable/Disable and subclass mutators all serialize on <see cref="Sync"/>.</summary>
public abstract class FeatureBase(string name, FeatureContext ctx) : IFeature
{
    protected readonly object Sync = new();
    protected FeatureContext Ctx { get; } = ctx;

    public string Name { get; } = name;
    public bool Enabled { get; private set; }
    public string? LastError { get; private set; }

    public virtual string Describe() => Enabled ? "on" : "off";

    public void Enable()
    {
        lock (Sync)
        {
            LastError = null;
            OnEnable(); // validates required variables; throws before we flip state
            Enabled = true;
        }
    }

    public void Disable()
    {
        lock (Sync)
        {
            Enabled = false;
            OnDisable();
        }
    }

    public void Tick()
    {
        lock (Sync)
        {
            if (Enabled) OnTick();
        }
    }

    public void Fault(Exception ex)
    {
        lock (Sync)
        {
            Enabled = false;
            LastError = ex.Message;
        }
    }

    protected virtual void OnEnable() { }
    protected virtual void OnDisable() { }
    protected abstract void OnTick();
}
