import Foundation

struct ClaudeApi: ChatProvider {
    static let baseUrl = "https://api.anthropic.com"

    let name = "Claude"
    let key: String?
    let limit = 90.0

    var heads: [Int: String] {
        [0: L("CantReachShort", name), 300: L("RedirectShort", name), 402: L("Http402"), 404: L("Http404Model", name)]
    }

    static func headers(_ req: inout URLRequest, key: String) {
        req.setValue(key, forHTTPHeaderField: "x-api-key")
        req.setValue("2026-10-21", forHTTPHeaderField: "anthropic-version")
    }

    func request(_ chat: ChatRequest) throws -> URLRequest {
        var messages: [[String: Any]] = []
        var first = true
        for (user, text) in chat.turns {
            if !user {
                messages.append(["role": "assistant", "content": text])
            } else if first {
                first = false
                messages.append(["role": "user", "content": [
                    ["type": "image", "source": ["type": "base64", "media_type": "image/jpeg", "data": chat.jpeg.base64EncodedString()]],
                    ["type": "text", "text": text],
                ]])
            } else {
                messages.append(["role": "user", "content": text])
            }
        }

        // effort and the fallback are rejected by older models a key can also list
        let current = Settings.models.contains { $0.id == chat.model }
        var body: [String: Any] = [
            "model": chat.model,
            "max_tokens": current ? 64000 : 8192,
            "stream": true,
            "system": chat.system,
            "messages": messages,
        ]
        if current {
            body["output_config"] = ["effort": "high"]
        }
        let fallback = chat.model == "claude-opus-5-5" || chat.model == "claude-sonnet-5-5"
        if fallback {
            body["fallbacks"] = "default"
        }
        var req = try json(Self.baseUrl + "/v1/messages", method: "POST", body: body)
        Self.headers(&req, key: key!)
        if fallback {
            req.setValue("server-side-fallback-2026-07-01", forHTTPHeaderField: "anthropic-beta")
        }
        return req
    }

    // only text deltas count; thinking and fallback blocks are skipped
    func delta(_ root: Any, started: Bool) throws -> String? {
        guard let event = root as? [String: Any] else { return nil }
        switch event["type"] as? String {
        case "content_block_delta":
            guard let d = event["delta"] as? [String: Any], d["type"] as? String == "text_delta" else { return nil }
            return d["text"] as? String
        case "message_delta":
            if (event["delta"] as? [String: Any])?["stop_reason"] as? String == "refusal" {
                throw AiError(L("Declined", name))
            }
            return nil
        case "error":
            throw streamError(event)
        default:
            return nil
        }
    }

    // an error event inside the stream has no status, only a type in its body
    private func streamError(_ event: [String: Any]) -> AiError {
        let type = (event["error"] as? [String: Any])?["type"] as? String ?? ""
        let limited = ["rate_limit_error", "billing_error", "authentication_error"].contains(type)
        var text = limited ? L("Http429", name) : L("ReportedError", name)
        if let detail = Api.message(event, key) {
            text += " " + detail
        }
        return AiError(text, keyProblem: Api.spent(limited ? 429 : 0, text))
    }

    static func models(key: String) async throws -> [String] {
        var ids: [String] = []
        var after: String?
        while true {
            var url = baseUrl + "/v1/models?limit=1000"
            if let after {
                url += "&after_id=" + after.addingPercentEncoding(withAllowedCharacters: .alphanumerics)!
            }
            var req = URLRequest(url: URL(string: url)!)
            headers(&req, key: key)
            let root = try Models.parse(try await Api.fetch(req, name: "Claude", key: key, heads: ClaudeApi(key: key).heads), "Claude")
            ids += (root["data"] as? [Any] ?? []).compactMap { ($0 as? [String: Any])?["id"] as? String }
            guard root["has_more"] as? Bool == true, let last = root["last_id"] as? String else { break }
            after = last
        }
        return try Models.sorted(ids, "Claude")
    }
}
