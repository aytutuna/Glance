import Foundation

enum Updates {
    static let api = URL(string: "https://api.github.com/repos/aytutuna/Glance/releases/latest")!

    // major.minor with up to two more numbers, after an optional leading v
    static func parse(_ tag: String) -> [Int]? {
        let parts = tag.drop { $0 == "v" }.split(separator: ".", omittingEmptySubsequences: false)
        let numbers = parts.compactMap { $0.allSatisfy { ("0"..."9").contains($0) } ? Int($0) : nil }
        return (2...4).contains(parts.count) && numbers.count == parts.count ? numbers : nil
    }

    static func isNewer(_ tag: String, than current: String) -> Bool {
        guard var a = parse(tag), var b = parse(current) else { return false }
        while a.count < 4 { a.append(0) }
        while b.count < 4 { b.append(0) }
        return b.lexicographicallyPrecedes(a)
    }

    static func trusted(_ url: String) -> Bool {
        url.hasPrefix("https://github.com/aytutuna/Glance/")
    }

    static func latest() async throws -> (tag: String, url: String) {
        var req = URLRequest(url: api, timeoutInterval: 10)
        req.setValue("Glance", forHTTPHeaderField: "User-Agent")
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard (resp as? HTTPURLResponse)?.statusCode == 200,
              let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String, let url = json["html_url"] as? String else { throw URLError(.badServerResponse) }
        return (tag, url)
    }
}
