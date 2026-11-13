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

    func shot(_ name: String) {
        let picture = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
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
        let picker = app.otherElements["picker"]
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
        let screen = NSScreen.screens[0].frame
        var picker: XCUIElement { app.windows.allElementsBoundByIndex.first { $0.frame.width == screen.width && $0.frame.height == screen.height }! }
        wait("the picker") { app.windows.allElementsBoundByIndex.contains { $0.frame.width == screen.width && $0.frame.height == screen.height } }
        shot("picker")
        let from = picker.coordinate(withNormalizedOffset: CGVector(dx: 0.2, dy: 0.2))
        from.press(forDuration: 0.3, thenDragTo: picker.coordinate(withNormalizedOffset: CGVector(dx: 0.45, dy: 0.4)))
        waitForAnswer("bytes.")
        let part = imageBytes()
        XCTAssertGreaterThan(part, 0)
        XCTAssertLessThan(part, full)
        shot("area answer")
    }
}
