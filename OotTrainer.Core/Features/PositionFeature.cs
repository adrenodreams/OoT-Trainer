namespace OotTrainer.Core.Features;

public readonly record struct Vec3(float X, float Y, float Z)
{
    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}

/// <summary>
/// Position access plus levitation. Variables: pos_x, pos_y, pos_z (f32). Y is assumed to be the up axis.
/// Levitation: when enabled, the current height is captured and written every tick so gravity cannot pull Link down;
/// <see cref="Adjust"/> raises/lowers the hover height. Disabling stops writing and gravity resumes.
/// </summary>
public sealed class PositionFeature(FeatureContext ctx) : FeatureBase("levitate", ctx)
{
    private static readonly string[] Vars = ["pos_x", "pos_y", "pos_z"];

    private float _hoverY;
    private Vec3? _saved;

    public override string Describe() =>
        Enabled ? $"on  hovering at Y={_hoverY:F2}" : "off" + (_saved is { } s ? $"  saved position {s}" : "");

    public Vec3? Read()
    {
        lock (Sync)
        {
            Ctx.RequireAll("position", Vars);
            if (Ctx.Require(Name, "pos_x").TryRead(Ctx.Map, out double x) &&
                Ctx.Require(Name, "pos_y").TryRead(Ctx.Map, out double y) &&
                Ctx.Require(Name, "pos_z").TryRead(Ctx.Map, out double z))
                return new Vec3((float)x, (float)y, (float)z);
            return null;
        }
    }

    public bool Teleport(Vec3 to)
    {
        lock (Sync)
        {
            Ctx.RequireAll("position", Vars);
            bool ok = Ctx.Require(Name, "pos_x").TryWrite(Ctx.Map, to.X) &
                      Ctx.Require(Name, "pos_y").TryWrite(Ctx.Map, to.Y) &
                      Ctx.Require(Name, "pos_z").TryWrite(Ctx.Map, to.Z);
            if (ok && Enabled) _hoverY = to.Y; // keep levitation consistent with the new height
            return ok;
        }
    }

    public bool SavePosition()
    {
        var p = Read();
        lock (Sync) _saved = p;
        return p is not null;
    }

    public bool RestorePosition()
    {
        Vec3? s;
        lock (Sync) s = _saved;
        return s is { } v && Teleport(v);
    }

    /// <summary>Raise (+) or lower (-) the hover height while levitating, or nudge Y once when not.</summary>
    public bool Adjust(float dy)
    {
        lock (Sync)
        {
            if (Enabled)
            {
                _hoverY += dy;
                return true;
            }
            var p = Read();
            return p is { } v && Teleport(v with { Y = v.Y + dy });
        }
    }

    protected override void OnEnable()
    {
        Ctx.RequireAll(Name, Vars);
        var p = Read() ?? throw new InvalidOperationException("pos_y does not resolve right now (loading screen?). Try again in-game.");
        _hoverY = p.Y;
    }

    protected override void OnTick() => Ctx.Require(Name, "pos_y").TryWrite(Ctx.Map, _hoverY);
}
