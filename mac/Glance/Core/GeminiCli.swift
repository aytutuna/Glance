import Foundation

struct GeminiCli: CliProvider {
    // names the CLI accepts for -m besides full model ids
    static let aliases = ["auto", "pro", "flash", "flash-lite"]

    let exe = "gemini"
    let label = "Gemini"
    var notFound: String { L("GeminiNotFound") }

    func run(_ path: String, _ env: [String: String], _ chat: ChatRequest, _ onText: @escaping @Sendable (String) -> Void) async throws -> String {
        try await withShot(chat) { dir in
            // the prompt arrives on stdin and the CLI appends this one, then reads the @file in the whole text
            let result = try await exec(path, Self.args(chat.model), env, dir, input: Prompts.whole(chat))
            if let answer = try Self.answer(result.output), !answer.isEmpty {
                return answer
            }
            throw result.code != 0 ? exited(result.code, result.errors) : noAnswer(result.errors)
        }
    }

    static func args(_ model: String) -> [String] {
        var args = ["-o", "json"]
        if !model.isEmpty {
            args += ["-m", model]
        }
        return args + ["-p", "@shot.jpg"]
    }

    // the reply text, or nil when the output holds none; an error in the JSON is thrown
    static func answer(_ output: String) throws -> String? {
        guard let start = output.firstIndex(of: "{"),
              let root = try? JSONSerialization.jsonObject(with: Data(output[start...].utf8)) as? [String: Any] else { return nil }
        if let error = Api.message(root, nil) {
            throw AiError(error)
        }
        return root["response"] as? String
    }
}
