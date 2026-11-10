using System.Text.Json;

namespace OotTrainer.Panel;

/// <summary>Remembered box values, which items are set to Infinite, and the always-on-top choice. Stored next to the program.</summary>
public sealed class Settings
{
    public Dictionary<string, int> Values { get; set; } = new();
    public Dictionary<string, bool> Infinite { get; set; } = new();
    public bool TopMost { get; set; }

    /// <summary>Where image.bin was found or chosen last time, so the question is only asked once.</summary>
    public string? ImagePath { get; set; }

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "panel-settings.json");

    public static Settings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings() : new Settings(); }
        catch { return new Settings(); } // a damaged settings file should never stop the panel from opening
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* read-only folder: settings just are not remembered */ }
    }
}
