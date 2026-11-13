import Foundation

// what a provider needs for one request, copied out of the conversation so a run never shares state
struct ChatRequest: Sendable {
    let jpeg: Data
    var model: String
    let system: String
    let message: String
    let turns: [(user: Bool, text: String)]
}

// one read and its follow-ups
final class Conversation {
    let jpeg: Data
    let model: String
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

    func request() -> ChatRequest {
        ChatRequest(jpeg: jpeg, model: model, system: Prompts.system(self), message: Prompts.message(self), turns: turns())
    }

    private func turns() -> [(user: Bool, text: String)] {
        done.flatMap { [(true, $0.question), (false, $0.answer)] } + [(true, pending)]
    }
}
