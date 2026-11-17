using System.Text;
using System.Text.Json;

namespace Glance;

class ClaudeCli() : CliProvider("claude", "Claude")
{
    protected override string NotFound => Strings.CliNotFound;

    protected override async Task<string> Run(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--model", chat.Model, "--tools=", "--no-session-persistence", "--strict-mcp-config",
            "--setting-sources=", "--disable-slash-commands", "--system-prompt", Prompts.System(chat),
        };

        var help = await Help(new[] { "--help" }, ct);
        ct.ThrowIfCancellationRequested();
        if (help?.Contains("--effort") == true)
        {
            args.Add("--effort");
            args.Add(chat.Effort.Length > 0 ? chat.Effort : "high");
        }

        var message = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = Convert.ToBase64String(chat.Jpeg) } },
                    new { type = "text", text = Prompts.Message(chat) },
                },
            },
        });

        var (read, exit, errors) = await Exec(args, Cli.WorkDir, message, stdout => ReadResult(stdout, onText), ct);
        var (result, failed) = read;
        if (failed)
            throw new AiException(string.IsNullOrEmpty(result) ? string.Format(Strings.CliError, Label) : result);
        if (!string.IsNullOrEmpty(result))
            return result;
        if (exit != 0)
            throw Exited(exit, errors);
        throw new AiException(string.Format(Strings.CliNoAnswer, Label));
    }

    static async Task<(string? Answer, bool Failed)> ReadResult(StreamReader stdout, Action<string> onText)
    {
        string? answer = null;
        bool failed = false;
        var streamed = new StringBuilder();
        string? line;
        while ((line = await stdout.ReadLineAsync()) != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    continue;
                if (type.GetString() == "stream_event")
                {
                    if (TextDelta(root) is string chunk)
                    {
                        streamed.Append(chunk);
                        onText(streamed.ToString());
                    }
                    continue;
                }
                if (type.GetString() != "result")
                    continue;
                answer = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
                failed = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
            }
        }
        return (answer, failed);
    }

    static string? TextDelta(JsonElement root)
    {
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            return null;
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object ||
            !ev.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object ||
            !delta.TryGetProperty("type", out var dt) || dt.GetString() != "text_delta" ||
            !delta.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            return null;
        return text.GetString();
    }
}
