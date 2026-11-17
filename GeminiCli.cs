using System.Text.Json;

namespace Glance;

class GeminiCli() : CliProvider("gemini", "Gemini")
{
    // names the CLI accepts for -m besides full model ids
    public static readonly string[] Aliases = { "auto", "pro", "flash", "flash-lite" };

    protected override string NotFound => Strings.GeminiNotFound;

    protected override Task<string> Run(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        return WithShot(chat, async dir =>
        {
            // the prompt arrives on stdin and the CLI appends this one, then reads the @file in the whole text
            var (output, exit, errors) = await Exec(Args(chat.Model), dir, Prompts.Whole(chat), stdout => stdout.ReadToEndAsync(), ct);
            if (Answer(output) is { Length: > 0 } answer)
                return answer;
            // the CLI may print its error JSON on stderr instead
            Answer(errors);
            throw exit != 0 ? Exited(exit, errors) : NoAnswer(errors);
        });
    }

    internal static List<string> Args(string model)
    {
        var args = new List<string> { "-o", "json" };
        if (model.Length > 0)
            args.AddRange(new[] { "-m", model });
        args.AddRange(new[] { "-p", "@shot.jpg" });
        return args;
    }

    // the reply text, or null when the output holds none; an error in the JSON is thrown
    internal static string? Answer(string output)
    {
        int start = output.IndexOf('{');
        if (start < 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(output[start..]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (Api.Message(root, null) is { } error)
                throw new AiException(error);
            return root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
