using System.Text.Json;

namespace Glance;

class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance", "settings.json");

    static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenCopy", "settings.json");

    public static readonly (string Id, string Name)[] Models =
    {
        ("claude-opus-5-5", "Opus 5.5"),
        ("claude-sonnet-5-5", "Sonnet 5.5"),
        ("claude-haiku-5-5", "Haiku 5.5"),
    };

    public static readonly (string Id, string Name)[] Modes =
    {
        ("answer", "Answer"),
        ("explain", "Explain"),
        ("translate", "Translate"),
        ("summarize", "Summarize"),
    };

    public string? ReadDevice { get; set; }
    public string? ShowDevice { get; set; }
    public Keys Hotkey { get; set; } = Keys.Control | Keys.Shift | Keys.Q;
    public Keys CloseKey { get; set; } = Keys.Control | Keys.Shift | Keys.X;
    public Keys RegionKey { get; set; } = Keys.Control | Keys.Shift | Keys.D;
    public string Model { get; set; } = Models[0].Id;
    public string Mode { get; set; } = Modes[0].Id;

    public static Settings Load()
    {
        try
        {
            bool migrate = !File.Exists(FilePath) && File.Exists(OldFilePath);
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(migrate ? OldFilePath : FilePath)) ?? new Settings();
            if (!KeyDialog.IsValid(settings.Hotkey))
                settings.Hotkey = new Settings().Hotkey;
            settings.CloseKey = Distinct(settings.CloseKey, settings.Hotkey);
            settings.RegionKey = Distinct(settings.RegionKey, settings.Hotkey, settings.CloseKey);
            if (!Models.Any(m => m.Id == settings.Model))
                settings.Model = Models[0].Id;
            if (!Modes.Any(m => m.Id == settings.Mode))
                settings.Mode = Modes[0].Id;
            if (migrate)
                settings.Save();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    static Keys Distinct(Keys key, params Keys[] taken)
    {
        if (KeyDialog.IsValid(key) && !taken.Contains(key))
            return key;
        foreach (char c in "XDWERASF")
        {
            var next = Keys.Control | Keys.Shift | (Keys)c;
            if (!taken.Contains(next))
                return next;
        }
        return key;
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
