import Foundation

// A child process in its own process group, so the whole tree can be stopped; Process can't do that.
final class Child: @unchecked Sendable {
    let pid: pid_t
    let input: FileHandle
    let output: FileHandle
    let errors: FileHandle

    init(path: String, args: [String], env: [String: String], dir: String) throws {
        func pipes() throws -> (read: Int32, write: Int32) {
            var fds: [Int32] = [0, 0]
            guard pipe(&fds) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
            _ = fcntl(fds[0], F_SETFD, FD_CLOEXEC)
            _ = fcntl(fds[1], F_SETFD, FD_CLOEXEC)
            return (fds[0], fds[1])
        }
        let stdin = try pipes(), stdout = try pipes(), stderr = try pipes()

        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_adddup2(&actions, stdin.read, 0)
        posix_spawn_file_actions_adddup2(&actions, stdout.write, 1)
        posix_spawn_file_actions_adddup2(&actions, stderr.write, 2)
        posix_spawn_file_actions_addchdir_np(&actions, dir)
        var attr: posix_spawnattr_t?
        posix_spawnattr_init(&attr)
        defer { posix_spawnattr_destroy(&attr) }
        posix_spawnattr_setflags(&attr, Int16(POSIX_SPAWN_SETPGROUP))
        posix_spawnattr_setpgroup(&attr, 0)

        let argv = ([path] + args).map { strdup($0) } + [nil]
        let envp = env.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }
        var child: pid_t = 0
        let status = posix_spawn(&child, path, &actions, &attr, argv, envp)
        close(stdin.read)
        close(stdout.write)
        close(stderr.write)
        if status != 0 {
            close(stdin.write)
            close(stdout.read)
            close(stderr.read)
            throw POSIXError(POSIXErrorCode(rawValue: status) ?? .EIO)
        }
        _ = fcntl(stdin.write, F_SETNOSIGPIPE, 1)
        pid = child
        input = FileHandle(fileDescriptor: stdin.write, closeOnDealloc: true)
        output = FileHandle(fileDescriptor: stdout.read, closeOnDealloc: true)
        errors = FileHandle(fileDescriptor: stderr.read, closeOnDealloc: true)
    }

    func kill() {
        Darwin.kill(-pid, SIGKILL)
    }

    func wait() async -> Int32 {
        await withCheckedContinuation { done in
            DispatchQueue.global().async {
                var status: Int32 = 0
                while waitpid(self.pid, &status, 0) < 0 && errno == EINTR {}
                done.resume(returning: status & 0x7f == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f))
            }
        }
    }

    func send(_ text: String) async {
        await withCheckedContinuation { done in
            DispatchQueue.global().async {
                try? self.input.write(contentsOf: Data(text.utf8))
                try? self.input.close()
                done.resume()
            }
        }
    }

    func errorText() async -> String {
        var data = Data()
        do {
            for try await b in errors.bytes where data.count < 65536 {
                data.append(b)
            }
        } catch {
        }
        return String(decoding: data, as: UTF8.self)
    }

    // runs a short command; nil when it can't start or doesn't finish in time
    static func capture(_ path: String, _ args: [String], env: [String: String], limit: Double) async -> (out: String, code: Int32)? {
        guard let child = try? Child(path: path, args: args, env: env, dir: NSTemporaryDirectory()) else { return nil }
        try? child.input.close()
        return await withTaskCancellationHandler {
            await withTaskGroup(of: (String, Int32)?.self) { group in
                group.addTask {
                    var data = Data()
                    do {
                        for try await b in child.output.bytes where data.count < 1_000_000 {
                            data.append(b)
                        }
                    } catch {
                    }
                    return (String(decoding: data, as: UTF8.self), await child.wait())
                }
                group.addTask {
                    try? await Task.sleep(for: .seconds(limit))
                    if !Task.isCancelled {
                        child.kill()
                    }
                    return nil
                }
                let first = await group.next()!
                group.cancelAll()
                return first
            }
        } onCancel: {
            child.kill()
        }
    }
}

// What is found about the CLI is kept, except a missing CLI, so installing it works without restarting Glance.
private actor Cli {
    static let shared = Cli()
    private var found: String?
    private var loginPath: String?
    private var effort: Bool?

    private func shell(_ script: String) async -> String? {
        let sh = ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"
        guard let result = await Child.capture(sh, ["-lc", script], env: ProcessInfo.processInfo.environment, limit: 5), result.code == 0 else { return nil }
        return result.out.split(separator: "\n").last.map { $0.trimmingCharacters(in: .whitespaces) }
    }

    func path() async -> String? {
        if found == nil {
            let home = NSHomeDirectory()
            let dirs = [home + "/.local/bin", home + "/.claude/local", "/opt/homebrew/bin", "/usr/local/bin", home + "/.npm-global/bin", home + "/.bun/bin"]
            found = dirs.map { $0 + "/claude" }.first { FileManager.default.isExecutableFile(atPath: $0) }
            if found == nil, let p = await shell("command -v claude"), p.hasPrefix("/"), FileManager.default.isExecutableFile(atPath: p) {
                found = p
            }
        }
        return found
    }

    func forget() {
        found = nil
    }

    // a script that starts with #!/usr/bin/env node needs the login shell's PATH to find node
    func env() async -> [String: String] {
        var env = ProcessInfo.processInfo.environment
        if loginPath == nil {
            loginPath = await shell("printf %s \"$PATH\"") ?? ""
        }
        if !loginPath!.isEmpty {
            env["PATH"] = loginPath! + ":" + (env["PATH"] ?? "")
        }
        return env
    }

    // an unfinished probe is not remembered, so the next read tries again
    func supportsEffort(_ path: String, _ env: [String: String]) async -> Bool {
        if effort == nil, let result = await Child.capture(path, ["--help"], env: env, limit: 15), result.code == 0 {
            effort = result.out.contains("--effort")
        }
        return effort ?? false
    }
}

struct ClaudeCli: Provider {
    func ask(_ chat: Conversation, onText: @escaping @Sendable (String) -> Void) async throws -> String {
        do {
            guard let path = await Cli.shared.path() else { throw AiError(L("CliNotFound")) }
            let env = await Cli.shared.env()
            let effort = await Cli.shared.supportsEffort(path, env)
            try Task.checkCancellation()
            return try await withLimit(90, name: "Claude") { try await run(path, env, effort, chat, onText) }
        } catch let e as AiError {
            // its limit and sign-in messages are plain text that changes between versions,
            // so any failure counts as a reason to move on rather than matching their wording
            throw AiError(e.message, keyProblem: true)
        }
    }

    private func run(_ path: String, _ env: [String: String], _ effort: Bool, _ chat: Conversation,
                     _ onText: @Sendable (String) -> Void) async throws -> String {
        var args = [
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--model", chat.model, "--tools=", "--no-session-persistence", "--strict-mcp-config",
            "--setting-sources=", "--disable-slash-commands", "--system-prompt", Prompts.system(chat),
        ]
        if effort {
            args += ["--effort", "high"]
        }
        let dir = NSTemporaryDirectory() + "Glance"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let child: Child
        do {
            child = try Child(path: path, args: args, env: env, dir: dir)
        } catch {
            await Cli.shared.forget()
            throw AiError(L("CliNotFound"))
        }
        defer { child.kill() }

        let message: [String: Any] = [
            "type": "user",
            "message": ["role": "user", "content": [
                ["type": "image", "source": ["type": "base64", "media_type": "image/jpeg", "data": chat.jpeg.base64EncodedString()]],
                ["type": "text", "text": Prompts.message(chat)],
            ]],
        ]
        let line = String(decoding: try JSONSerialization.data(withJSONObject: message), as: UTF8.self) + "\n"

        return try await withTaskCancellationHandler {
            async let errors = child.errorText()
            async let sent: Void = child.send(line)
            var answer: String?
            var failed = false
            var streamed = ""
            for try await text in child.output.bytes.lines {
                guard let root = try? JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any],
                      let type = root["type"] as? String else { continue }
                if type == "stream_event" {
                    if let chunk = Self.textDelta(root) {
                        streamed += chunk
                        onText(streamed)
                    }
                } else if type == "result" {
                    answer = root["result"] as? String ?? ""
                    failed = root["is_error"] as? Bool == true
                }
            }
            await sent
            let code = await child.wait()
            let stderr = await errors
            try Task.checkCancellation()

            if failed {
                throw AiError(answer?.isEmpty == false ? answer! : L("CliError"))
            }
            if let answer, !answer.isEmpty {
                return answer
            }
            if code != 0 {
                let tail = stderr.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "\n", omittingEmptySubsequences: false)
                    .suffix(5).joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
                throw AiError(tail.isEmpty ? L("CliExit", code) : String(tail.suffix(600)))
            }
            throw AiError(L("CliNoAnswer"))
        } onCancel: {
            child.kill()
        }
    }

    static func textDelta(_ root: [String: Any]) -> String? {
        if root["parent_tool_use_id"] is String {
            return nil
        }
        guard let delta = (root["event"] as? [String: Any])?["delta"] as? [String: Any],
              delta["type"] as? String == "text_delta" else { return nil }
        return delta["text"] as? String
    }
}
