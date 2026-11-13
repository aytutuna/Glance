import Carbon.HIToolbox
import Foundation

struct CustomMode: Codable, Equatable {
    var id = "custom:" + shortId()
    var name = ""
    var prompt = ""
}

func shortId() -> String {
    String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(8)).lowercased()
}

// key code plus Carbon modifier flags, as RegisterEventHotKey wants them
struct Hotkey: Codable, Equatable {
    var code: UInt32
    var mods: UInt32

    static let ctrlShift = UInt32(controlKey | shiftKey)
    static let fKeys = Set([
        kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
        kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20,
    ].map { UInt32($0) })

    var isValid: Bool {
        mods & UInt32(controlKey | optionKey | cmdKey) != 0 || Hotkey.fKeys.contains(code)
    }

    // when a saved key is unusable or taken, the next free control-shift letter takes its place
    static func distinct(_ key: Hotkey, taken: [Hotkey]) -> Hotkey {
        if key.isValid && !taken.contains(key) {
            return key
        }
        for letter in [kVK_ANSI_X, kVK_ANSI_D, kVK_ANSI_W, kVK_ANSI_E, kVK_ANSI_R, kVK_ANSI_A, kVK_ANSI_S, kVK_ANSI_F] {
            let next = Hotkey(code: UInt32(letter), mods: ctrlShift)
            if !taken.contains(next) {
                return next
            }
        }
        return key
    }
}

struct Settings: Codable {
    static let models = [
        (id: "claude-opus-5-5", name: "Opus 5.5"),
        (id: "claude-sonnet-5-5", name: "Sonnet 5.5"),
        (id: "claude-haiku-5-5", name: "Haiku 5.5"),
    ]

    static var modes: [(id: String, name: String)] {
        [("answer", L("ModeAnswer")), ("explain", L("ModeExplain")), ("translate", L("ModeTranslate")), ("summarize", L("ModeSummarize"))]
    }

    static let languages = [
        "English", "Turkish", "Spanish", "French", "German", "Italian", "Portuguese",
        "Russian", "Arabic", "Chinese (Simplified)", "Japanese", "Korean", "Hindi",
    ]

    static let claude = "claude"

    static var providers: [(id: String, name: String)] {
        [(claude, "Claude CLI"), ("anthropic", "Claude API"), ("openai", "OpenAI"), ("gemini", "Gemini"), ("custom", L("CustomName"))]
    }

    static let file = AppDir.url.appendingPathComponent("settings.json")

    // display UUIDs of the screens
    var readDevice: String?
    var showDevice: String?
    var centerAnswer = false
    var plainAnswer = false
    var textOpacity = 100
    var useArea = false
    var keepHistory = false
    var checkUpdates = true
    var lastUpdateCheck = Date(timeIntervalSince1970: 0)
    // left, top, width, height as fractions of the screen, so it survives a resolution change
    var answerArea: [Double]?
    var hotkey = Hotkey(code: UInt32(kVK_ANSI_Q), mods: Hotkey.ctrlShift)
    var closeKey = Hotkey(code: UInt32(kVK_ANSI_X), mods: Hotkey.ctrlShift)
    var regionKey = Hotkey(code: UInt32(kVK_ANSI_D), mods: Hotkey.ctrlShift)
    var hideKey = Hotkey(code: UInt32(kVK_ANSI_B), mods: Hotkey.ctrlShift)
    var model = models[0].id
    var mode = "answer"
    // English name of the language to translate into; empty follows the system language
    var translateTo = ""
    var provider = claude
    var customModes: [CustomMode] = []
    // edited prompts of the built-in modes by mode id; a missing entry means the default text
    var promptOverrides: [String: String] = [:]
    var customUrl: String?
    var providerModels: [String: String] = [:]
    // the keys themselves are in the Keychain under the account "<provider>:<tag>"
    var apiKeys: [String: [String]] = [:]
    // tried from the top: "claude" is the CLI, provider:tag is one saved key, a bare provider a keyless one
    var order: [String] = []

    // the file exists but couldn't be read, so saving now would overwrite saved settings
    private var unread = false
    private var path = Settings.file

    enum CodingKeys: String, CodingKey {
        case readDevice, showDevice, centerAnswer, plainAnswer, textOpacity, useArea, keepHistory, checkUpdates, lastUpdateCheck
        case answerArea, hotkey, closeKey, regionKey, hideKey, model, mode, translateTo, provider, customModes, promptOverrides
        case customUrl, providerModels, apiKeys, order
    }

    init() {}

    init(from decoder: Decoder) throws {
        self.init()
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func get<T: Decodable>(_ key: CodingKeys, _ value: inout T) throws {
            if let found = try c.decodeIfPresent(T.self, forKey: key) {
                value = found
            }
        }
        try get(.readDevice, &readDevice)
        try get(.showDevice, &showDevice)
        try get(.centerAnswer, &centerAnswer)
        try get(.plainAnswer, &plainAnswer)
        try get(.textOpacity, &textOpacity)
        try get(.useArea, &useArea)
        try get(.keepHistory, &keepHistory)
        try get(.checkUpdates, &checkUpdates)
        try get(.lastUpdateCheck, &lastUpdateCheck)
        try get(.answerArea, &answerArea)
        try get(.hotkey, &hotkey)
        try get(.closeKey, &closeKey)
        try get(.regionKey, &regionKey)
        try get(.hideKey, &hideKey)
        try get(.model, &model)
        try get(.mode, &mode)
        try get(.translateTo, &translateTo)
        try get(.provider, &provider)
        try get(.customModes, &customModes)
        try get(.promptOverrides, &promptOverrides)
        try get(.customUrl, &customUrl)
        try get(.providerModels, &providerModels)
        try get(.apiKeys, &apiKeys)
        try get(.order, &order)
    }

    static func validArea(_ a: [Double]?) -> Bool {
        guard let a, a.count == 4 else { return false }
        return a.allSatisfy { $0 >= 0 && $0 <= 1 } && a[2] > 0 && a[3] > 0 && a[0] + a[2] <= 1.001 && a[1] + a[3] <= 1.001
    }

    static func load(_ url: URL = file) -> Settings {
        var fresh = Settings()
        fresh.path = url
        guard FileManager.default.fileExists(atPath: url.path) else { return fresh }
        guard let data = try? Data(contentsOf: url) else {
            fresh.unread = true
            return fresh
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        var s: Settings
        do {
            s = try decoder.decode(Settings.self, from: data)
        } catch {
            let bad = URL(fileURLWithPath: url.path + ".bad")
            try? FileManager.default.removeItem(at: bad)
            do {
                try FileManager.default.copyItem(at: url, to: bad)
            } catch {
                fresh.unread = true
            }
            return fresh
        }
        s.path = url
        if !s.hotkey.isValid {
            s.hotkey = Settings().hotkey
        }
        s.closeKey = Hotkey.distinct(s.closeKey, taken: [s.hotkey])
        s.regionKey = Hotkey.distinct(s.regionKey, taken: [s.hotkey, s.closeKey])
        s.hideKey = Hotkey.distinct(s.hideKey, taken: [s.hotkey, s.closeKey, s.regionKey])
        if !models.contains(where: { $0.id == s.model }) {
            s.model = models[0].id
        }
        if !s.allModes().contains(where: { $0.id == s.mode }) {
            s.mode = "answer"
        }
        if !validArea(s.answerArea) {
            s.answerArea = nil
            s.useArea = false
        }
        s.textOpacity = min(max(s.textOpacity, 10), 100)
        if !providers.contains(where: { $0.id == s.provider }) || !s.configured(s.provider) {
            s.provider = claude
        }
        return s
    }

    func save() -> Bool {
        if unread {
            return false
        }
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        do {
            try FileManager.default.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
            try encoder.encode(self).write(to: path, options: .atomic)
            return true
        } catch {
            return false
        }
    }

    func allModes() -> [(id: String, name: String)] {
        Settings.modes + customModes.map { ($0.id, $0.name) }
    }

    func options() -> [(entry: String, label: String)] {
        var list = [(entry: Settings.claude, label: "Claude CLI")]
        for (id, name) in Settings.providers.dropFirst() {
            if id != "anthropic" && !configured(id) {
                continue
            }
            let tags = apiKeys[id] ?? []
            if tags.isEmpty && id == "custom" {
                let host = customUrl.flatMap { URLComponents(string: $0)?.host }
                list.append((id, host.map { L("CustomHost", $0) } ?? L("CustomName")))
            }
            for tag in tags {
                let end: String
                if let key = Keychain.get("\(id):\(tag)") {
                    end = key.count >= 12 ? L("OptKeyEnding", key.suffix(4)) : L("OptKey")
                } else {
                    end = L("OptKeyUnreadable")
                }
                list.append(("\(id):\(tag)", "\(name), \(end)"))
            }
        }
        return list
    }

    // pass the options when they were just read, since each one is a Keychain lookup
    func chain(_ options: [(entry: String, label: String)]? = nil) -> [String] {
        let known = (options ?? self.options()).map(\.entry)
        var seen = Set<String>()
        let chain = order.filter { known.contains($0) && seen.insert($0).inserted }
        if !chain.isEmpty {
            return chain
        }
        // until an order is saved: the CLI, then each Claude API key
        return known.filter { $0 == Settings.claude || $0.hasPrefix("anthropic:") }
    }

    // a newly connected AI goes to the end of the order
    mutating func include(_ entry: String) {
        if order.isEmpty {
            order = chain()
        }
        if !order.contains(entry) {
            order.append(entry)
        }
    }

    func keyFor(_ entry: String) -> String? {
        entry.contains(":") ? Keychain.get(entry) : nil
    }

    func getKeys(_ id: String) -> [String] {
        (apiKeys[id] ?? []).compactMap { Keychain.get("\(id):\($0)") }
    }

    func hasKey(_ id: String) -> Bool {
        !(apiKeys[id] ?? []).isEmpty
    }

    func keyNames(_ id: String) -> [String] {
        (apiKeys[id] ?? []).enumerated().map { i, tag in
            guard let key = Keychain.get("\(id):\(tag)") else { return L("SavedKeyUnreadable", i + 1) }
            return key.count >= 12 ? L("SavedKeyEnding", i + 1, key.suffix(4)) : L("SavedKey", i + 1)
        }
    }

    func sameHost(_ url: String?) -> Bool {
        func authority(_ text: String?) -> String? {
            guard let text, let c = URLComponents(string: text.trimmingCharacters(in: .whitespaces)),
                  let scheme = c.scheme, let host = c.host else { return nil }
            return "\(host.lowercased()):\(c.port ?? (scheme == "https" ? 443 : 80))"
        }
        guard let a = authority(url), let b = authority(customUrl) else { return false }
        return a == b
    }

    mutating func addKey(_ id: String, _ key: String) throws {
        if getKeys(id).contains(key) {
            return
        }
        let tag = shortId()
        try Keychain.set("\(id):\(tag)", key)
        apiKeys[id, default: []].append(tag)
        include("\(id):\(tag)")
    }

    mutating func removeKey(_ id: String, at index: Int) {
        let entry = "\(id):\(apiKeys[id]![index])"
        Keychain.delete(entry)
        order.removeAll { $0 == entry }
        apiKeys[id]!.remove(at: index)
        if apiKeys[id]!.isEmpty {
            apiKeys[id] = nil
        }
        if !configured(provider) {
            provider = Settings.claude
        }
    }

    func activeModel() -> String {
        provider == Settings.claude ? model : providerModels[provider] ?? ""
    }

    func configured(_ id: String) -> Bool {
        if id == Settings.claude {
            return true
        }
        guard let model = providerModels[id], !model.isEmpty else { return false }
        return id == "custom" ? !(customUrl ?? "").isEmpty : hasKey(id)
    }
}
