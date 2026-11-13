import Foundation

// one read and its follow-ups; a single run touches it at a time
final class Conversation: @unchecked Sendable {
    let jpeg: Data
    var model: String
    let mode: String
    let instruction: String
    let provider: any Provider
    private(set) var pending: String
    private(set) var done: [(question: String, answer: String)] = []

    init(jpeg: Data, model: String, mode: String, instruction: String, provider: any Provider) {
        self.jpeg = jpeg
        self.model = model
        self.mode = mode
        self.instruction = instruction
        self.provider = provider
        pending = Prompts.first(mode)
    }

    func ask(_ question: String) {
        pending = question
    }

    func complete(_ answer: String) {
        done.append((pending, answer))
    }

    func turns() -> [(user: Bool, text: String)] {
        done.flatMap { [(true, $0.question), (false, $0.answer)] } + [(true, pending)]
    }
}
