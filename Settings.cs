using System.Text.Json;

namespace Glance;

class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance", "settings.json");

    static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenCopy", "settings.json");

    public string? ReadDevice { get; set; }
    public string? ShowDevice { get; set; }
    public Keys Hotkey { get; set; } = Keys.Control | Keys.Shift | Keys.Q;
    public Keys CloseKey { get; set; } = Keys.Control | Keys.Shift | Keys.X;

    public static Settings Load()
    {
        try
        {
            bool migrate = !File.Exists(FilePath) && File.Exists(OldFilePath);
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(migrate ? OldFilePath : FilePath)) ?? new Settings();
            if (!KeyDialog.IsValid(settings.Hotkey))
                settings.Hotkey = new Settings().Hotkey;
            if (!KeyDialog.IsValid(settings.CloseKey))
                settings.CloseKey = new Settings().CloseKey;
            if (settings.CloseKey == settings.Hotkey)
                settings.CloseKey = Keys.Control | Keys.Shift | Keys.W;
            if (migrate)
                settings.Save();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
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
