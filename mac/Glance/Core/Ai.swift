import Foundation

enum Ai {
    // Each entry in the order has its own limit, so when one runs out the next carries on.
    static func create(_ s: Settings) throws -> any Provider {
        let chain = s.chain()
        var steps: [(provider: any Provider, model: String)] = []
        for entry in chain {
            if entry == Settings.claude {
                steps.append((ClaudeCli(), s.model))
                continue
            }
            let id = String(entry.split(separator: ":", omittingEmptySubsequences: false)[0])
            let key = s.keyFor(entry)
            let model = s.providerModels[id] ?? (id == "anthropic" ? Settings.models[0].id : "")
            // an unreadable key or a broken address drops that one entry, not the whole order
            if entry.contains(":") && key == nil || model.isEmpty {
                continue
            }
            if let url = try? baseOf(id, s.customUrl, withKey: key != nil) {
                let provider = make(id, url: url, key: key)
                steps.append((provider, model))
            }
        }
        if steps.isEmpty {
            throw AiError(L("NoAiUsable"))
        }
        return Failover(id: chain.joined(separator: "|"), steps: steps)
    }

    private static func make(_ id: String, url: String, key: String?) -> any Provider {
        switch id {
        case "anthropic": ClaudeApi(key: key)
        case "gemini": GeminiProvider(key: key)
        case "openai": OpenAiProvider(name: "OpenAI", baseUrl: url, key: key, limit: 90)
        default: OpenAiProvider(name: URL(string: url)?.host ?? url, baseUrl: url, key: key, limit: 300)
        }
    }

    static func models(_ s: Settings, id: String, urlText: String?, typedKey: String?) async throws -> [String] {
        // a key saved for one custom address is never sent to a newly typed one
        var key = typedKey
        if key == nil && !(id == "custom" && !s.sameHost(urlText)) {
            key = try keys(s, id).first
        }
        let url = try baseOf(id, urlText, withKey: key != nil)
        switch id {
        case "anthropic": return try await ClaudeApi.models(key: key!)
        case "gemini": return try await GeminiProvider.models(key: key!)
        case "openai": return try await OpenAiProvider.models(name: "OpenAI", baseUrl: url, key: key)
        default: return try await OpenAiProvider.models(name: URL(string: url)?.host ?? url, baseUrl: url, key: key)
        }
    }

    private static func keys(_ s: Settings, _ id: String) throws -> [String] {
        let keys = s.getKeys(id)
        if keys.isEmpty && s.hasKey(id) {
            throw AiError(L("SavedKeyUnusable"))
        }
        if keys.isEmpty && id != "custom" {
            throw AiError(L("NeedsKey"))
        }
        return keys
    }

    private static func baseOf(_ id: String, _ text: String?, withKey: Bool) throws -> String {
        if id != "custom" {
            return OpenAiProvider.defaultUrl
        }
        let (error, url) = Api.checkUrl(text ?? "", withKey: withKey)
        if let error {
            throw AiError(error)
        }
        return url
    }
}
