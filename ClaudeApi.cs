using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace Glance;

class ClaudeApi(string url, string key) : IProvider
{
    public const string DefaultUrl = "https://api.anthropic.com";
    const string Name = "Claude";
    const int MaxAnswer = 1_000_000;

    public async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cut.CancelAfter(TimeSpan.FromSeconds(90));
        var client = Client(url, key);
        var answer = new StringBuilder();
        bool refused = false;
        try
        {
            await foreach (var e in client.Beta.Messages.CreateStreaming(Params(chat), cut.Token))
            {
                if (e.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text))
                {
                    answer.Append(text.Text);
                    onText(answer.ToString());
                    if (answer.Length >= MaxAnswer)
                        break;
                }
                else if (e.TryPickDelta(out var end) && end.Delta.StopReason == "refusal")
                {
                    refused = true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"{Name} took too long to answer.");
        }
        catch (AnthropicException ex)
        {
            throw Failed(ex, key);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            // what already arrived is kept, as with the other providers
            if (answer.Length == 0)
                throw new AiException($"The connection to {Name} was lost.");
        }
        ct.ThrowIfCancellationRequested();
        if (refused)
            throw new AiException($"{Name} declined to answer this one.");
        if (answer.Length == 0)
            throw new AiException($"{Name} returned no answer.");
        return answer.ToString();
    }

    static MessageCreateParams Params(Conversation chat)
    {
        var messages = new List<BetaMessageParam>();
        bool first = true;
        foreach (var (user, text) in chat.Turns())
        {
            if (!user)
            {
                messages.Add(new BetaAssistantMessageParam { Content = text });
            }
            else if (first)
            {
                first = false;
                messages.Add(new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam>
                    {
                        new BetaImageBlockParam
                        {
                            Source = new BetaBase64ImageSource
                            {
                                Data = Convert.ToBase64String(chat.Jpeg),
                                MediaType = MediaType.ImageJpeg,
                            },
                        },
                        new BetaTextBlockParam { Text = text },
                    },
                });
            }
            else
            {
                messages.Add(new BetaUserMessageParam { Content = text });
            }
        }

        // effort and refusal fallbacks are rejected by older models a key can also list
        bool current = Settings.Models.Any(m => m.Id == chat.Model);
        var request = new MessageCreateParams
        {
            Model = chat.Model,
            MaxTokens = current ? 64000 : 8192,
            System = Prompts.System(chat),
            Messages = messages,
        };
        if (current)
            request = request with { OutputConfig = new BetaOutputConfig { Effort = Effort.High } };
        if (chat.Model is "claude-opus-5-5" or "claude-sonnet-5-5")
        {
            request = request with
            {
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new BetaFallbacksParam(new Default()),
            };
        }
        return request;
    }

    public static async Task<string[]> Models(string url, string key, CancellationToken ct)
    {
        try
        {
            var page = await Client(url, key).Models.List(cancellationToken: ct);
            var ids = new List<string>();
            await foreach (var m in page.Paginate(ct))
                ids.Add(m.ID);
            return ids.Count > 0 ? ids.Order(StringComparer.OrdinalIgnoreCase).ToArray() : throw new AiException($"{Name} listed no models.");
        }
        catch (AnthropicException ex)
        {
            throw Failed(ex, key);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            throw new AiException($"{Name} didn't send a list of models.");
        }
    }

    // BaseUrl is set so an ANTHROPIC_BASE_URL in the environment can't send the key elsewhere
    static AnthropicClient Client(string url, string key)
    {
        // no retries: a spent key should move to the next one at once, and the popup is waiting
        return new AnthropicClient { ApiKey = key, BaseUrl = url, HttpClient = Api.Http, MaxRetries = 0 };
    }

    // the exception text is the status followed by the raw JSON body
    static string? Detail(string message, string key)
    {
        int start = message.IndexOf('{');
        if (start < 0)
            return Api.Tidy(message, key);
        try
        {
            using var doc = JsonDocument.Parse(message[start..].TrimEnd('\'', ' '));
            return Api.Message(doc.RootElement, key);
        }
        catch (JsonException)
        {
            return Api.Tidy(message, key);
        }
    }

    static AiException Failed(AnthropicException ex, string key)
    {
        int code = (int?)(ex as AnthropicApiException)?.StatusCode ?? 0;
        // an error event inside the stream has no status, only a type in its body
        if (code == 0 && (ex.Message.Contains("rate_limit_error") || ex.Message.Contains("billing_error") || ex.Message.Contains("authentication_error")))
            code = 429;
        string head = code switch
        {
            401 => $"{Name} rejected the API key (401).",
            402 => $"This key is out of credit (402).",
            403 => $"{Name} refused the request (403).",
            404 => $"{Name} doesn't have that model (404).",
            429 => $"{Name} is rate limiting this key, or its quota is used up (429).",
            400 => $"{Name} returned an error (400).",
            >= 500 => $"{Name} had a server error ({code}).",
            >= 300 and < 400 => $"{Name} answered with a redirect, which Glance doesn't follow.",
            _ when ex is AnthropicIOException => $"Couldn't reach {Name}. Check the connection.",
            _ => $"{Name} reported an error.",
        };
        // providers can echo part of a bad key back, so a 401 gets no detail
        string? detail = code == 401 ? null : Detail(ex.Message, key);
        string text = detail == null ? head : head + " " + detail;
        return new AiException(text) { KeyProblem = Api.Spent(code, text) };
    }
}
