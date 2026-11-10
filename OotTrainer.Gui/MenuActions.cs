using OotTrainer.Core.Features;

namespace OotTrainer.Gui;

/// <summary>Menu logic with no ImGui dependency, so it can be self-tested against synthetic memory.</summary>
public sealed class MenuActions(FeatureContext ctx, InventoryFeature inventory)
{
    public const double FallbackMaxRupees = 999;

    public bool HasRupees => ctx.Find("rupees") is not null;

    /// <summary>Why an item button is unusable, or null when it can be pressed.</summary>
    public string? Blocker(CatalogItem item) =>
        ctx.Find(item.Variable) is null ? $"save '{item.Variable}' first (scan / ptr / save)"
        : item.Value is null ? "item ID not set; add it to trainer\\items.json"
        : null;

    /// <summary>Live <c>rupees_max</c> when saved and readable, otherwise <see cref="FallbackMaxRupees"/>.</summary>
    public double MaxRupeesTarget() =>
        ctx.Find("rupees_max") is { } max && max.TryRead(ctx.Map, out double v) ? v : FallbackMaxRupees;

    public string MaxRupees()
    {
        if (!HasRupees) return "rupees not saved in offsets.json";
        double target = MaxRupeesTarget();
        try { return inventory.Set("rupees", target) ? $"rupees = {target:G}" : "chain did not resolve (loading screen?)"; }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    public string Give(CatalogItem item)
    {
        if (Blocker(item) is { } why) return why;
        try { return inventory.Set(item.Variable, item.Value!.Value) ? $"gave {item.Label}" : "chain did not resolve (loading screen?)"; }
        catch (Exception ex) { return "error: " + ex.Message; } // e.g. value does not fit the variable's type
    }
}
