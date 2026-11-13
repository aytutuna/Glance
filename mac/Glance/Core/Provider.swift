import Foundation

struct AiError: Error, CustomStringConvertible {
    let message: String
    var keyProblem = false

    init(_ message: String, keyProblem: Bool = false) {
        self.message = message
        self.keyProblem = keyProblem
    }

    var description: String { message }
}

protocol Provider: Sendable {
    func ask(_ chat: Conversation, onText: @escaping @Sendable (String) -> Void) async throws -> String
}

// a limit comes back eventually, so the first choice gets another try after this long
private actor Memory {
    static let shared = Memory()
    private var last: [String: (at: Int, since: ContinuousClock.Instant)] = [:]

    func start(_ id: String, count: Int) -> Int {
        if let l = last[id], l.since.duration(to: .now) >= .seconds(30 * 60) {
            last[id] = nil
        }
        return (last[id]?.at ?? 0) % count
    }

    func worked(_ id: String, at: Int) {
        if at == 0 {
            last[id] = nil
        } else if last[id]?.at != at {
            last[id] = (at, .now)
        }
    }
}

// Tries each option in turn without asking, for when a limit or a budget runs out.
// It starts from what last worked, so a spent option isn't retried on every read.
struct Failover: Provider {
    let id: String
    let steps: [(provider: any Provider, model: String)]

    func ask(_ chat: Conversation, onText: @escaping @Sendable (String) -> Void) async throws -> String {
        let start = await Memory.shared.start(id, count: steps.count)
        var i = 0
        while true {
            let at = (start + i) % steps.count
            chat.model = steps[at].model
            do {
                let answer = try await steps[at].provider.ask(chat, onText: onText)
                await Memory.shared.worked(id, at: at)
                return answer
            } catch let e as AiError where e.keyProblem && i + 1 < steps.count {
                // onText always carries the whole text, so the next option replaces any partial answer
                i += 1
            } catch let e as AiError where e.keyProblem && steps.count > 1 {
                throw AiError(L("AllFailed", steps.count, e.message))
            }
        }
    }
}

func withLimit<T: Sendable>(_ seconds: Double, name: String, _ body: @escaping @Sendable () async throws -> T) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await body() }
        group.addTask {
            try await Task.sleep(for: .seconds(seconds))
            throw AiError(L("TookTooLong", name))
        }
        defer { group.cancelAll() }
        return try await group.next()!
    }
}

private final class NoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

// the data payload of each server-sent event line; a line is capped so a bad server can't fill memory
struct EventLines {
    private var line: [UInt8] = []
    static let maxLine = 4 * 1024 * 1024

    mutating func feed(_ byte: UInt8) throws -> String? {
        if byte != 10 {
            if line.count >= EventLines.maxLine {
                throw AiError(L("LineTooLong"))
            }
            line.append(byte)
            return nil
        }
        defer { line.removeAll(keepingCapacity: true) }
        return data(line)
    }

    func finish() -> String? {
        data(line)
    }

    private func data(_ bytes: [UInt8]) -> String? {
        var text = String(decoding: bytes, as: UTF8.self)
        if text.hasSuffix("\r") {
            text.removeLast()
        }
        guard text.hasPrefix("data:") else { return nil }
        text.removeFirst(5)
        if text.hasPrefix(" ") {
            text.removeFirst()
        }
        return text
    }
}

enum Api {
    // redirects are never followed, since a custom header such as x-goog-api-key would go along with them
    static let session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 600
        return URLSession(configuration: config, delegate: NoRedirect(), delegateQueue: nil)
    }()

    // heads replaces the generic text for a status: 0 can't reach, 300 redirect, or an exact code
    static func send(_ req: URLRequest, name: String, key: String?, heads: [Int: String] = [:]) async throws -> URLSession.AsyncBytes {
        let bytes: URLSession.AsyncBytes
        let resp: URLResponse
        do {
            (bytes, resp) = try await session.bytes(for: req)
        } catch {
            throw unreachable(error, name: name, heads: heads)
        }
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if (200..<300).contains(code) {
            return bytes
        }
        var body = Data()
        if code != 401 && !(300..<400).contains(code) {
            do {
                for try await b in bytes {
                    body.append(b)
                    if body.count >= 16 * 1024 {
                        break
                    }
                }
            } catch {
            }
        }
        throw failure(code, name: name, key: key, body: body, heads: heads)
    }

    static func fetch(_ req: URLRequest, name: String, key: String?, heads: [Int: String] = [:]) async throws -> Data {
        let data: Data
        let resp: URLResponse
        do {
            (data, resp) = try await session.data(for: req)
        } catch {
            throw unreachable(error, name: name, heads: heads)
        }
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if (200..<300).contains(code) {
            if data.count > 4 * 1024 * 1024 {
                throw AiError(L("NoModelList", name))
            }
            return data
        }
        throw failure(code, name: name, key: key, body: data.prefix(16 * 1024), heads: heads)
    }

    private static func unreachable(_ error: Error, name: String, heads: [Int: String]) -> Error {
        if error is CancellationError || (error as? URLError)?.code == .cancelled {
            return CancellationError()
        }
        if (error as? URLError)?.code == .timedOut {
            return AiError(L("TookTooLong", name))
        }
        return AiError(heads[0] ?? L("CantReach", name))
    }

    private static func failure(_ code: Int, name: String, key: String?, body: Data, heads: [Int: String]) -> AiError {
        if (300..<400).contains(code) {
            return AiError(heads[300] ?? L("Redirect", name))
        }
        let generic = switch code {
        case 401: L("Http401", name)
        case 403: L("Http403", name)
        case 404: L("Http404", name)
        case 429: L("Http429", name)
        case 500...: L("HttpServer", name, code)
        default: L("HttpOther", name, code)
        }
        // providers can echo part of a bad key back, so a 401 gets no detail
        var text = heads[code] ?? generic
        if code != 401, let json = try? JSONSerialization.jsonObject(with: body), let detail = message(json, key) {
            text += " " + detail
        }
        // a custom provider's name is its host, which must not match the words in spent()
        return AiError(text, keyProblem: spent(code, text.replacingOccurrences(of: name, with: "")))
    }

    // out of credit, rate limited or dead: another key may still work
    static func spent(_ code: Int, _ text: String) -> Bool {
        [401, 402, 403, 429].contains(code) || code == 400 && spentWords(text)
    }

    static func spentWords(_ text: String) -> Bool {
        ["credit", "api key", "quota", "rate limit"].contains { text.localizedCaseInsensitiveContains($0) }
    }

    // the text of an error object, one short plain line, or nil
    static func message(_ json: Any, _ key: String?) -> String? {
        if let list = json as? [Any], let first = list.first {
            return message(first, key)
        }
        guard let e = json as? [String: Any] else { return nil }
        var text: String?
        if let err = e["error"] {
            text = err as? String ?? (err as? [String: Any])?["message"] as? String
        } else {
            text = e["message"] as? String
        }
        return tidy(text, key)
    }

    static func tidy(_ text: String?, _ key: String?) -> String? {
        guard var text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        if let key, !key.isEmpty {
            text = text.replacingOccurrences(of: key, with: "***")
        }
        text = text.replacingOccurrences(of: #"[\s\p{C}]+"#, with: " ", options: .regularExpression)
            .trimmingCharacters(in: .whitespaces)
        return text.count > 200 ? text.prefix(197) + "..." : text
    }

    static func checkUrl(_ text: String, withKey: Bool) -> (error: String?, url: String) {
        var url = text.trimmingCharacters(in: .whitespacesAndNewlines)
        while url.hasSuffix("/") {
            url.removeLast()
        }
        guard let c = URLComponents(string: url), let scheme = c.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = c.host, !host.isEmpty else { return (L("BaseUrlScheme"), url) }
        if c.user != nil || c.password != nil {
            return (L("BaseUrlUser"), url)
        }
        if c.query != nil || c.fragment != nil {
            return (L("BaseUrlQuery"), url)
        }
        if withKey && scheme == "http" && !loopback(host) {
            return (L("BaseUrlPlain"), url)
        }
        return (nil, url)
    }

    private static func loopback(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        let v4 = parts.count == 4 && parts.allSatisfy { UInt8($0) != nil }
        return host.lowercased() == "localhost" || host == "::1" || host == "[::1]" || v4 && parts[0] == "127"
    }

    static func checkKey(_ key: String) -> String? {
        key.unicodeScalars.contains { $0.value <= 32 || $0.value > 126 } ? L("BadKeyChars") : nil
    }
}
