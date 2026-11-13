import Foundation

// A child process in its own process group, so the whole tree can be stopped; Process can't do that.
final class Child: @unchecked Sendable {
    let pid: pid_t
    private let lock = NSLock()
    private var status: Int32?
    let input: FileHandle
    let output: FileHandle
    let errors: FileHandle

    // fds are the child's stdin, stdout and stderr; everything else is closed in the child
    private static func spawn(_ path: String, _ args: [String], _ env: [String: String], _ dir: String, fds: [Int32]) throws -> pid_t {
        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        for (target, fd) in fds.enumerated() {
            posix_spawn_file_actions_adddup2(&actions, fd, Int32(target))
        }
        posix_spawn_file_actions_addchdir_np(&actions, dir)
        var attr: posix_spawnattr_t?
        posix_spawnattr_init(&attr)
        defer { posix_spawnattr_destroy(&attr) }
        var defaults = sigset_t()
        sigfillset(&defaults)
        var mask = sigset_t()
        sigemptyset(&mask)
        posix_spawnattr_setsigdefault(&attr, &defaults)
        posix_spawnattr_setsigmask(&attr, &mask)
        posix_spawnattr_setpgroup(&attr, 0)
        posix_spawnattr_setflags(&attr, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK))

        let argv = ([path] + args).map { strdup($0) } + [nil]
        let envp = env.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }
        var child: pid_t = 0
        let status = posix_spawn(&child, path, &actions, &attr, argv, envp)
        if status != 0 {
            throw POSIXError(POSIXErrorCode(rawValue: status) ?? .EIO)
        }
        return child
    }

    init(path: String, args: [String], env: [String: String], dir: String) throws {
        func pipes() throws -> (read: Int32, write: Int32) {
            var fds: [Int32] = [0, 0]
            guard pipe(&fds) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
            return (fds[0], fds[1])
        }
        let stdin = try pipes(), stdout = try pipes(), stderr = try pipes()
        let all = [stdin.read, stdin.write, stdout.read, stdout.write, stderr.read, stderr.write]
        defer {
            close(stdin.read)
            close(stdout.write)
            close(stderr.write)
        }
        do {
            pid = try Child.spawn(path, args, env, dir, fds: [stdin.read, stdout.write, stderr.write])
        } catch {
            all.forEach { close($0) }
            throw error
        }
        _ = fcntl(stdin.write, F_SETNOSIGPIPE, 1)
        input = FileHandle(fileDescriptor: stdin.write, closeOnDealloc: true)
        output = FileHandle(fileDescriptor: stdout.read, closeOnDealloc: true)
        errors = FileHandle(fileDescriptor: stderr.read, closeOnDealloc: true)
    }

    init(path: String, args: [String], env: [String: String], dir: String, outputFile: String) throws {
        let none = open("/dev/null", O_RDONLY), sink = open("/dev/null", O_WRONLY)
        let out = open(outputFile, O_WRONLY | O_CREAT | O_EXCL, 0o600)
        defer {
            close(none)
            close(sink)
            close(out)
        }
        guard none >= 0, sink >= 0, out >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        pid = try Child.spawn(path, args, env, dir, fds: [none, out, sink])
        input = FileHandle.nullDevice
        output = try FileHandle(forReadingFrom: URL(fileURLWithPath: outputFile))
        errors = FileHandle.nullDevice
    }

    // never signals a group whose leader has been reaped, since the pid could be someone else's by then
    func kill() {
        lock.lock()
        defer { lock.unlock() }
        if status == nil {
            Darwin.kill(-pid, SIGKILL)
        }
    }

    func wait() async -> Int32 {
        await withCheckedContinuation { done in
            DispatchQueue.global().async {
                var info = siginfo_t()
                while waitid(P_PID, id_t(self.pid), &info, WEXITED | WNOWAIT) < 0 && errno == EINTR {}
                self.lock.lock()
                defer { self.lock.unlock() }
                if self.status == nil {
                    var raw: Int32 = 0
                    _ = waitpid(self.pid, &raw, 0)
                    self.status = raw & 0x7f == 0 ? (raw >> 8) & 0xff : 128 + (raw & 0x7f)
                }
                done.resume(returning: self.status!)
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
            for try await b in errors.bytes {
                data.append(b)
                // the end is what matters, so only the last 64 KB is kept
                if data.count >= 131072 {
                    data.removeFirst(data.count - 65536)
                }
            }
        } catch {
        }
        return String(decoding: data, as: UTF8.self)
    }

    // runs a short command; nil when it can't start or doesn't finish in time. Its output goes to a file, so a
    // login profile that leaves a daemon holding stdout can't stall the wait.
    static func capture(_ path: String, _ args: [String], env: [String: String], limit: Double) async -> (out: String, code: Int32)? {
        let file = NSTemporaryDirectory() + "glance-" + shortId()
        defer { unlink(file) }
        guard let child = try? Child(path: path, args: args, env: env, dir: NSTemporaryDirectory(), outputFile: file) else { return nil }
        let code: Int32? = await withTaskCancellationHandler {
            await withTaskGroup(of: Int32?.self) { group in
                group.addTask { await child.wait() }
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
        guard let code, let data = try? Data(contentsOf: URL(fileURLWithPath: file)) else { return nil }
        return (String(decoding: data.prefix(1_000_000), as: UTF8.self), code)
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
    func ask(_ chat: ChatRequest, onText: @escaping @Sendable (String) -> Void) async throws -> String {
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

    private func run(_ path: String, _ env: [String: String], _ effort: Bool, _ chat: ChatRequest,
                     _ onText: @Sendable (String) -> Void) async throws -> String {
        var args = [
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--include-partial-messages", "--model", chat.model, "--tools=", "--no-session-persistence", "--strict-mcp-config",
            "--setting-sources=", "--disable-slash-commands", "--system-prompt", chat.system,
        ]
        if effort {
            args += ["--effort", "high"]
        }
        let dir = NSTemporaryDirectory() + "Glance"
        try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let message: [String: Any] = [
            "type": "user",
            "message": ["role": "user", "content": [
                ["type": "image", "source": ["type": "base64", "media_type": "image/jpeg", "data": chat.jpeg.base64EncodedString()]],
                ["type": "text", "text": chat.message],
            ]],
        ]
        let line = String(decoding: try JSONSerialization.data(withJSONObject: message), as: UTF8.self) + "\n"

        let child: Child
        do {
            child = try Child(path: path, args: args, env: env, dir: dir)
        } catch {
            await Cli.shared.forget()
            throw AiError(L("CliNotFound"))
        }
        defer { child.kill() }

        return try await withTaskCancellationHandler {
            async let errors = child.errorText()
            async let sent: Void = child.send(line)
            var answer: String?
            var failed = false
            var streamed = ""
            // only a newline ends a line: Node leaves U+2028 and U+0085 unescaped inside JSON strings
            func handle(_ bytes: [UInt8]) {
                guard let root = try? JSONSerialization.jsonObject(with: Data(bytes)) as? [String: Any],
                      let type = root["type"] as? String else { return }
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
            do {
                var buf: [UInt8] = []
                for try await byte in child.output.bytes {
                    if byte == 10 {
                        handle(buf)
                        buf.removeAll(keepingCapacity: true)
                    } else {
                        buf.append(byte)
                    }
                }
                handle(buf)
            } catch {
                child.kill()
                _ = await child.wait()
                throw error
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
