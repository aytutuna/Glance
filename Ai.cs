namespace Glance;

static class Ai
{
    // Each entry in the order has its own limit, so when one runs out the next carries on.
    public static IProvider Create(Settings s)
    {
        var chain = s.Chain();
        var steps = new List<(IProvider, string)>();
        foreach (var entry in chain)
        {
            if (entry == Settings.Claude)
            {
                steps.Add((new ClaudeCli(), s.Model));
                continue;
            }
            string id = entry.Split(':')[0];
            string? key = entry.Contains(':') ? s.KeyFor(entry) : null;
            string model = s.ProviderModels.GetValueOrDefault(id) ?? (id == "anthropic" ? Settings.Models[0].Id : "");
            // an unreadable key or a broken address drops that one entry, not the whole order
            if (entry.Contains(':') && key == null || model.Length == 0)
                continue;
            try
            {
                steps.Add((Make(id, BaseOf(id, s.CustomUrl, key != null), key), model));
            }
            catch (AiException)
            {
            }
        }
        if (steps.Count == 0)
            throw new AiException("None of the AIs in the order can be used. Check them under AI in the tray menu.");
        return new Failover(string.Join("|", chain), steps.ToArray());
    }

    static IProvider Make(string id, string url, string? key)
    {
        return id switch
        {
            "anthropic" => new ClaudeApi(ClaudeApi.DefaultUrl, key!),
            "gemini" => new GeminiProvider(key!),
            "openai" => new OpenAiProvider("OpenAI", url, key, TimeSpan.FromSeconds(90)),
            _ => new OpenAiProvider(new Uri(url).Host, url, key, TimeSpan.FromMinutes(5)),
        };
    }

    public static Task<string[]> Models(Settings s, string id, string? urlText, string? typedKey, CancellationToken ct)
    {
        // a key saved for one custom address is never sent to a newly typed one
        string? key = typedKey ?? (id == "custom" && !s.SameHost(urlText) ? null : Keys(s, id).FirstOrDefault());
        string url = BaseOf(id, urlText, key != null);
        return id switch
        {
            "anthropic" => ClaudeApi.Models(ClaudeApi.DefaultUrl, key!, ct),
            "gemini" => GeminiProvider.Models(key!, ct),
            "openai" => OpenAiProvider.Models("OpenAI", url, key, ct),
            _ => OpenAiProvider.Models(new Uri(url).Host, url, key, ct),
        };
    }

    static string[] Keys(Settings s, string id)
    {
        var keys = s.GetKeys(id);
        if (keys.Length == 0 && s.HasKey(id))
            throw new AiException("The saved API key can't be read by this Windows account. Connect the AI again to enter a new one.");
        if (keys.Length == 0 && id != "custom")
            throw new AiException("This AI needs an API key.");
        return keys;
    }

    static string BaseOf(string id, string? text, bool withKey)
    {
        if (id != "custom")
            return OpenAiProvider.DefaultUrl;
        var error = Api.CheckUrl(text ?? "", withKey, out var url);
        return error == null ? url : throw new AiException(error);
    }
}
