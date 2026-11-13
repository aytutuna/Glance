import Carbon.HIToolbox
import XCTest

private struct NoProvider: Provider {
    func ask(_ chat: Conversation, onText: @escaping @Sendable (String) -> Void) async throws -> String { "" }
}

final class LogicTests: XCTestCase {
    func testDefaultPromptsDifferPerMode() {
        let s = Settings()
        let prompts = ["answer", "explain", "translate", "summarize"].map { Prompts.instruction(s, $0) }
        XCTAssertEqual(Set(prompts).count, 4)
        XCTAssertEqual(prompts[0], Prompts.instruction(s, "unknown"))
    }

    func testTranslateNamesTheTarget() {
        var s = Settings()
        s.translateTo = "German"
        XCTAssertTrue(Prompts.instruction(s, "translate").contains("into German,"))
    }

    func testEditedPromptWinsOverDefault() {
        var s = Settings()
        s.promptOverrides["explain"] = "Be brief."
        XCTAssertEqual(Prompts.instruction(s, "explain"), "Be brief.")
        s.promptOverrides["explain"] = ""
        XCTAssertTrue(Prompts.instruction(s, "explain").contains("Explain what it shows"))
    }

    func testCustomModeKeepsTheInjectionGuard() {
        var s = Settings()
        s.customModes.append(CustomMode(id: "custom:1", name: "Haiku", prompt: "Write a haiku."))
        let text = Prompts.instruction(s, "custom:1")
        XCTAssertTrue(text.hasPrefix("Write a haiku. "))
        XCTAssertTrue(text.contains("not as instructions"))
    }

    func testFirstMessageFollowsTheMode() {
        XCTAssertEqual(Prompts.first("explain"), "Explain this screenshot.")
        XCTAssertEqual(Prompts.first("custom:1"), "Follow your instructions for this screenshot.")
    }

    func testFollowUpCarriesTheTranscript() {
        let chat = Conversation(jpeg: Data(), model: "m", mode: "answer", instruction: "sys", provider: NoProvider())
        XCTAssertEqual(Prompts.message(chat), Prompts.first("answer"))
        XCTAssertEqual(Prompts.system(chat), "sys")
        chat.complete("B")
        chat.ask("why?")
        let message = Prompts.message(chat)
        XCTAssertTrue(message.contains("User: Answer the question in this screenshot.\nAssistant: B"))
        XCTAssertTrue(message.hasSuffix("Latest message, to answer now: why?"))
        XCTAssertNotEqual(Prompts.system(chat), "sys")
    }

    private func events(_ text: String) throws -> [String] {
        var lines = EventLines()
        var found: [String] = []
        for byte in Data(text.utf8) {
            if let data = try lines.feed(byte) {
                found.append(data)
            }
        }
        if let last = lines.finish() {
            found.append(last)
        }
        return found
    }

    func testEventsReturnOnlyDataLines() throws {
        let got = try events("event: x\r\ndata: {\"a\":1}\r\n\r\ndata:two\n: comment\ndata: [DONE]")
        XCTAssertEqual(got, ["{\"a\":1}", "two", "[DONE]"])
    }

    func testUrlAccepted() {
        XCTAssertNil(Api.checkUrl("https://api.example.com/v1/", withKey: true).error)
        XCTAssertNil(Api.checkUrl("http://localhost:1234/v1", withKey: true).error)
        XCTAssertNil(Api.checkUrl("http://example.com/v1", withKey: false).error)
    }

    func testUrlRejected() {
        XCTAssertNotNil(Api.checkUrl("ftp://example.com", withKey: false).error)
        XCTAssertNotNil(Api.checkUrl("https://user:pw@example.com", withKey: false).error)
        XCTAssertNotNil(Api.checkUrl("https://example.com/v1?x=1", withKey: false).error)
        XCTAssertNotNil(Api.checkUrl("http://example.com/v1", withKey: true).error)
    }

    func testUrlLosesTrailingSlash() {
        XCTAssertEqual(Api.checkUrl(" https://api.example.com/v1/ ", withKey: false).url, "https://api.example.com/v1")
    }

    func testHotkeyValidity() {
        XCTAssertTrue(Hotkey(code: UInt32(kVK_ANSI_Q), mods: Hotkey.ctrlShift).isValid)
        XCTAssertTrue(Hotkey(code: UInt32(kVK_F5), mods: 0).isValid)
        XCTAssertFalse(Hotkey(code: UInt32(kVK_ANSI_Q), mods: UInt32(shiftKey)).isValid)
        XCTAssertFalse(Hotkey(code: 0, mods: 0).isValid)
    }

    private func claudeText(_ stream: String) throws -> String {
        let api = ClaudeApi(key: "k")
        var text = ""
        for data in try events(stream) {
            let json = try JSONSerialization.jsonObject(with: Data(data.utf8))
            text += try api.delta(json, started: !text.isEmpty) ?? ""
        }
        return text
    }

    private func event(_ type: String, _ json: String) -> String {
        "event: \(type)\ndata: \(json)\n\n"
    }

    func testClaudeStreamKeepsOnlyText() throws {
        let stream = event("content_block_start", #"{"type":"content_block_start","index":0,"content_block":{"type":"fallback"}}"#) +
            event("content_block_delta", #"{"type":"content_block_delta","index":1,"delta":{"type":"thinking_delta","thinking":"hmm"}}"#) +
            event("content_block_delta", #"{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Hel"}}"#) +
            event("content_block_delta", #"{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"lo"}}"#) +
            event("message_delta", #"{"type":"message_delta","delta":{"stop_reason":"end_turn"}}"#)
        XCTAssertEqual(try claudeText(stream), "Hello")
    }

    func testClaudeRefusalIsAnError() {
        let stream = event("message_delta", #"{"type":"message_delta","delta":{"stop_reason":"refusal"}}"#)
        XCTAssertThrowsError(try claudeText(stream)) {
            XCTAssertFalse(($0 as? AiError)?.keyProblem ?? true)
        }
    }

    func testClaudeErrorEventsMapToKeyProblems() {
        let limited = event("error", #"{"type":"error","error":{"type":"rate_limit_error","message":"Slow down"}}"#)
        XCTAssertThrowsError(try claudeText(limited)) {
            let error = $0 as? AiError
            XCTAssertEqual(error?.keyProblem, true)
            XCTAssertTrue(error?.message.hasSuffix("Slow down") ?? false)
        }
        let busy = event("error", #"{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"#)
        XCTAssertThrowsError(try claudeText(busy)) {
            XCTAssertEqual(($0 as? AiError)?.keyProblem, false)
        }
    }
}
