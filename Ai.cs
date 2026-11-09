namespace Glance;

static class Ai
{
    public static IProvider Create(Settings s)
    {
        string id = s.Provider;
        if (id == Settings.Claude)
            return new ClaudeCli();
        string? key = Key(s, id);
        string url = BaseOf(id, s.CustomUrl, key != null);
        return id switch
        {
            "gemini" => new GeminiProvider(key!),
            "openai" => new OpenAiProvider("OpenAI", url, key, TimeSpan.FromSeconds(90)),
            _ => new OpenAiProvider(new Uri(url).Host, url, key, TimeSpan.FromMinutes(5)),
        };
    }

    public static Task<string[]> Models(Settings s, string id, string? urlText, string? typedKey, CancellationToken ct)
    {
        string? key = typedKey ?? Key(s, id);
        string url = BaseOf(id, urlText, key != null);
        return id switch
        {
            "gemini" => GeminiProvider.Models(key!, ct),
            "openai" => OpenAiProvider.Models("OpenAI", url, key, ct),
            _ => OpenAiProvider.Models(new Uri(url).Host, url, key, ct),
        };
    }

    static string? Key(Settings s, string id)
    {
        string? key = s.GetKey(id);
        if (key == null && s.HasKey(id))
            throw new AiException("The saved API key can't be read by this Windows account. Connect the AI again to enter a new one.");
        if (key == null && id != "custom")
            throw new AiException("This AI needs an API key.");
        return key;
    }

    static string BaseOf(string id, string? text, bool withKey)
    {
        if (id != "custom")
            return OpenAiProvider.DefaultUrl;
        var error = Api.CheckUrl(text ?? "", withKey, out var url);
        return error == null ? url : throw new AiException(error);
    }
}
