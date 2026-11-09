using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Glance;

abstract class ChatProvider(string name, string? key, TimeSpan limit) : IProvider
{
    const int MaxAnswer = 1_000_000;

    protected string Name { get; } = name;
    protected string? Key { get; } = key;

    protected abstract HttpRequestMessage Request(Conversation chat);

    // new text in one stream chunk; throws when the chunk is an error or a block
    protected abstract string? Delta(JsonElement root, bool any);

    public async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cut.CancelAfter(limit);
        var answer = new StringBuilder();
        try
        {
            using var req = Request(chat);
            using var resp = await Api.Send(req, Name, Key, cut.Token);
            var body = await resp.Content.ReadAsStreamAsync(cut.Token);
            try
            {
                await foreach (var data in Api.Events(body, cut.Token))
                {
                    if (data.Trim() == "[DONE]")
                        break;
                    string? text;
                    try
                    {
                        using var doc = JsonDocument.Parse(data);
                        text = Delta(doc.RootElement, answer.Length > 0);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (string.IsNullOrEmpty(text))
                        continue;
                    answer.Append(text);
                    onText(answer.ToString());
                    if (answer.Length >= MaxAnswer)
                        break;
                }
            }
            catch (IOException)
            {
                if (answer.Length == 0)
                    throw new AiException($"The connection to {Name} was lost.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"{Name} took too long to answer.");
        }
        ct.ThrowIfCancellationRequested();
        if (cut.IsCancellationRequested)
            throw new AiException($"{Name} took too long to answer.");
        if (answer.Length == 0)
            throw new AiException($"{Name} returned no answer.");
        return answer.ToString();
    }

    protected AiException Failed(JsonElement root)
    {
        var detail = Api.Message(root, Key);
        return new AiException(detail == null ? $"{Name} reported an error." : $"{Name} reported an error: {detail}")
        {
            KeyProblem = detail != null && Api.SpentWords(detail),
        };
    }

    protected static StringContent Json(object body)
    {
        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }

    protected static string Image(Conversation chat)
    {
        return Convert.ToBase64String(chat.Jpeg);
    }

    protected static async Task<JsonDocument> Parse(HttpResponseMessage resp, string name, CancellationToken ct)
    {
        try
        {
            return JsonDocument.Parse(await Api.Read(resp, 4 * 1024 * 1024, ct));
        }
        catch (JsonException)
        {
            throw new AiException($"{name} didn't send a list of models.");
        }
    }

    protected static string[] Sorted(IEnumerable<string> ids, string name)
    {
        var list = ids.Where(id => id.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return list.Length > 0 ? list : throw new AiException($"{name} listed no models.");
    }
}

class OpenAiProvider(string name, string baseUrl, string? key, TimeSpan limit) : ChatProvider(name, key, limit)
{
    public const string DefaultUrl = "https://api.openai.com/v1";

    protected override HttpRequestMessage Request(Conversation chat)
    {
        var messages = new List<object> { new { role = "system", content = Prompts.System(chat) } };
        bool first = true;
        foreach (var (user, text) in chat.Turns())
        {
            if (!user)
            {
                messages.Add(new { role = "assistant", content = text });
            }
            else if (first)
            {
                first = false;
                messages.Add(new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text },
                        new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Image(chat) } },
                    },
                });
            }
            else
            {
                messages.Add(new { role = "user", content = text });
            }
        }
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        {
            Content = Json(new { model = chat.Model, stream = true, messages }),
        };
        Authorize(req, Key);
        return req;
    }

    protected override string? Delta(JsonElement root, bool any)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("error", out _))
            throw Failed(root);
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object)
            return null;
        var choice = choices[0];
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
            delta.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String &&
            text.GetString() is { Length: > 0 } s)
            return s;
        if (!any && choice.TryGetProperty("finish_reason", out var why) && why.ValueKind == JsonValueKind.String && why.GetString() == "content_filter")
            throw new AiException($"{Name} filtered the answer and returned no text.");
        return null;
    }

    static void Authorize(HttpRequestMessage req, string? key)
    {
        if (!string.IsNullOrEmpty(key))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public static async Task<string[]> Models(string name, string baseUrl, string? key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        Authorize(req, key);
        using var resp = await Api.Send(req, name, key, ct);
        using var doc = await Parse(resp, name, ct);
        var ids = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);
            }
        }
        return Sorted(ids, name);
    }
}

class GeminiProvider(string? key) : ChatProvider("Gemini", key, TimeSpan.FromSeconds(90))
{
    public const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    protected override HttpRequestMessage Request(Conversation chat)
    {
        var contents = new List<object>();
        bool first = true;
        foreach (var (user, text) in chat.Turns())
        {
            if (user && first)
            {
                first = false;
                contents.Add(new
                {
                    role = "user",
                    parts = new object[]
                    {
                        new { text },
                        new { inlineData = new { mimeType = "image/jpeg", data = Image(chat) } },
                    },
                });
            }
            else
            {
                contents.Add(new { role = user ? "user" : "model", parts = new[] { new { text } } });
            }
        }
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/models/{Uri.EscapeDataString(chat.Model)}:streamGenerateContent?alt=sse")
        {
            Content = Json(new { systemInstruction = new { parts = new[] { new { text = Prompts.System(chat) } } }, contents }),
        };
        req.Headers.Add("x-goog-api-key", Key!);
        return req;
    }

    protected override string? Delta(JsonElement root, bool any)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty("error", out _))
            throw Failed(root);
        if (!any && root.TryGetProperty("promptFeedback", out var feedback) && feedback.ValueKind == JsonValueKind.Object &&
            feedback.TryGetProperty("blockReason", out var block) && block.ValueKind == JsonValueKind.String)
            throw new AiException($"Gemini blocked the screenshot ({Reason(block)}) and returned no text.");
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0 ||
            candidates[0].ValueKind != JsonValueKind.Object)
            return null;
        var candidate = candidates[0];
        var text = new StringBuilder();
        if (candidate.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text.Append(t.GetString());
            }
        }
        if (text.Length > 0)
            return text.ToString();
        if (!any && candidate.TryGetProperty("finishReason", out var why) && why.ValueKind == JsonValueKind.String &&
            why.GetString() is not ("STOP" or "MAX_TOKENS"))
            throw new AiException($"Gemini stopped without an answer ({Reason(why)}). It may have blocked the content.");
        return null;
    }

    static string Reason(JsonElement e)
    {
        var text = new string((e.GetString() ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray());
        return text.Length > 0 ? text : "unknown reason";
    }

    public static async Task<string[]> Models(string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models?pageSize=1000");
        req.Headers.Add("x-goog-api-key", key);
        using var resp = await Api.Send(req, "Gemini", key, ct);
        using var doc = await Parse(resp, "Gemini", ct);
        var ids = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("name", out var id) || id.ValueKind != JsonValueKind.String ||
                    !m.TryGetProperty("supportedGenerationMethods", out var methods) || methods.ValueKind != JsonValueKind.Array)
                    continue;
                if (methods.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "generateContent"))
                    ids.Add(id.GetString()!.StartsWith("models/") ? id.GetString()![7..] : id.GetString()!);
            }
        }
        return Sorted(ids, "Gemini");
    }
}
