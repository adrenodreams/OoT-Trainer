namespace OotTrainer.Core.Features;

/// <summary>
/// Keeps a variable from dropping below a target: if current &lt; target, write target.
/// Target is the live value of <c>maxVar</c> when that variable exists in offsets.json, otherwise <see cref="FixedTarget"/>.
/// Using a floor (not a constant write) leaves higher values alone and avoids fighting UI animations.
/// Disabling just stops writing; the game resumes normal control of the value.
/// </summary>
public class FloorLockFeature(string name, FeatureContext ctx, string valueVar, string? maxVar, double fixedTarget)
    : FeatureBase(name, ctx)
{
    public double FixedTarget { get; set; } = fixedTarget;

    public override string Describe()
    {
        string target = maxVar is not null && Ctx.Find(maxVar) is not null ? $"live '{maxVar}'" : FixedTarget.ToString("G");
        return $"{(Enabled ? "on" : "off")}  var '{valueVar}', keep >= {target}";
    }

    protected override void OnEnable() => Ctx.RequireAll(Name, valueVar);

    protected override void OnTick()
    {
        var value = Ctx.Require(Name, valueVar);
        if (!value.TryRead(Ctx.Map, out double cur)) return; // chain unresolved this frame (loading screen): try again next tick

        double target = FixedTarget;
        if (maxVar is not null && Ctx.Find(maxVar) is { } max && max.TryRead(Ctx.Map, out double m)) target = m;

        if (cur < target) value.TryWrite(Ctx.Map, target);
    }
}

public sealed class HealthFeature(FeatureContext ctx, double fixedTarget = 80)
    : FloorLockFeature("health", ctx, "health", "health_max", fixedTarget);

public sealed class MagicFeature(FeatureContext ctx, double fixedTarget = 100)
    : FloorLockFeature("magic", ctx, "magic", "magic_max", fixedTarget);

public sealed class RupeesFeature(FeatureContext ctx, double fixedTarget = 999)
    : FloorLockFeature("rupees", ctx, "rupees", "rupees_max", fixedTarget);
