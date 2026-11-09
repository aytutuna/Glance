namespace Glance;

static class Ai
{
    public static IProvider Create(Settings s)
    {
        string id = s.Provider;
        if (id is Settings.Claude or "anthropic")
            return ClaudeChain(s);
        var keys = Keys(s, id);
        string url = BaseOf(id, s.CustomUrl, keys.Length > 0);
        string model = s.ProviderModels.GetValueOrDefault(id) ?? "";
        return keys.Length > 1
            ? new Failover(id, keys.Select(key => (Make(id, url, key), model)).ToArray())
            : Make(id, url, keys.FirstOrDefault());
    }

    // The CLI and each API key have separate limits, so when one runs out the next carries on.
    // The chain stays within Claude: other companies only see screenshots when picked by hand.
    static IProvider ClaudeChain(Settings s)
    {
        string apiModel = s.ProviderModels.GetValueOrDefault("anthropic") ?? Settings.Models[0].Id;
        var api = s.GetKeys("anthropic").Select(key => ((IProvider)new ClaudeApi(ClaudeApi.DefaultUrl, key), apiModel));
        var cli = new[] { ((IProvider)new ClaudeCli(), s.Model) };
        return new Failover(s.Provider, (s.Provider == Settings.Claude ? cli.Concat(api) : api.Concat(cli)).ToArray());
    }

    static IProvider Make(string id, string url, string? key)
    {
        return id switch
        {
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
