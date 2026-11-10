using System.Text.Json;
using System.Text.Json.Serialization;

namespace OotTrainer.Gui;

/// <summary>
/// One menu button: writes <see cref="Value"/> to the offsets.json variable <see cref="Variable"/>.
/// <see cref="Value"/> is null until the real item ID has been discovered; such buttons stay disabled.
/// </summary>
public sealed record CatalogItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("variable")] string Variable,
    [property: JsonPropertyName("value")] double? Value);

public sealed record CatalogGroup(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("items")] List<CatalogItem> Items);

public static class ItemCatalog
{
    /// <summary>
    /// Built-in layout. Values are deliberately null: item IDs and addresses are not known for this build and must be
    /// discovered with the trainer's scan/ptr/save workflow, then supplied through items.json.
    /// </summary>
    public static IReadOnlyList<CatalogGroup> Default { get; } =
    [
        new("Tools",   [new("Hookshot", "item_hookshot", null), new("Longshot", "item_longshot", null)]),
        new("Swords",  [new("Kokiri", "sword", null), new("Master", "sword", null), new("Biggoron", "sword", null)]),
        new("Shields", [new("Deku", "shield", null), new("Hylian", "shield", null), new("Mirror", "shield", null)]),
    ];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip };

    /// <summary>Loads items.json if it exists (replacing the default layout entirely); otherwise returns <see cref="Default"/>.</summary>
    public static IReadOnlyList<CatalogGroup> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<List<CatalogGroup>>(File.ReadAllText(path), Options) ?? [.. Default]
            : Default;
}
