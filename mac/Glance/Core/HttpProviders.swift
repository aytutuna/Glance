import Foundation

protocol ChatProvider: Provider {
    var name: String { get }
    var key: String? { get }
    var limit: Double { get }
    var heads: [Int: String] { get }
    func request(_ chat: ChatRequest) throws -> URLRequest
    // new text in one stream chunk; throws when the chunk is an error or a block
    func delta(_ root: Any, started: Bool) throws -> String?
}

extension ChatProvider {
    var heads: [Int: String] { [:] }

    func ask(_ chat: ChatRequest, onText: @escaping @Sendable (String) -> Void) async throws -> String {
        let req = try request(chat)
        let provider = self, name = name, key = key, heads = heads
        return try await withLimit(limit, name: name) {
            let bytes = try await Api.send(req, name: name, key: key, heads: heads)
            var answer = ""
            // true once the answer is complete or has reached the cap
            func take(_ data: String) throws -> Bool {
                if data.trimmingCharacters(in: .whitespaces) == "[DONE]" {
                    return true
                }
                guard let json = try? JSONSerialization.jsonObject(with: Data(data.utf8)),
                      let text = try provider.delta(json, started: !answer.isEmpty), !text.isEmpty else { return false }
                answer += text
                onText(answer)
                return answer.utf8.count >= 1_000_000
            }
            do {
                var lines = EventLines()
                var done = false
                for try await byte in bytes {
                    if let data = try lines.feed(byte), try take(data) {
                        done = true
                        break
                    }
                }
                if !done, let last = lines.finish() {
                    _ = try take(last)
                }
            } catch let e as URLError where answer.isEmpty {
                if e.code == .cancelled {
                    throw CancellationError()
                }
                throw AiError(L("ConnectionLost", name))
            } catch is URLError {
            }
            try Task.checkCancellation()
            if answer.isEmpty {
                throw AiError(L("NoAnswer", name))
            }
            return answer
        }
    }

    func failed(_ root: Any) -> AiError {
        let detail = Api.message(root, key)
        return AiError(detail.map { L("ReportedErrorDetail", name, $0) } ?? L("ReportedError", name),
                       keyProblem: detail.map(Api.spentWords) ?? false)
    }

    func json(_ url: String, method: String = "GET", body: [String: Any]? = nil) throws -> URLRequest {
        var req = URLRequest(url: URL(string: url)!)
        req.httpMethod = method
        if let body {
            req.httpBody = try JSONSerialization.data(withJSONObject: body)
            req.setValue("application/json", forHTTPHeaderField: "content-type")
        }
        return req
    }
}

enum Models {
    static func sorted(_ ids: [String], _ name: String) throws -> [String] {
        let list = Set(ids.filter { !$0.isEmpty }).sorted { $0.caseInsensitiveCompare($1) == .orderedAscending }
        if list.isEmpty {
            throw AiError(L("ListedNoModels", name))
        }
        return list
    }

    static func parse(_ data: Data, _ name: String) throws -> [String: Any] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw AiError(L("NoModelList", name))
        }
        return root
    }
}

struct OpenAiProvider: ChatProvider {
    static let defaultUrl = "https://api.openai.com/v1"

    let name: String
    let baseUrl: String
    let key: String?
    let limit: Double

    func request(_ chat: ChatRequest) throws -> URLRequest {
        var messages: [[String: Any]] = [["role": "system", "content": chat.system]]
        var first = true
        for (user, text) in chat.turns {
            if !user {
                messages.append(["role": "assistant", "content": text])
            } else if first {
                first = false
                messages.append(["role": "user", "content": [
                    ["type": "text", "text": text],
                    ["type": "image_url", "image_url": ["url": "data:image/jpeg;base64," + chat.jpeg.base64EncodedString()]],
                ]])
            } else {
                messages.append(["role": "user", "content": text])
            }
        }
        var req = try json(baseUrl + "/chat/completions", method: "POST", body: ["model": chat.model, "stream": true, "messages": messages])
        authorize(&req, key)
        return req
    }

    func delta(_ root: Any, started: Bool) throws -> String? {
        guard let root = root as? [String: Any] else { return nil }
        if root["error"] != nil {
            throw failed(root)
        }
        guard let choice = (root["choices"] as? [Any])?.first as? [String: Any] else { return nil }
        if let text = (choice["delta"] as? [String: Any])?["content"] as? String, !text.isEmpty {
            return text
        }
        if !started && choice["finish_reason"] as? String == "content_filter" {
            throw AiError(L("Filtered", name))
        }
        return nil
    }

    private func authorize(_ req: inout URLRequest, _ key: String?) {
        if let key, !key.isEmpty {
            req.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
        }
    }

    static func models(name: String, baseUrl: String, key: String?) async throws -> [String] {
        var req = URLRequest(url: URL(string: baseUrl + "/models")!)
        if let key, !key.isEmpty {
            req.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
        }
        let root = try Models.parse(try await Api.fetch(req, name: name, key: key), name)
        let ids = (root["data"] as? [Any] ?? []).compactMap { ($0 as? [String: Any])?["id"] as? String }
        return try Models.sorted(ids, name)
    }
}

struct GeminiProvider: ChatProvider {
    static let baseUrl = "https://generativelanguage.googleapis.com/v1beta"

    let name = "Gemini"
    let key: String?
    let limit = 90.0

    func request(_ chat: ChatRequest) throws -> URLRequest {
        var contents: [[String: Any]] = []
        var first = true
        for (user, text) in chat.turns {
            if user && first {
                first = false
                contents.append(["role": "user", "parts": [
                    ["text": text],
                    ["inlineData": ["mimeType": "image/jpeg", "data": chat.jpeg.base64EncodedString()]],
                ]])
            } else {
                contents.append(["role": user ? "user" : "model", "parts": [["text": text]]])
            }
        }
        let model = chat.model.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~"))) ?? chat.model
        var req = try json("\(Self.baseUrl)/models/\(model):streamGenerateContent?alt=sse", method: "POST", body: [
            "systemInstruction": ["parts": [["text": chat.system]]],
            "contents": contents,
        ])
        req.setValue(key, forHTTPHeaderField: "x-goog-api-key")
        return req
    }

    func delta(_ root: Any, started: Bool) throws -> String? {
        guard let root = root as? [String: Any] else { return nil }
        if root["error"] != nil {
            throw failed(root)
        }
        if !started, let block = (root["promptFeedback"] as? [String: Any])?["blockReason"] as? String {
            throw AiError(L("GeminiBlocked", reason(block)))
        }
        guard let candidate = (root["candidates"] as? [Any])?.first as? [String: Any] else { return nil }
        let parts = (candidate["content"] as? [String: Any])?["parts"] as? [Any] ?? []
        let text = parts.compactMap { ($0 as? [String: Any])?["text"] as? String }.joined()
        if !text.isEmpty {
            return text
        }
        if !started, let why = candidate["finishReason"] as? String, why != "STOP" && why != "MAX_TOKENS" {
            throw AiError(L("GeminiStopped", reason(why)))
        }
        return nil
    }

    private func reason(_ text: String) -> String {
        let clean = String(text.filter { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "_") }.prefix(40))
        return clean.isEmpty ? "unknown reason" : clean
    }

    static func models(key: String) async throws -> [String] {
        var req = URLRequest(url: URL(string: baseUrl + "/models?pageSize=1000")!)
        req.setValue(key, forHTTPHeaderField: "x-goog-api-key")
        let root = try Models.parse(try await Api.fetch(req, name: "Gemini", key: key), "Gemini")
        var ids: [String] = []
        for case let m as [String: Any] in root["models"] as? [Any] ?? [] {
            guard let id = m["name"] as? String, let methods = m["supportedGenerationMethods"] as? [Any],
                  methods.contains(where: { $0 as? String == "generateContent" }) else { continue }
            ids.append(id.hasPrefix("models/") ? String(id.dropFirst(7)) : id)
        }
        return try Models.sorted(ids, "Gemini")
    }
}
