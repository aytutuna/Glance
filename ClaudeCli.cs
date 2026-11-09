using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glance;

class ClaudeCli : IProvider
{
    const string NotFound = "Glance can't find the claude command. Install the Claude CLI, run claude once to sign in, then try again.";

    const string TimedOut = "Claude took too long to answer.";

    static readonly UTF8Encoding Utf8 = new(false);
    static bool? supportsEffort;

    public async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        try
        {
            return await Run(chat, onText, ct);
        }
        catch (AiException ex)
        {
            // its limit and sign-in messages are plain text that changes between versions,
            // so any failure counts as a reason to move on rather than matching their wording
            throw new AiException(ex.Message) { KeyProblem = true };
        }
    }

    static async Task<string> Run(Conversation chat, Action<string> onText, CancellationToken ct)
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
            throw new AiException(NotFound);

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
                throw new AiException(TimedOut);
            }

            // the kill closes the pipes, so the awaits above can finish normally after a timeout
            ct.ThrowIfCancellationRequested();
            if (limit.IsCancellationRequested)
                throw new AiException(TimedOut);

            var (result, failed) = read.Result;
            if (failed)
                throw new AiException(string.IsNullOrEmpty(result) ? "Claude reported an error." : result);
            if (!string.IsNullOrEmpty(result))
                return result;

            if (proc.ExitCode != 0)
            {
                var tail = string.Join('\n', errors.Result.Trim().Split('\n').TakeLast(5)).Trim();
                if (tail.Length > 600)
                    tail = tail[^600..];
                throw new AiException(tail.Length > 0 ? tail : $"Claude exited with code {proc.ExitCode}.");
            }
            throw new AiException("Claude returned no answer.");
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

    static (string Program, string? Script)? found;

    static Process? Launch(IEnumerable<string> args)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "Glance");
        Directory.CreateDirectory(workDir);

        // not cached while missing, so installing the CLI works without restarting Glance
        found ??= Find();
        if (found is not var (program, script))
            return null;
        var psi = new ProcessStartInfo(program)
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
        if (script != null)
            psi.ArgumentList.Add(script);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception)
        {
            found = null;
            return null;
        }
    }

    // PATH is read fresh from the registry too, since Glance may have started before the CLI was installed
    static IEnumerable<string> Dirs()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(t => (Environment.GetEnvironmentVariable("PATH", t) ?? "").Split(';'))
            .Select(d => Environment.ExpandEnvironmentVariables(d.Trim().Trim('"')))
            .Append(Path.Combine(home, ".local", "bin"))
            .Append(Path.Combine(roaming, "npm"))
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static (string Program, string? Script)? Find()
    {
        foreach (var dir in Dirs())
        {
            var exe = Path.Combine(dir, "claude.exe");
            if (File.Exists(exe))
                return (exe, null);
            var shim = Path.Combine(dir, "claude.cmd");
            if (File.Exists(shim) && Shim(shim) is { } target)
                return target;
        }
        return null;
    }

    // An npm install leaves a batch file. Running what it points to directly keeps cmd.exe
    // from reinterpreting the arguments.
    public static (string Program, string? Script)? Shim(string path)
    {
        // the last quoted path is the one it runs; earlier ones check for a node.exe beside it
        var m = Regex.Matches(File.ReadAllText(path), @"%~?dp0%?\\([^""%]+?\.(exe|js))""", RegexOptions.IgnoreCase).LastOrDefault();
        if (m == null)
            return null;
        string dir = Path.GetDirectoryName(path)!;
        string target = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value));
        if (!File.Exists(target))
            return null;
        if (m.Groups[2].Value.Equals("exe", StringComparison.OrdinalIgnoreCase))
            return (target, null);
        var node = Dirs().Prepend(dir).Select(d => Path.Combine(d, "node.exe")).FirstOrDefault(File.Exists);
        return node == null ? null : (node, target);
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
