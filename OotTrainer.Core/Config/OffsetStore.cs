using System.Text.Json;
using System.Text.Json.Serialization;
using OotTrainer.Core.Memory;

namespace OotTrainer.Core.Config;

/// <summary>A named game variable located via a pointer chain (e.g. "health" -> i32 at 0x82...-> 0x40 -> 0x1C).</summary>
public sealed class OffsetEntry
{
    public string Name { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ValueKind Kind { get; set; }

    /// <summary>Chain text, e.g. "0x82345678 -> 0x10 -> 0x48".</summary>
    public string Chain { get; set; } = "";

    public string? Note { get; set; }

    [JsonIgnore]
    public PointerChain ParsedChain => PointerChain.Parse(Chain);

    public bool TryRead(GuestMap map, out double value)
    {
        value = 0;
        if (ParsedChain.Resolve(map) is not ulong addr) return false;
        var b = map.ReadGuestBytes(addr, Kind.SizeOf());
        if (b is null) return false;
        value = ValueKindInfo.Read(b, Kind);
        return true;
    }

    public bool TryWrite(GuestMap map, double value)
    {
        if (ParsedChain.Resolve(map) is not ulong addr) return false;
        return map.Process.TryWriteBytes(map.ToHost(addr), ValueKindInfo.Encode(value, Kind));
    }
}

/// <summary>offsets.json: build signature + named entries. Offsets are only trusted if the build signature matches.</summary>
public sealed class OffsetFile
{
    /// <summary>Hex of the first 64 bytes of image.bin; guards against applying offsets to a different build.</summary>
    public string? BuildSignature { get; set; }

    public List<OffsetEntry> Entries { get; set; } = [];
}

public static class OffsetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static OffsetFile Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<OffsetFile>(File.ReadAllText(path), Options) ?? new() : new();

    public static void Save(string path, OffsetFile file) => File.WriteAllText(path, JsonSerializer.Serialize(file, Options));

    public static void Upsert(OffsetFile file, OffsetEntry entry)
    {
        file.Entries.RemoveAll(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
        file.Entries.Add(entry);
    }
}
