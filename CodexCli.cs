namespace Glance;

class CodexCli() : CliProvider("codex", "Codex")
{
    protected override string NotFound => Strings.CodexNotFound;

    protected override async Task<string> Run(Conversation chat, Action<string> onText, CancellationToken ct)
    {
        var help = await Help(new[] { "exec", "--help" }, ct) ?? "";
        ct.ThrowIfCancellationRequested();
        return await WithShot(chat, async dir =>
        {
            string answer = Path.Combine(dir, "answer.txt");
            var args = Args(help, chat.Model, chat.Effort, answer, Path.Combine(dir, "shot.jpg"));
            var (_, exit, errors) = await Exec(args, dir, Prompts.Whole(chat), stdout => stdout.ReadToEndAsync(), ct);
            if (exit != 0)
                throw Exited(exit, errors);
            string text = File.Exists(answer) ? (await File.ReadAllTextAsync(answer, ct)).Trim() : "";
            return text.Length > 0 ? text : throw NoAnswer(errors);
        });
    }

    // flags are added only when this version lists them, since options come and go between releases
    internal static List<string> Args(string help, string model, string effort, string answer, string shot)
    {
        var args = new List<string> { "exec" };
        foreach (var flag in new[] { "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules" })
        {
            if (help.Contains(flag))
                args.Add(flag);
        }
        if (help.Contains("--color"))
            args.AddRange(new[] { "--color", "never" });
        // -i takes every value up to the next option, so it can't be last before the "-" prompt
        args.AddRange(new[] { "-i", shot, "--sandbox", "read-only", "-o", answer });
        if (model.Length > 0)
            args.AddRange(new[] { "-m", model });
        if (effort.Length > 0)
            args.AddRange(new[] { "-c", "model_reasoning_effort=" + effort });
        args.Add("-");
        return args;
    }
}
