import CryptoKit
import Foundation

// The last answers, kept only when the user asks for it. The key lives in the Keychain, the file in the support folder.
enum History {
    static let limit = 10
    static let file = AppDir.url.appendingPathComponent("history.dat")
    private static let account = "history"

    static func seal(_ answers: [String], with key: SymmetricKey) throws -> Data {
        try AES.GCM.seal(JSONEncoder().encode(answers), using: key).combined!
    }

    static func open(_ data: Data, with key: SymmetricKey) -> [String]? {
        guard let box = try? AES.GCM.SealedBox(combined: data), let plain = try? AES.GCM.open(box, using: key),
              let answers = try? JSONDecoder().decode([String].self, from: plain) else { return nil }
        return Array(answers.prefix(limit))
    }

    static func load() -> [String] {
        guard let text = Keychain.get(account), let raw = Data(base64Encoded: text), let data = try? Data(contentsOf: file) else { return [] }
        return open(data, with: SymmetricKey(data: raw)) ?? []
    }

    static func save(_ answers: [String]) {
        do {
            let key = try loadOrMakeKey()
            try FileManager.default.createDirectory(at: AppDir.url, withIntermediateDirectories: true)
            try seal(answers, with: key).write(to: file, options: .atomic)
        } catch {
            Log.write("history not saved", error)
        }
    }

    static func delete() {
        try? FileManager.default.removeItem(at: file)
        Keychain.delete(account)
    }

    private static func loadOrMakeKey() throws -> SymmetricKey {
        if let text = Keychain.get(account), let raw = Data(base64Encoded: text) {
            return SymmetricKey(data: raw)
        }
        let key = SymmetricKey(size: .bits256)
        try Keychain.set(account, key.withUnsafeBytes { Data($0).base64EncodedString() })
        return key
    }

    // one line for the menu
    static func preview(_ answer: String) -> String {
        let line = answer.replacingOccurrences(of: #"\s+"#, with: " ", options: .regularExpression)
        return line.count > 60 ? line.prefix(57) + "..." : line
    }
}
