namespace Glance;

static class Ai
{
    public static IProvider Create(Settings s)
    {
        string id = s.Provider;
        if (id == Settings.Claude)
            return new ClaudeCli();
        var keys = Keys(s, id);
        string url = BaseOf(id, s.CustomUrl, keys.Length > 0);
        return keys.Length > 1
            ? new KeyRing(id, keys, key => Make(id, url, key))
            : Make(id, url, keys.FirstOrDefault());
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
