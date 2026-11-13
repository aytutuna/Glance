import Carbon.HIToolbox
import CryptoKit
import XCTest

final class ShellTests: XCTestCase {
    func testKeyNamesUseMacOrder() {
        let all = UInt32(controlKey | optionKey | shiftKey | cmdKey)
        XCTAssertEqual(Hotkey(code: UInt32(kVK_F5), mods: UInt32(controlKey | shiftKey)).name, "⌃⇧F5")
        XCTAssertEqual(Hotkey(code: UInt32(kVK_Space), mods: all).name, "⌃⌥⇧⌘Space")
        XCTAssertEqual(Hotkey(code: UInt32(kVK_Return), mods: UInt32(cmdKey)).name, "⌘↩")
        XCTAssertEqual(Hotkey(code: UInt32(kVK_F13), mods: 0).name, "F13")
    }

    func testVersionCompare() {
        XCTAssertTrue(Updates.isNewer("v1.2.0", than: "1.1.0"))
        XCTAssertTrue(Updates.isNewer("1.10", than: "1.9.5"))
        XCTAssertFalse(Updates.isNewer("1.1.0", than: "1.1.0"))
        XCTAssertFalse(Updates.isNewer("1.1", than: "1.1.0"))
        XCTAssertFalse(Updates.isNewer("1.0.9", than: "1.1.0"))
        XCTAssertFalse(Updates.isNewer("nightly", than: "1.1.0"))
        XCTAssertFalse(Updates.isNewer("2", than: "1.1.0"))
        XCTAssertFalse(Updates.isNewer("1.2.x", than: "1.1.0"))
    }

    func testOnlyOurReleasePagesAreOpened() {
        XCTAssertTrue(Updates.trusted("https://github.com/aytutuna/Glance/releases/tag/v1.2.0"))
        XCTAssertFalse(Updates.trusted("https://github.com/aytutuna/Glance.evil/x"))
        XCTAssertFalse(Updates.trusted("http://github.com/aytutuna/Glance/"))
    }

    func testHistoryRoundTrip() throws {
        let key = SymmetricKey(size: .bits256)
        let sealed = try History.seal(["one", "two\nlines"], with: key)
        XCTAssertFalse(String(decoding: sealed, as: UTF8.self).contains("two"))
        XCTAssertEqual(History.open(sealed, with: key), ["one", "two\nlines"])
        XCTAssertNil(History.open(sealed, with: SymmetricKey(size: .bits256)))
        XCTAssertNil(History.open(Data("not a file".utf8), with: key))
    }

    func testHistoryKeepsTheLatestTen() throws {
        let key = SymmetricKey(size: .bits256)
        let many = (1...14).map(String.init)
        XCTAssertEqual(History.open(try History.seal(many, with: key), with: key), Array(many.prefix(10)))
    }

    func testPreviewIsOneShortLine() {
        XCTAssertEqual(History.preview("a  b\n\n c"), "a b c")
        let long = String(repeating: "x", count: 100)
        XCTAssertEqual(History.preview(long), String(repeating: "x", count: 57) + "...")
        XCTAssertEqual(History.preview(String(repeating: "y", count: 60)).count, 60)
    }

    func testCornerSitsInsideTheWorkingArea() {
        let area = CGRect(x: 0, y: 25, width: 1000, height: 775)
        let r = Layout.corner(size: CGSize(width: 420, height: 100), in: area, margin: 16)
        XCTAssertEqual(r, CGRect(x: 564, y: 41, width: 420, height: 100))
    }

    func testCenterIsCentered() {
        let r = Layout.centered(size: CGSize(width: 400, height: 200), in: CGRect(x: 1000, y: 0, width: 1000, height: 800))
        XCTAssertEqual(r, CGRect(x: 1300, y: 300, width: 400, height: 200))
    }

    func testDrawnAreaCountsFromTheTop() {
        let screen = CGRect(x: 0, y: 0, width: 1000, height: 800)
        let r = Layout.drawn([0.1, 0.25, 0.5, 0.25], on: screen)
        XCTAssertEqual(r, CGRect(x: 100, y: 400, width: 500, height: 200))
    }

    func testDrawnAreaHasAMinimumAndStaysOnScreen() {
        let screen = CGRect(x: 0, y: 0, width: 1000, height: 800)
        let r = Layout.drawn([0.99, 0.99, 0.01, 0.01], on: screen)
        XCTAssertEqual(r, CGRect(x: 840, y: 0, width: 160, height: 60))
    }

    func testFractionsRoundTrip() {
        let area = Layout.fractions(of: CGRect(x: 100, y: 200, width: 500, height: 100), in: CGSize(width: 1000, height: 800))
        XCTAssertEqual(area, [0.1, 0.25, 0.5, 0.125])
        XCTAssertTrue(Settings.validArea(area))
    }
}
