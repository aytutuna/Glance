import XCTest

final class SettingsTests: XCTestCase {
    var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("glance-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try FileManager.default.removeItem(at: dir)
    }

    private func write(_ name: String, _ json: String) throws -> URL {
        let url = dir.appendingPathComponent(name)
        try json.write(to: url, atomically: true, encoding: .utf8)
        return url
    }

    private func load(_ json: String) throws -> Settings {
        Settings.load(try write("settings.json", json))
    }

    func testMissingFileGivesDefaults() {
        let s = Settings.load(dir.appendingPathComponent("none.json"))
        XCTAssertEqual(s.provider, Settings.claude)
        XCTAssertEqual(s.textOpacity, 100)
        XCTAssertTrue(s.checkUpdates)
    }

    func testFieldsAddedLaterKeepTheirDefaults() throws {
        let s = try load(#"{"model":"claude-sonnet-5-5","centerAnswer":true}"#)
        XCTAssertEqual(s.model, "claude-sonnet-5-5")
        XCTAssertTrue(s.centerAnswer)
        XCTAssertEqual(s.textOpacity, 100)
        XCTAssertTrue(s.customModes.isEmpty)
        XCTAssertEqual(s.translateTo, "")
    }

    func testNullCollectionsAreReplaced() throws {
        let s = try load(#"{"customModes":null,"apiKeys":null,"order":null,"promptOverrides":null,"providerModels":null}"#)
        XCTAssertTrue(s.customModes.isEmpty)
        XCTAssertTrue(s.apiKeys.isEmpty)
        XCTAssertTrue(s.order.isEmpty)
    }

    func testBadValuesAreRepaired() throws {
        let s = try load(#"{"model":"gone","mode":"gone","provider":"openai","textOpacity":3,"useArea":true,"answerArea":[0.5,0.5,0.9,0.9]}"#)
        XCTAssertEqual(s.model, Settings.models[0].id)
        XCTAssertEqual(s.mode, "answer")
        XCTAssertEqual(s.provider, Settings.claude)
        XCTAssertEqual(s.textOpacity, 10)
        XCTAssertNil(s.answerArea)
        XCTAssertFalse(s.useArea)
    }

    func testCustomModeSurvivesAsTheMode() throws {
        let s = try load(#"{"mode":"custom:ab","customModes":[{"id":"custom:ab","name":"Haiku","prompt":"Write a haiku."}]}"#)
        XCTAssertEqual(s.mode, "custom:ab")
    }

    func testHotkeysNeverCollide() throws {
        let same = #"{"code":12,"mods":4608}"#
        let s = try load(#"{"hotkey":\#(same),"closeKey":\#(same),"regionKey":\#(same),"hideKey":{"code":0,"mods":0}}"#)
        let keys = [s.hotkey, s.closeKey, s.regionKey, s.hideKey]
        XCTAssertEqual(Set(keys.map { $0.code << 16 | $0.mods }).count, 4)
        XCTAssertTrue(keys.allSatisfy(\.isValid))
    }

    func testCorruptFileIsKeptAsBad() throws {
        let url = try write("settings.json", "{ not json")
        let s = Settings.load(url)
        XCTAssertEqual(s.provider, Settings.claude)
        XCTAssertEqual(try String(contentsOf: URL(fileURLWithPath: url.path + ".bad"), encoding: .utf8), "{ not json")
    }

    func testUnreadableFileBlocksSaving() throws {
        let url = try write("settings.json", #"{"centerAnswer":true}"#)
        try FileManager.default.setAttributes([.posixPermissions: 0], ofItemAtPath: url.path)
        let s = Settings.load(url)
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: url.path)
        XCTAssertFalse(s.save())
        XCTAssertEqual(try String(contentsOf: url, encoding: .utf8), #"{"centerAnswer":true}"#)
    }

    func testSavedSettingsLoadBack() throws {
        var s = Settings.load(dir.appendingPathComponent("new/settings.json"))
        s.centerAnswer = true
        s.customModes.append(CustomMode(id: "custom:1", name: "Haiku", prompt: "Write a haiku."))
        XCTAssertTrue(s.save())
        let back = Settings.load(dir.appendingPathComponent("new/settings.json"))
        XCTAssertTrue(back.centerAnswer)
        XCTAssertEqual(back.customModes, s.customModes)
        XCTAssertEqual(back.hotkey, s.hotkey)
    }

    func testAreaValidation() {
        XCTAssertTrue(Settings.validArea([0, 0, 1, 1]))
        XCTAssertTrue(Settings.validArea([0.1, 0.1, 0.5, 0.5]))
        XCTAssertFalse(Settings.validArea([0.6, 0, 0.5, 1]))
        XCTAssertFalse(Settings.validArea([0, 0, 0, 1]))
        XCTAssertFalse(Settings.validArea([0, 0, 1]))
    }
}
