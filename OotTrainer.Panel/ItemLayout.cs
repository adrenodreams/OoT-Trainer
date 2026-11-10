namespace OotTrainer.Panel;

/// <summary>One editable number in the game's live item table.</summary>
/// <param name="Key">Stable id used in the settings file.</param>
/// <param name="Index">Entry number relative to the first entry matched by <see cref="ItemLayout.MarkerText"/> (may be negative).</param>
/// <param name="Default">Initial value in the editor box, in displayed units.</param>
/// <param name="Max">Largest value the editor box accepts, in displayed units.</param>
/// <param name="CanHold">Whether the "Infinite" (hold) option is offered.</param>
/// <param name="TopUpTo">When set, Infinite keeps this entry equal to the live value of the entry with that key (health to max health) instead of the box number.</param>
/// <param name="Scale">Raw game units per displayed unit (health is stored in quarter hearts, so hearts have Scale 4). The window shows and edits displayed units; the game table always gets raw units.</param>
/// <param name="ByteOffset">When set, this number is a lone int at that many bytes from the table's first entry (negative = before it) instead of a table entry: no marker, no duplicate. Reads and writes are refused while the current value is outside 0..Max, so a moved address is never written blindly.</param>
public sealed record ItemDef(string Key, string Label, string Group, int Index, int Default, int Max, bool CanHold, string? TopUpTo = null, int Scale = 1, long? ByteOffset = null);

/// <summary>
/// ItemLayout of the game's live item table, worked out from this build and one save: entries are 0x28 bytes apart, each starts
/// with a vtable-style marker, the count is an int at +0x1C and a duplicate copy sits at +0x20. Entry k is GameData Int-list
/// entry #(971 + k), which is how rupees (-9), bombs (36), bombchu (40), nuts (48), seeds (49), sticks (50),
/// scrub seeds (86), magic beans (61, found by buying a second bean) and Gold Skulltula tokens (94) were matched.
/// </summary>
public static class ItemLayout
{
    /// <summary>Marker + 0x20 + 1 as seen at the start of most entries. The rupees entry differs after the marker, so it is not matched by this.</summary>
    public const string MarkerText = "18 07 C4 84 00 00 00 00 20 00 00 00 01 00 00 00";

    /// <summary>First four marker bytes as a little-endian uint, checked at every entry before it is touched.</summary>
    public const uint MarkerDword = 0x84C40718;

    public const ulong Stride = 0x28;
    public const ulong CountOffset = 0x1C;
    public const ulong CopyOffset = 0x20;

    /// <summary>Fewest marker matches that counts as "the table" (the real one has 116 entries in one run).</summary>
    public const int MinRun = 100;

    public const string GroupItems = "Items";
    public const string GroupVitals = "Health and magic";
    public const string GroupLimits = "Limits";
    public const string GroupHorse = "Epona";

    public static readonly IReadOnlyList<ItemDef> All =
    [
        new("rupees", "Rupees", GroupItems, -9, 1000, 9999, true),
        new("bombs", "Bombs", GroupItems, 36, 99, 999, true),
        new("bombchu", "Bombchus", GroupItems, 40, 99, 999, true),
        new("nuts", "Deku nuts", GroupItems, 48, 99, 999, true),
        new("seeds", "Deku seeds", GroupItems, 49, 99, 999, true),
        new("sticks", "Deku sticks", GroupItems, 50, 99, 999, true),
        new("scrub", "Business scrub seeds", GroupItems, 86, 99, 999, true),
        new("beans", "Magic beans", GroupItems, 61, 10, 99, true),
        new("skulltula", "Gold Skulltula tokens", GroupItems, 94, 999, 999, true),

        // 4 health = 1 heart (6 hearts read 24); magic full read 240. Entries -8..-4 sit just before rupees (-9) in the same table.
        new("health", "Hearts now", GroupVitals, -8, 10, 20, true, TopUpTo: "max_health", Scale: 4),
        new("max_health", "Max hearts", GroupVitals, -7, 10, 20, true, Scale: 4),
        new("magic", "Magic now", GroupVitals, -6, 240, 999, true, TopUpTo: "max_magic"),
        new("max_magic", "Max magic", GroupVitals, -4, 240, 999, true),

        // Epona's carrots (0..6, refill about one per 2-3 s). Found with a scan while riding: 0x107EF0CDAC when the table's first entry was 0x107EF0E280, so -0x14D4.
        // It is a lone int, not a table entry. Holding it at 6 kept all six carrots lit while sprinting.
        new("carrots", "Epona carrots (6 = full)", GroupHorse, 0, 6, 6, true, ByteOffset: -0x14D4),

        new("bomb_limit", "Bomb limit", GroupLimits, 1, 99, 999, false),
        new("nut_limit", "Deku nut limit", GroupLimits, 2, 99, 999, false),
        new("seed_limit", "Deku seed limit", GroupLimits, 3, 99, 999, false),
        new("stick_limit", "Deku stick limit", GroupLimits, 4, 99, 999, false),
        new("wallet", "Wallet limit", GroupLimits, 5, 1000, 9999, false),
    ];
}
