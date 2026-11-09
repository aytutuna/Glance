using System.Security.Cryptography;
using System.Text;
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

    public const string Claude = "claude";

    public static readonly (string Id, string Name)[] Providers =
    {
        (Claude, "Claude CLI"),
        ("anthropic", "Claude API"),
        ("openai", "OpenAI"),
        ("gemini", "Gemini"),
        ("custom", "Custom"),
    };

    public string? ReadDevice { get; set; }
    public string? ShowDevice { get; set; }
    public bool CenterAnswer { get; set; }
    public bool PlainAnswer { get; set; }
    public bool UseArea { get; set; }
    // left, top, width, height as fractions of the screen, so it survives a resolution change
    public double[]? AnswerArea { get; set; }

    public static bool ValidArea(double[]? a)
    {
        return a is { Length: 4 } && a.All(v => v >= 0 && v <= 1) && a[2] > 0 && a[3] > 0 && a[0] + a[2] <= 1.001 && a[1] + a[3] <= 1.001;
    }
    public Keys Hotkey { get; set; } = Keys.Control | Keys.Shift | Keys.Q;
    public Keys CloseKey { get; set; } = Keys.Control | Keys.Shift | Keys.X;
    public Keys RegionKey { get; set; } = Keys.Control | Keys.Shift | Keys.D;
    public Keys HideKey { get; set; } = Keys.Control | Keys.Shift | Keys.B;
    public string Model { get; set; } = Models[0].Id;
    public string Mode { get; set; } = Modes[0].Id;
    public string Provider { get; set; } = Claude;
    public string? CustomUrl { get; set; }
    public Dictionary<string, string> ProviderModels { get; set; } = new();
    // DPAPI-encrypted for this Windows account, base64; never stored as plain text
    public Dictionary<string, List<string>> ApiKeys { get; set; } = new();
    // tried from the top: "claude" is the CLI, provider:tag is one saved key, a bare provider a keyless one
    public List<string> Order { get; set; } = new();

    // names a saved key without revealing anything about the key itself
    static string Tag(string stored)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored)))[..8].ToLowerInvariant();
    }

    public List<(string Entry, string Label)> Options()
    {
        var list = new List<(string, string)> { (Claude, "Claude CLI") };
        foreach (var (id, name) in Providers.Skip(1))
        {
            if (id != "anthropic" && !Configured(id))
                continue;
            var stored = ApiKeys.GetValueOrDefault(id) ?? new();
            if (stored.Count == 0 && id == "custom")
                list.Add((id, Uri.TryCreate(CustomUrl, UriKind.Absolute, out var u) ? $"Custom ({u.Host})" : "Custom"));
            foreach (var s in stored)
            {
                string? key = Open(s);
                string end = key == null ? "key can't be read" : key.Length >= 12 ? $"key ending {key[^4..]}" : "key";
                list.Add(($"{id}:{Tag(s)}", $"{name}, {end}"));
            }
        }
        return list;
    }

    public List<string> Chain()
    {
        var known = Options().Select(o => o.Entry).ToList();
        var chain = Order.Where(known.Contains).Distinct().ToList();
        if (chain.Count > 0)
            return chain;
        // until an order is saved: the CLI, then each Claude API key
        return known.Where(e => e == Claude || e.StartsWith("anthropic:")).ToList();
    }

    // a newly connected AI goes to the end of the order
    public void Include(string entry)
    {
        if (Order.Count == 0)
            Order = Chain();
        if (!Order.Contains(entry))
            Order.Add(entry);
    }

    public string? KeyFor(string entry)
    {
        int at = entry.IndexOf(':');
        var stored = ApiKeys.GetValueOrDefault(entry[..at])?.FirstOrDefault(s => Tag(s) == entry[(at + 1)..]);
        return stored == null ? null : Open(stored);
    }

    static string? Open(string stored)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public string[] GetKeys(string id)
    {
        return ApiKeys.GetValueOrDefault(id)?.Select(Open).OfType<string>().ToArray() ?? Array.Empty<string>();
    }

    public bool HasKey(string id)
    {
        return ApiKeys.GetValueOrDefault(id)?.Count > 0;
    }

    public string[] KeyNames(string id)
    {
        return (ApiKeys.GetValueOrDefault(id) ?? new()).Select((stored, i) =>
        {
            string? key = Open(stored);
            if (key == null)
                return $"Key {i + 1} (can't be read)";
            return key.Length >= 12 ? $"Key {i + 1} (ending {key[^4..]})" : $"Key {i + 1}";
        }).ToArray();
    }

    public bool SameHost(string? url)
    {
        return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var a) && Uri.TryCreate(CustomUrl, UriKind.Absolute, out var b) && a.Authority == b.Authority;
    }

    public void AddKey(string id, string key)
    {
        if (GetKeys(id).Contains(key))
            return;
        if (!ApiKeys.TryGetValue(id, out var keys))
            ApiKeys[id] = keys = new();
        string stored = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
        keys.Add(stored);
        Include($"{id}:{Tag(stored)}");
    }

    public void RemoveKey(string id, int index)
    {
        Order.Remove($"{id}:{Tag(ApiKeys[id][index])}");
        ApiKeys[id].RemoveAt(index);
        if (ApiKeys[id].Count == 0)
            ApiKeys.Remove(id);
        if (!Configured(Provider))
            Provider = Claude;
    }

    public string ActiveModel()
    {
        return Provider == Claude ? Model : ProviderModels.GetValueOrDefault(Provider) ?? "";
    }

    public bool Configured(string id)
    {
        if (id == Claude)
            return true;
        if (!ProviderModels.TryGetValue(id, out var model) || model.Length == 0)
            return false;
        return id == "custom" ? !string.IsNullOrEmpty(CustomUrl) : HasKey(id);
    }

    // the file exists but couldn't be read, so saving now would overwrite saved keys
    bool unread;

    public static Settings Load()
    {
        bool migrate = !File.Exists(FilePath) && File.Exists(OldFilePath);
        string path = migrate ? OldFilePath : FilePath;
        if (!File.Exists(path))
            return new Settings();
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
            if (!KeyDialog.IsValid(settings.Hotkey))
                settings.Hotkey = new Settings().Hotkey;
            settings.CloseKey = Distinct(settings.CloseKey, settings.Hotkey);
            settings.RegionKey = Distinct(settings.RegionKey, settings.Hotkey, settings.CloseKey);
            settings.HideKey = Distinct(settings.HideKey, settings.Hotkey, settings.CloseKey, settings.RegionKey);
            if (!Models.Any(m => m.Id == settings.Model))
                settings.Model = Models[0].Id;
            if (!Modes.Any(m => m.Id == settings.Mode))
                settings.Mode = Modes[0].Id;
            if (!ValidArea(settings.AnswerArea))
            {
                settings.AnswerArea = null;
                settings.UseArea = false;
            }
            settings.ApiKeys ??= new();
            settings.ProviderModels ??= new();
            settings.Order ??= new();
            if (!Providers.Any(p => p.Id == settings.Provider) || !settings.Configured(settings.Provider))
                settings.Provider = Claude;
            if (migrate)
                settings.Save();
            return settings;
        }
        catch (JsonException)
        {
            try
            {
                File.Copy(path, path + ".bad", true);
                return new Settings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Settings { unread = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Settings { unread = true };
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
        if (unread)
            return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // written aside first so a crash can't leave a cut-off file
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, FilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
