import Foundation

struct CodexCli: CliProvider {
    let exe = "codex"
    let label = "Codex"
    var notFound: String { L("CodexNotFound") }

    func run(_ path: String, _ env: [String: String], _ chat: ChatRequest, _ onText: @escaping @Sendable (String) -> Void) async throws -> String {
        let listed = await help(path, env, ["exec", "--help"])
        try Task.checkCancellation()
        return try await withShot(chat) { dir in
            let answer = dir + "/answer.txt"
            let args = Self.args(help: listed, model: chat.model, effort: chat.effort, answer: answer, shot: dir + "/shot.jpg")
            let result = try await exec(path, args, env, dir, input: Prompts.whole(chat))
            if result.code != 0 {
                throw exited(result.code, result.errors)
            }
            let text = (try? String(contentsOfFile: answer, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            if text.isEmpty {
                throw noAnswer(result.errors)
            }
            return text
        }
    }

    // flags are added only when this version lists them, since options come and go between releases
    static func args(help: String, model: String, effort: String, answer: String, shot: String) -> [String] {
        var args = ["exec"]
        for flag in ["--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules"] where help.contains(flag) {
            args.append(flag)
        }
        if help.contains("--color") {
            args += ["--color", "never"]
        }
        // -i takes every value up to the next option, so it can't be last before the "-" prompt
        args += ["-i", shot, "--sandbox", "read-only", "-o", answer]
        if !model.isEmpty {
            args += ["-m", model]
        }
        if !effort.isEmpty {
            args += ["-c", "model_reasoning_effort=" + effort]
        }
        return args + ["-"]
    }
}
