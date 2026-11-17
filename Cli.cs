using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Glance;

// A command-line client that is signed in to the user's own account, so no API key is involved.
abstract class CliProvider(string exe, string label) : IProvider
{
    // the help text is read once per command
    static readonly Dictionary<string, string> helps = new();

    protected string Label { get; } = label;

    protected abstract string NotFound { get; }

    protected abstract Task<string> Run(Conversation chat, Action<string> onText, CancellationToken ct);

    public async Task<string> AskAsync(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        try
        {
            return await Run(chat, onText, ct);
        }
        catch (AiException ex)
        {
            // their limit and sign-in messages are plain text that changes between versions,
            // so any failure counts as a reason to move on rather than matching their wording
            throw new AiException(ex.Message) { KeyProblem = true };
        }
    }

    // null when the probe did not finish, so the next read tries again
    protected async Task<string?> Help(string[] args, CancellationToken ct)
    {
        if (helps.TryGetValue(exe, out var text))
            return text;
        if (await Cli.Probe(exe, args, ct) is not { Exit: 0 } probe)
            return null;
        return helps[exe] = probe.Output;
    }

    protected async Task<(T Result, int Exit, string Errors)> Exec<T>(
        IEnumerable<string> args, string dir, string input, Func<StreamReader, Task<T>> read, CancellationToken ct)
    {
        using var proc = Cli.Launch(exe, args, dir);
        if (proc == null)
            throw new AiException(NotFound);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(90));
        using var _ = limit.Token.Register(() => Cli.Kill(proc));

        var write = WriteInput(proc, input);
        var reading = read(proc.StandardOutput);
        var errors = proc.StandardError.ReadToEndAsync();

        try
        {
            try
            {
                await Task.WhenAll(write, reading, errors).WaitAsync(limit.Token);
                await proc.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancel() runs the callbacks last-registered-first and this can resume inline,
                // so the kill registration may already be disposed by the time it would run
                Cli.Kill(proc);
                ct.ThrowIfCancellationRequested();
                throw new AiException(string.Format(Strings.CliTimedOut, Label));
            }

            // the kill closes the pipes, so the awaits above can finish normally after a timeout
            ct.ThrowIfCancellationRequested();
            if (limit.IsCancellationRequested)
                throw new AiException(string.Format(Strings.CliTimedOut, Label));
            return (reading.Result, proc.ExitCode, errors.Result);
        }
        finally
        {
            Cli.Kill(proc);
        }
    }

    protected AiException Exited(int exit, string errors)
    {
        var tail = Cli.Tail(errors);
        return new AiException(tail.Length > 0 ? tail : string.Format(Strings.CliExit, Label, exit));
    }

    protected AiException NoAnswer(string errors)
    {
        var tail = Cli.Tail(errors);
        return new AiException(tail.Length > 0 ? tail : string.Format(Strings.CliNoAnswer, Label));
    }

    // the screenshot is handed over as a file, in a folder only this request uses
    protected static async Task<string> WithShot(Conversation chat, Func<string, Task<string>> run)
    {
        var dir = Directory.CreateTempSubdirectory("Glance-");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir.FullName, "shot.jpg"), chat.Jpeg);
            return await run(dir.FullName);
        }
        finally
        {
            try
            {
                dir.Delete(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("temp folder not removed", ex);
            }
        }
    }

    static async Task WriteInput(Process proc, string text)
    {
        try
        {
            await proc.StandardInput.WriteLineAsync(text);
            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // the process died before reading everything; the caller reports its exit
        }
    }
}

static class Cli
{
    static readonly UTF8Encoding Utf8 = new(false);
    static readonly Dictionary<string, (string Program, string? Script)> found = new();

    public static string WorkDir => Path.Combine(Path.GetTempPath(), "Glance");

    // null when the command is missing or did not finish in time
    public static async Task<(int Exit, string Output)?> Probe(string exe, string[] args, CancellationToken ct)
    {
        using var proc = Launch(exe, args, WorkDir);
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
            return (proc.ExitCode, await output);
        }
        catch (OperationCanceledException)
        {
            Kill(proc);
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    public static Process? Launch(string exe, IEnumerable<string> args, string workDir)
    {
        Directory.CreateDirectory(workDir);

        // not cached while missing, so installing the CLI works without restarting Glance
        if (!found.TryGetValue(exe, out var target))
        {
            if (Find(exe) is not { } at)
                return null;
            found[exe] = target = at;
        }
        var psi = new ProcessStartInfo(target.Program)
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
        if (target.Script != null)
            psi.ArgumentList.Add(target.Script);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception)
        {
            found.Remove(exe);
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

    public static (string Program, string? Script)? Find(string exe)
    {
        foreach (var dir in Dirs())
        {
            var program = Path.Combine(dir, exe + ".exe");
            if (File.Exists(program))
                return (program, null);
            var shim = Path.Combine(dir, exe + ".cmd");
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

    public static string Tail(string errors)
    {
        var tail = string.Join('\n', errors.Trim().Split('\n').TakeLast(5)).Trim();
        return tail.Length > 600 ? tail[^600..] : tail;
    }

    public static void Kill(Process proc)
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
