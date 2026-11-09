using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance;

interface IProvider
{
    Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct);
}

class AiException(string message) : Exception(message)
{
    public bool KeyProblem { get; init; }
}

class KeyRing(string id, string[] keys, Func<string, IProvider> make) : IProvider
{
    // start from the key that last worked, so a spent key isn't retried on every read
    static readonly Dictionary<string, int> Last = new();

    public async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        int start = Last.GetValueOrDefault(id) % keys.Length;
        for (int i = 0; ; i++)
        {
            int at = (start + i) % keys.Length;
            bool wrote = false;
            try
            {
                string answer = await make(keys[at]).AskAsync(chat, text =>
                {
                    wrote = true;
                    onText(text);
                }, ct);
                Last[id] = at;
                return answer;
            }
            catch (AiException ex) when (ex.KeyProblem && !wrote)
            {
                if (i + 1 == keys.Length)
                    throw new AiException($"All {keys.Length} saved keys failed. {ex.Message}");
            }
        }
    }
}

static class Api
{
    // .NET keeps custom headers such as x-goog-api-key on a redirect, so redirects are never followed
    public static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static async Task<HttpResponseMessage> Send(HttpRequestMessage req, string name, string? key, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            throw new AiException($"Couldn't reach {name}. Check the connection and the address.");
        }
        if (resp.IsSuccessStatusCode)
            return resp;
        using (resp)
        {
            string text = await Failure(resp, name, key, ct);
            // a custom provider's name is its host, which must not match the words below
            throw new AiException(text) { KeyProblem = Spent((int)resp.StatusCode, text.Replace(name, "")) };
        }
    }

    // out of credit, rate limited or dead: another key may still work
    public static bool Spent(int code, string text)
    {
        return code is 401 or 402 or 403 or 429 || code == 400 && SpentWords(text);
    }

    public static bool SpentWords(string text)
    {
        return new[] { "credit", "api key", "quota", "rate limit" }.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    static async Task<string> Failure(HttpResponseMessage resp, string name, string? key, CancellationToken ct)
    {
        int code = (int)resp.StatusCode;
        if (code is >= 300 and < 400)
            return $"{name} answered with a redirect. Glance doesn't follow redirects, so check the address.";
        string head = code switch
        {
            401 => $"{name} rejected the API key (401).",
            403 => $"{name} refused the request (403).",
            404 => $"{name} doesn't have that model or address (404).",
            429 => $"{name} is rate limiting this key, or its quota is used up (429).",
            >= 500 => $"{name} had a server error ({code}).",
            _ => $"{name} returned an error ({code}).",
        };
        // providers can echo part of a bad key back, so a 401 gets no detail
        if (code == 401)
            return head;
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await Read(resp, 16 * 1024, ct));
            detail = Message(doc.RootElement, key);
        }
        catch (JsonException)
        {
        }
        return detail == null ? head : head + " " + detail;
    }

    public static async Task<byte[]> Read(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        try
        {
            using var body = await resp.Content.ReadAsStreamAsync(ct);
            using var buf = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while (buf.Length < max && (n = await body.ReadAsync(chunk, ct)) > 0)
                buf.Write(chunk, 0, n);
            return buf.ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<byte>();
        }
    }

    // the text of an error object, one short plain line, or null
    public static string? Message(JsonElement e, string? key)
    {
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0)
            return Message(e[0], key);
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        string? text = null;
        if (e.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String)
                text = err.GetString();
            else if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                text = m.GetString();
        }
        else if (e.TryGetProperty("message", out var top) && top.ValueKind == JsonValueKind.String)
        {
            text = top.GetString();
        }
        return Tidy(text, key);
    }

    public static string? Tidy(string? text, string? key)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!string.IsNullOrEmpty(key))
            text = text.Replace(key, "***");
        text = Regex.Replace(text, @"[\s\p{C}]+", " ").Trim();
        return text.Length > 200 ? text[..197] + "..." : text;
    }

    // the data payload of each server-sent event line; the reader is bounded per line
    public static async IAsyncEnumerable<string> Events(Stream body, [EnumeratorCancellation] CancellationToken ct)
    {
        const int MaxLine = 4 * 1024 * 1024;
        using var reader = new StreamReader(body, Encoding.UTF8);
        var buf = new char[4096];
        var line = new StringBuilder();
        int n;
        while ((n = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                if (buf[i] != '\n')
                {
                    if (line.Length >= MaxLine)
                        throw new AiException("The reply had a line that was far too long.");
                    line.Append(buf[i]);
                    continue;
                }
                if (Data(line.ToString()) is string data)
                    yield return data;
                line.Clear();
            }
        }
        if (Data(line.ToString()) is string last)
            yield return last;
    }

    static string? Data(string line)
    {
        line = line.TrimEnd('\r');
        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return null;
        line = line[5..];
        return line.StartsWith(' ') ? line[1..] : line;
    }

    public static string? CheckUrl(string text, bool withKey, out string url)
    {
        url = text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "The base URL must start with http:// or https://.";
        if (uri.UserInfo.Length > 0)
            return "Take the user name and password out of the base URL.";
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return "Take everything from ? or # onwards out of the base URL.";
        if (withKey && uri.Scheme == "http" && !uri.IsLoopback)
            return "A plain http address would send the API key unencrypted. Use https, or an address on this PC.";
        return null;
    }

    public static string? CheckKey(string key)
    {
        return key.Any(c => c <= ' ' || c > '~') ? "That key has characters an API key can't have." : null;
    }
}
