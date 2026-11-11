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

// Tries each option in turn without asking, for when a limit or a budget runs out.
class Failover(string id, (IProvider Provider, string Model)[] steps) : IProvider
{
    // a limit comes back eventually, so the first choice gets another try after this long
    const long RetryMs = 30 * 60 * 1000;

    // starts from what last worked, so a spent option isn't retried on every read
    static readonly Dictionary<string, (int At, long Since)> Last = new();

    public async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        if (Last.TryGetValue(id, out var last) && Environment.TickCount64 - last.Since >= RetryMs)
            Last.Remove(id);
        int start = Last.TryGetValue(id, out last) ? last.At % steps.Length : 0;
        for (int i = 0; ; i++)
        {
            int at = (start + i) % steps.Length;
            chat.Model = steps[at].Model;
            try
            {
                string answer = await steps[at].Provider.AskAsync(chat, onText, ct);
                if (at == 0)
                    Last.Remove(id);
                else if (!Last.TryGetValue(id, out var kept) || kept.At != at)
                    Last[id] = (at, Environment.TickCount64);
                return answer;
            }
            catch (AiException ex) when (ex.KeyProblem && i + 1 < steps.Length)
            {
                // onText always carries the whole text, so the next option replaces any partial answer
            }
            catch (AiException ex) when (ex.KeyProblem && steps.Length > 1)
            {
                throw new AiException(string.Format(Strings.AllFailed, steps.Length, ex.Message));
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
            throw new AiException(string.Format(Strings.CantReach, name));
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
            return string.Format(Strings.Redirect, name);
        string head = code switch
        {
            401 => string.Format(Strings.Http401, name),
            403 => string.Format(Strings.Http403, name),
            404 => string.Format(Strings.Http404, name),
            429 => string.Format(Strings.Http429, name),
            >= 500 => string.Format(Strings.HttpServer, name, code),
            _ => string.Format(Strings.HttpOther, name, code),
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
                        throw new AiException(Strings.LineTooLong);
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
            return Strings.BaseUrlScheme;
        if (uri.UserInfo.Length > 0)
            return Strings.BaseUrlUser;
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return Strings.BaseUrlQuery;
        if (withKey && uri.Scheme == "http" && !uri.IsLoopback)
            return Strings.BaseUrlPlain;
        return null;
    }

    public static string? CheckKey(string key)
    {
        return key.Any(c => c <= ' ' || c > '~') ? Strings.BadKeyChars : null;
    }
}
