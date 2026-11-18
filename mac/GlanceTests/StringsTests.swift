import XCTest

final class StringsTests: XCTestCase {
    func testEveryKeyIsTranslatedWithTheSameFormats() throws {
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Glance/Localizable.xcstrings")
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        let strings = json["strings"] as! [String: Any]
        let languages = ["en", "tr", "ar", "de", "es", "fr", "hi", "id", "it", "ja", "ko", "nl", "pl", "pt-BR", "ru", "zh-Hans"]
        let format = try NSRegularExpression(pattern: #"%(\d+\$)?l*[@dfsu]"#)
        func specifiers(_ text: String) -> [String] {
            format.matches(in: text, range: NSRange(text.startIndex..., in: text)).map { String(text[Range($0.range, in: text)!]) }.sorted()
        }
        XCTAssertFalse(strings.isEmpty)
        for (key, entry) in strings {
            let localizations = (entry as! [String: Any])["localizations"] as! [String: [String: [String: String]]]
            let english = specifiers(localizations["en"]!["stringUnit"]!["value"]!)
            for language in languages {
                guard let unit = localizations[language]?["stringUnit"], let value = unit["value"] else {
                    XCTFail("\(key) has no \(language)")
                    continue
                }
                XCTAssertEqual(unit["state"], "translated", "\(key) \(language)")
                XCTAssertEqual(specifiers(value), english, "\(key) \(language)")
            }
        }
    }
}
