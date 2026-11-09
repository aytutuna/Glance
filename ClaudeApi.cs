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
        var request = new MessageCreateParams
        {
            Model = chat.Model,
            MaxTokens = 64000,
            System = Prompts.System(chat),
            Messages = messages,
        };
        if (Settings.Models.Any(m => m.Id == chat.Model))
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
            var ids = page.Items.Select(m => m.ID).Where(id => id.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            return ids.Length > 0 ? ids : throw new AiException($"{Name} listed no models.");
        }
        catch (AnthropicException ex)
        {
            throw Failed(ex, key);
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
            return null;
        try
        {
            using var doc = JsonDocument.Parse(message[start..]);
            return Api.Message(doc.RootElement, key);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static AiException Failed(AnthropicException ex, string key)
    {
        string head = ex switch
        {
            AnthropicUnauthorizedException => $"{Name} rejected the API key (401).",
            AnthropicForbiddenException => $"{Name} refused the request (403).",
            AnthropicNotFoundException => $"{Name} doesn't have that model (404).",
            AnthropicRateLimitException => $"{Name} is rate limiting this key, or its quota is used up (429).",
            Anthropic5xxException => $"{Name} had a server error.",
            AnthropicIOException => $"Couldn't reach {Name}. Check the connection.",
            AnthropicBadRequestException => $"{Name} returned an error (400).",
            _ => $"{Name} reported an error.",
        };
        string? detail = ex is AnthropicBadRequestException ? Detail(ex.Message, key) : null;
        string text = detail == null ? head : head + " " + detail;
        int code = ex switch
        {
            AnthropicUnauthorizedException => 401,
            AnthropicForbiddenException => 403,
            AnthropicRateLimitException => 429,
            AnthropicBadRequestException => 400,
            _ => 0,
        };
        return new AiException(text) { KeyProblem = Api.Spent(code, text) };
    }
}
