using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Glance;

static class ClaudeCli
{
    const string NotFound = "The claude command was not found. Install the CLI and sign in, then try again.";

    const string TimedOut = "Claude took too long to answer.";

    static readonly UTF8Encoding Utf8 = new(false);
    static bool? supportsEffort;

    public static async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--model", chat.Model, "--tools=", "--no-session-persistence", "--strict-mcp-config",
            "--setting-sources=", "--disable-slash-commands", "--system-prompt", Prompts.System(chat),
        };

        supportsEffort ??= await HelpMentionsEffort(ct);
        ct.ThrowIfCancellationRequested();
        if (supportsEffort == true)
        {
            args.Add("--effort");
            args.Add("high");
        }

        using var proc = Launch(args);
        if (proc == null)
            throw new ClaudeException(NotFound);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        using var _ = limit.Token.Register(() => Kill(proc));

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

        var write = WriteInput(proc, message);
        var read = ReadResult(proc.StandardOutput, onText);
        var errors = proc.StandardError.ReadToEndAsync();

        try
        {
            try
            {
                await Task.WhenAll(write, read, errors).WaitAsync(limit.Token);
                await proc.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancel() runs the callbacks last-registered-first and this can resume inline,
                // so the kill registration may already be disposed by the time it would run
                Kill(proc);
                ct.ThrowIfCancellationRequested();
                throw new ClaudeException(TimedOut);
            }

            // the kill closes the pipes, so the awaits above can finish normally after a timeout
            ct.ThrowIfCancellationRequested();
            if (limit.IsCancellationRequested)
                throw new ClaudeException(TimedOut);

            var (result, failed) = read.Result;
            if (failed)
                throw new ClaudeException(string.IsNullOrEmpty(result) ? "Claude reported an error." : result);
            if (!string.IsNullOrEmpty(result))
                return result;

            if (proc.ExitCode != 0)
            {
                var tail = string.Join('\n', errors.Result.Trim().Split('\n').TakeLast(5)).Trim();
                if (tail.Length > 600)
                    tail = tail[^600..];
                throw new ClaudeException(tail.Length > 0 ? tail : $"Claude exited with code {proc.ExitCode}.");
            }
            throw new ClaudeException("Claude returned no answer.");
        }
        finally
        {
            Kill(proc);
        }
    }

    static async Task WriteInput(Process proc, string line)
    {
        try
        {
            await proc.StandardInput.WriteLineAsync(line);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // the process died before reading everything; the caller reports its exit
        }
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

    // null when the probe did not finish, so the next read tries again
    static async Task<bool?> HelpMentionsEffort(CancellationToken ct)
    {
        using var proc = Launch(new[] { "--help" });
        if (proc == null)
            return null;
        proc.StandardInput.Close();
        var errors = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await proc.WaitForExitAsync(limit.Token);
            var text = await output;
            return proc.ExitCode == 0 ? text.Contains("--effort") : null;
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    static Process? Launch(IEnumerable<string> args)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "Glance");
        Directory.CreateDirectory(workDir);

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");

        foreach (var file in new[] { "claude", fallback })
        {
            var psi = new ProcessStartInfo(file)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = Utf8,
                StandardOutputEncoding = Utf8,
                StandardErrorEncoding = Utf8,
                WorkingDirectory = workDir,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            try
            {
                return Process.Start(psi);
            }
            catch (Win32Exception)
            {
            }
        }
        return null;
    }

    static void Kill(Process proc)
    {
        try
        {
            proc.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
        }
    }
}

class ClaudeException(string message) : Exception(message);
