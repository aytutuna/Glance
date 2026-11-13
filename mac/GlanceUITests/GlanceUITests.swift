import AppKit
import XCTest

// Needs fake_ai.py on port 8123 (its /reset also restores the settings file) and Screen Recording granted to Glance; the CI job sets both up.
@MainActor
final class GlanceUITests: XCTestCase {
    let app = XCUIApplication()
    let server = URL(string: "http://127.0.0.1:8123")!
    let ctrlShift: XCUIElement.KeyModifierFlags = [.control, .shift]

    override func setUp() async throws {
        continueAfterFailure = false
        _ = try await URLSession.shared.data(for: post("reset"))
        app.launch()
    }

    override func tearDown() async throws {
        app.terminate()
    }

    func post(_ path: String) -> URLRequest {
        var req = URLRequest(url: server.appendingPathComponent(path))
        req.httpMethod = "POST"
        return req
    }

    func requests() async throws -> [[String: Any]] {
        let (data, _) = try await URLSession.shared.data(from: server.appendingPathComponent("requests"))
        return try JSONSerialization.jsonObject(with: data) as? [[String: Any]] ?? []
    }

    func shot(_ name: String, of element: XCUIElement? = nil) {
        let picture = XCTAttachment(screenshot: element?.screenshot() ?? XCUIScreen.main.screenshot())
        picture.name = name
        picture.lifetime = .keepAlways
        add(picture)
    }

    func wait(_ what: String, timeout: TimeInterval = 20, until done: () -> Bool) {
        let end = Date().addingTimeInterval(timeout)
        while !done() {
            if Date() > end {
                shot("timeout " + what)
                XCTFail("timed out waiting for \(what)\n\(app.debugDescription)")
                return
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        }
    }

    var answer: XCUIElement { app.staticTexts["answer"] }

    var shown: String { answer.exists ? answer.value as? String ?? "" : "" }

    func waitForAnswer(_ text: String) {
        wait("answer containing \(text)") { shown.contains(text) }
    }

    // the byte count the fake server put into the first answer
    func imageBytes() -> Int {
        let digits = shown.components(separatedBy: " ").compactMap { Int($0) }
        return digits.first ?? 0
    }

    func openMenu() {
        let item = app.statusItems.firstMatch
        XCTAssertTrue(item.waitForExistence(timeout: 10), app.debugDescription)
        item.click()
    }

    func choose(_ titles: String...) {
        for title in titles {
            let entry = app.menuItems[title]
            XCTAssertTrue(entry.waitForExistence(timeout: 5), "no menu item \(title)\n\(app.debugDescription)")
            entry.click()
        }
    }

    func press(_ key: String) {
        app.typeKey(key, modifierFlags: ctrlShift)
    }

    func readFromMenu() {
        openMenu()
        choose("Read screen now")
    }

    func testMenuItems() {
        openMenu()
        wait("the menu to open") { app.menuItems["Quit Glance"].isHittable }
        for title in ["Read screen now", "Read an area", "Hide answer text", "Answer position", "Quit Glance"] {
            XCTAssertTrue(app.menuItems[title].exists, title)
        }
        shot("menu")
        app.typeKey(.escape, modifierFlags: [])
        wait("the menu to close") { !app.menuItems["Quit Glance"].isHittable }
    }

    func testReadFromMenu() async throws {
        readFromMenu()
        waitForAnswer("bytes.")
        XCTAssertGreaterThan(imageBytes(), 0)
        wait("answer to finish") { app.buttons["copy"].isEnabled }
        shot("answer from the menu")
        let sent = try await requests()
        XCTAssertEqual(sent.count, 1)
        XCTAssertEqual(sent.first?["image"] as? Int, imageBytes())
    }

    func testReadAndCloseWithKeys() {
        press("q")
        waitForAnswer("bytes.")
        shot("answer from the key")
        press("x")
        wait("popup to close") { !answer.exists }
        shot("closed")
    }

    func testFollowUp() async throws {
        press("q")
        waitForAnswer("bytes.")
        let first = shown
        let field = app.textFields["followup"]
        XCTAssertTrue(field.waitForExistence(timeout: 10))
        field.click()
        field.typeText("What is the second line?")
        field.typeKey(.return, modifierFlags: [])
        waitForAnswer("Follow-up 1: What is the second line?")
        XCTAssertTrue(shown.contains(first))
        XCTAssertTrue(shown.contains("Q: What is the second line?"))
        shot("follow-up")
        let sent = try await requests()
        XCTAssertEqual(sent.count, 2)
        XCTAssertEqual(sent.last?["roles"] as? [String], ["system", "user", "assistant", "user"])
    }

    func testCopy() {
        press("q")
        waitForAnswer("bytes.")
        let copy = app.buttons["copy"]
        wait("copy to be enabled") { copy.isEnabled }
        let expected = shown
        copy.click()
        wait("the pasteboard") { NSPasteboard.general.string(forType: .string) == expected }
        shot("copied")
    }

    func testHide() {
        press("q")
        waitForAnswer("bytes.")
        wait("answer to finish") { app.buttons["copy"].isEnabled }
        press("b")
        wait("the text to go") { shown.isEmpty }
        shot("hidden")
    }

    func testEscapeClosesThePicker() async throws {
        press("d")
        let picker = app.groups["picker"]
        XCTAssertTrue(picker.waitForExistence(timeout: 10), app.debugDescription)
        shot("picker")
        app.typeKey(.escape, modifierFlags: [])
        wait("the picker to close") { !picker.exists }
        try await Task.sleep(for: .seconds(1))
        let sent = try await requests()
        XCTAssertEqual(sent.count, 0)
    }

    func testCenterPosition() {
        openMenu()
        choose("Answer position", "Center of the screen")
        press("q")
        waitForAnswer("bytes.")
        let screen = NSScreen.screens[0].frame
        XCTAssertEqual(answer.frame.midX, screen.midX, accuracy: 30)
        XCTAssertEqual(answer.frame.midY, screen.midY, accuracy: 60)
        shot("centered")
    }

    func testPlainBlack() {
        openMenu()
        choose("Plain black answer")
        press("q")
        waitForAnswer("bytes.")
        let screen = NSScreen.screens[0].frame
        XCTAssertEqual(answer.frame.width, screen.width, accuracy: 2)
        XCTAssertEqual(answer.frame.height, screen.height, accuracy: 2)
        shot("plain black")
    }

    func testAreaRead() {
        press("q")
        waitForAnswer("bytes.")
        wait("answer to finish") { app.buttons["copy"].isEnabled }
        let full = imageBytes()
        press("x")
        wait("popup to close") { !answer.exists }

        press("d")
        let picker = app.groups["picker"]
        wait("the picker") { picker.exists }
        shot("picker")
        let from = picker.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.2))
        from.press(forDuration: 0.3, thenDragTo: picker.coordinate(withNormalizedOffset: CGVector(dx: 0.45, dy: 0.4)))
        waitForAnswer("bytes.")
        let part = imageBytes()
        XCTAssertGreaterThan(part, 0)
        XCTAssertLessThan(part, full)
        shot("area answer")
    }

    func replace(_ field: XCUIElement, with text: String) {
        XCTAssertTrue(field.waitForExistence(timeout: 10), app.debugDescription)
        field.click()
        field.typeKey("a", modifierFlags: .command)
        field.typeText(text)
    }

    func dialog(_ title: String) -> XCUIElement {
        let window = app.windows[title]
        XCTAssertTrue(window.waitForExistence(timeout: 10), "no window \(title)\n\(app.debugDescription)")
        return window
    }

    func closes(_ window: XCUIElement) {
        wait("the window to close") { !window.exists }
    }

    func tab(_ name: String, in window: XCUIElement) -> XCUIElement {
        for query in [window.radioButtons, window.tabs, window.buttons] where query[name].exists {
            return query[name]
        }
        return window.radioButtons[name]
    }

    // opens the AI submenu and looks for an entry in it
    func chainItem(_ title: String) {
        openMenu()
        choose("AI")
        XCTAssertTrue(app.menuItems[title].waitForExistence(timeout: 5), "no menu item \(title)\n\(app.debugDescription)")
        app.typeKey(.escape, modifierFlags: [])
        app.typeKey(.escape, modifierFlags: [])
    }

    func testSettingsWindowHasTheTabs() {
        openMenu()
        choose("Settings...")
        let window = dialog("Glance settings")
        for name in ["Reading", "Answer", "Keys", "AI", "General"] {
            XCTAssertTrue(tab(name, in: window).waitForExistence(timeout: 5), "no tab \(name)\n\(app.debugDescription)")
        }
        tab("Keys", in: window).click()
        wait("the four Change buttons") { window.buttons.matching(identifier: "Change...").count == 4 }
        tab("General", in: window).click()
        XCTAssertTrue(window.switches["Open at login"].waitForExistence(timeout: 5), app.debugDescription)
        shot("settings", of: window)
        window.buttons["Close"].click()
        closes(window)
    }

    func testConnectSavesTheCustomAddress() {
        openMenu()
        choose("AI", "Connect an AI...")
        let window = dialog("Connect an AI")
        replace(app.textFields["connect.url"], with: "http://localhost:8123/v1")
        shot("connect", of: window)
        app.buttons["Save"].click()
        closes(window)
        chainItem("1. Custom (localhost)")
    }

    func testConnectRefusesABadAddress() {
        openMenu()
        choose("AI", "Connect an AI...")
        let window = dialog("Connect an AI")
        replace(app.textFields["connect.url"], with: "ftp://example.com")
        app.buttons["Save"].click()
        let status = app.staticTexts["connect.status"]
        wait("the address to be refused") { (status.value as? String ?? status.label).contains("http://") }
        XCTAssertTrue(window.exists)
        app.buttons["Cancel"].click()
        closes(window)
    }

    func testNewModeAppearsAndIsUsed() async throws {
        openMenu()
        choose("Mode", "Edit modes...")
        let window = dialog("Edit modes")
        window.buttons["Add"].click()
        replace(app.textFields["modes.name"], with: "Pirate")
        let prompt = window.textViews.firstMatch
        prompt.click()
        prompt.typeText("Answer like a pirate.")
        shot("modes", of: window)
        window.buttons["OK"].click()
        closes(window)
        openMenu()
        choose("Mode", "Pirate")
        press("q")
        waitForAnswer("bytes.")
        let sent = try await requests()
        XCTAssertEqual(sent.last?["last"] as? String, "Follow your instructions for this screenshot.")
    }

    func testOrderMovesAnEntry() {
        openMenu()
        choose("AI", "Change order...")
        let window = dialog("AI order")
        let cli = window.checkBoxes["Claude CLI"]
        XCTAssertTrue(cli.waitForExistence(timeout: 5), app.debugDescription)
        cli.click()
        window.buttons["Up"].click()
        shot("order", of: window)
        window.buttons["Save"].click()
        closes(window)
        chainItem("1. Claude CLI")
        chainItem("2. Custom (127.0.0.1)")
    }

    func testRecorderChangesTheReadKey() async throws {
        openMenu()
        choose("Read key (\u{2303}\u{21E7}Q)...")
        let window = dialog("Read key")
        shot("recorder", of: window)
        app.typeKey("r", modifierFlags: [.control, .option])
        closes(window)
        openMenu()
        XCTAssertTrue(app.menuItems["Read key (\u{2303}\u{2325}R)..."].waitForExistence(timeout: 5), app.debugDescription)
        app.typeKey(.escape, modifierFlags: [])
        press("q")
        try await Task.sleep(for: .seconds(2))
        let none = try await requests()
        XCTAssertEqual(none.count, 0)
        app.typeKey("r", modifierFlags: [.control, .option])
        waitForAnswer("bytes.")
    }

    func testRecorderRefusesKeys() {
        openMenu()
        choose("Read key (\u{2303}\u{21E7}Q)...")
        let window = dialog("Read key")
        let status = app.staticTexts["recorder.status"]
        press("x")
        wait("the taken key to be refused") { (status.value as? String ?? status.label).contains("already the Close key") }
        app.typeKey("a", modifierFlags: [])
        wait("the bare key to be refused") { (status.value as? String ?? status.label).contains("F-key") }
        shot("recorder refusing", of: window)
        app.typeKey(.escape, modifierFlags: [])
        closes(window)
    }

    func tour(_ language: String, _ words: [String: String], tabs: [String]) {
        if language == "tr" {
            app.terminate()
            app.launchArguments = ["-AppleLanguages", "(tr)", "-AppleLocale", "tr_TR"]
            app.launch()
        }
        func look(_ name: String, at window: XCUIElement) {
            shot("\(language)-\(name)", of: window)
        }

        openMenu()
        choose(words["settings"]!)
        let settings = dialog(words["settingsTitle"]!)
        for name in tabs {
            let item = tab(name, in: settings)
            XCTAssertTrue(item.waitForExistence(timeout: 5), "no tab \(name)\n\(app.debugDescription)")
            item.click()
            look("settings-\(name)", at: settings)
        }
        settings.buttons[words["close"]!].click()
        closes(settings)

        openMenu()
        choose(words["ai"]!, words["connect"]!)
        let connect = dialog(words["connectTitle"]!)
        look("connect", at: connect)
        app.popUpButtons["connect.provider"].click()
        app.menuItems["Claude API"].click()
        look("connect-api", at: connect)
        app.buttons[words["cancel"]!].click()
        closes(connect)

        openMenu()
        choose(words["ai"]!, words["order"]!)
        let order = dialog(words["orderTitle"]!)
        look("order", at: order)
        app.buttons[words["cancel"]!].click()
        closes(order)

        openMenu()
        choose(words["mode"]!, words["modes"]!)
        let modes = dialog(words["modesTitle"]!)
        look("modes", at: modes)
        app.buttons[words["cancel"]!].click()
        closes(modes)

        openMenu()
        choose(words["readKey"]!)
        let recorder = dialog(words["readKeyTitle"]!)
        look("recorder", at: recorder)
        app.buttons[words["cancel"]!].click()
        closes(recorder)
    }

    func testDialogsInEnglish() {
        tour("en", [
            "settings": "Settings...", "settingsTitle": "Glance settings", "close": "Close", "cancel": "Cancel",
            "ai": "AI", "connect": "Connect an AI...", "connectTitle": "Connect an AI",
            "order": "Change order...", "orderTitle": "AI order",
            "mode": "Mode", "modes": "Edit modes...", "modesTitle": "Edit modes",
            "readKey": "Read key (\u{2303}\u{21E7}Q)...", "readKeyTitle": "Read key",
        ], tabs: ["Reading", "Answer", "Keys", "AI", "General"])
    }

    func testDialogsInTurkish() {
        tour("tr", [
            "settings": "Ayarlar...", "settingsTitle": "Glance ayarlar\u{0131}", "close": "Kapat", "cancel": "\u{0130}ptal",
            "ai": "Yapay zek\u{00E2}", "connect": "Yapay zek\u{00E2} ba\u{011F}la...", "connectTitle": "Yapay zek\u{00E2} ba\u{011F}la",
            "order": "S\u{0131}ray\u{0131} de\u{011F}i\u{015F}tir...", "orderTitle": "Yapay zek\u{00E2} s\u{0131}ras\u{0131}",
            "mode": "Mod", "modes": "Modlar\u{0131} d\u{00FC}zenle...", "modesTitle": "Modlar\u{0131} d\u{00FC}zenle",
            "readKey": "Okuma tu\u{015F}u (\u{2303}\u{21E7}Q)...", "readKeyTitle": "Okuma tu\u{015F}u",
        ], tabs: ["Okuma", "Yan\u{0131}t", "Tu\u{015F}lar", "Yapay zek\u{00E2}", "Genel"])
    }
}
