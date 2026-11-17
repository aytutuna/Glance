import Foundation

// What is found about a CLI is kept, except a missing CLI, so installing it works without restarting Glance.
actor Cli {
    static let shared = Cli()
    private var found: [String: String] = [:]
    private var loginPath: String?
    private var helps: [String: String] = [:]

    private func shell(_ script: String) async -> String? {
        let sh = ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"
        guard let result = await Child.capture(sh, ["-lc", script], env: ProcessInfo.processInfo.environment, limit: 5), result.code == 0 else { return nil }
        return result.out.split(separator: "\n").last.map { $0.trimmingCharacters(in: .whitespaces) }
    }

    func path(_ exe: String) async -> String? {
        if found[exe] == nil {
            let home = NSHomeDirectory()
            let dirs = [home + "/.local/bin", home + "/.claude/local", "/opt/homebrew/bin", "/usr/local/bin", home + "/.npm-global/bin", home + "/.bun/bin"]
            found[exe] = dirs.map { $0 + "/" + exe }.first { FileManager.default.isExecutableFile(atPath: $0) }
            if found[exe] == nil, let p = await shell("command -v " + exe), p.hasPrefix("/"), FileManager.default.isExecutableFile(atPath: p) {
                found[exe] = p
            }
        }
        return found[exe]
    }

    func forget(_ exe: String) {
        found[exe] = nil
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
    func help(_ exe: String, _ path: String, _ args: [String], _ env: [String: String]) async -> String {
        if helps[exe] == nil, let result = await Child.capture(path, args, env: env, limit: 15), result.code == 0 {
            helps[exe] = result.out
        }
        return helps[exe] ?? ""
    }
}

extension Child {
    // the whole output is kept, up to a cap; the rest is still read so the child never blocks on a full pipe
    func outputText() async -> String {
        var data = Data()
        do {
            for try await b in output.bytes where data.count < 4_000_000 {
                data.append(b)
            }
        } catch {
        }
        return String(decoding: data, as: UTF8.self)
    }
}

// A command-line client signed in to the user's own account, so no API key is involved.
protocol CliProvider: Provider {
    var exe: String { get }
    var label: String { get }
    var notFound: String { get }
    func run(_ path: String, _ env: [String: String], _ chat: ChatRequest, _ onText: @escaping @Sendable (String) -> Void) async throws -> String
}

extension CliProvider {
    func ask(_ chat: ChatRequest, onText: @escaping @Sendable (String) -> Void) async throws -> String {
        do {
            guard let path = await Cli.shared.path(exe) else { throw AiError(notFound) }
            let env = await Cli.shared.env()
            try Task.checkCancellation()
            return try await withLimit(90, name: label) { try await run(path, env, chat, onText) }
        } catch let e as AiError {
            // their limit and sign-in messages are plain text that changes between versions,
            // so any failure counts as a reason to move on rather than matching their wording
            throw AiError(e.message, keyProblem: true)
        } catch let e as CocoaError {
            // the temp folder or the answer file, so the next provider still gets a turn
            throw AiError(e.localizedDescription, keyProblem: true)
        }
    }

    func help(_ path: String, _ env: [String: String], _ args: [String]) async -> String {
        await Cli.shared.help(exe, path, args, env)
    }

    func start(_ path: String, _ args: [String], _ env: [String: String], _ dir: String) async throws -> Child {
        do {
            return try Child(path: path, args: args, env: env, dir: dir)
        } catch {
            await Cli.shared.forget(exe)
            throw AiError(notFound)
        }
    }

    // runs the command with the text on stdin and waits for it to finish
    func exec(_ path: String, _ args: [String], _ env: [String: String], _ dir: String,
              input: String) async throws -> (output: String, code: Int32, errors: String) {
        let child = try await start(path, args, env, dir)
        defer { child.kill() }
        return try await withTaskCancellationHandler {
            async let errors = child.errorText()
            async let output = child.outputText()
            async let sent: Void = child.send(input)
            let out = await output
            await sent
            let code = await child.wait()
            let err = await errors
            try Task.checkCancellation()
            return (out, code, err)
        } onCancel: {
            child.kill()
        }
    }

    // the screenshot is handed over as a file, in a folder only this request uses
    func withShot<T>(_ chat: ChatRequest, _ body: (String) async throws -> T) async throws -> T {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("Glance-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { try? FileManager.default.removeItem(at: dir) }
        try chat.jpeg.write(to: dir.appendingPathComponent("shot.jpg"))
        return try await body(dir.path)
    }

    func exited(_ code: Int32, _ errors: String) -> AiError {
        let text = lastLines(errors)
        return AiError(text.isEmpty ? L("CliExit", label, code) : text)
    }

    func noAnswer(_ errors: String) -> AiError {
        let text = lastLines(errors)
        return AiError(text.isEmpty ? L("CliNoAnswer", label) : text)
    }

    private func lastLines(_ errors: String) -> String {
        let lines = errors.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "\n", omittingEmptySubsequences: false)
        return String(lines.suffix(5).joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines).suffix(600))
    }
}
