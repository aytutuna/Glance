import SwiftUI

struct ModeEntry: Identifiable {
    var id: String
    var name: String
    var prompt: String
    var builtin = false
}

@MainActor
final class ModesModel: ObservableObject {
    unowned let app: AppDelegate
    // the prompts shown were filled in with this one's language
    let start: Settings
    @Published var entries: [ModeEntry]
    @Published var selected: String {
        didSet { status = "" }
    }
    @Published var status = ""
    var changed = false
    var close: () -> Void = {}

    init(app: AppDelegate) {
        self.app = app
        let s = app.settings
        start = s
        let all = Settings.modes.map { ModeEntry(id: $0.id, name: $0.name, prompt: Prompts.instruction(s, $0.id), builtin: true) }
            + s.customModes.map { ModeEntry(id: $0.id, name: $0.name, prompt: $0.prompt) }
        entries = all
        selected = all[0].id
    }

    var index: Int? {
        entries.firstIndex { $0.id == selected }
    }

    var current: ModeEntry? {
        index.map { entries[$0] }
    }

    func text(_ path: WritableKeyPath<ModeEntry, String>) -> Binding<String> {
        Binding(get: { self.current?[keyPath: path] ?? "" }, set: { value in
            if let i = self.index {
                self.entries[i][keyPath: path] = value
            }
        })
    }

    func add() {
        let mode = CustomMode()
        entries.append(ModeEntry(id: mode.id, name: L("NewMode"), prompt: ""))
        selected = mode.id
    }

    func delete() {
        guard let at = index, !entries[at].builtin else { return }
        entries.remove(at: at)
        selected = entries[min(at, entries.count - 1)].id
    }

    func reset() {
        if let i = index {
            entries[i].prompt = Prompts.defaultPrompt(start, entries[i].id)
        }
    }

    func save() {
        for i in entries.indices where !entries[i].builtin {
            entries[i].name = entries[i].name.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        if let blank = entries.first(where: { $0.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || $0.name.isEmpty }) {
            selected = blank.id
            status = blank.builtin ? L("PromptEmpty") : L("ModeNeedsBoth")
            return
        }
        var s = app.settings
        s.customModes = entries.filter { !$0.builtin }.map { CustomMode(id: $0.id, name: $0.name, prompt: $0.prompt.trimmingCharacters(in: .whitespacesAndNewlines)) }
        s.promptOverrides = [:]
        for e in entries where e.builtin {
            let text = e.prompt.trimmingCharacters(in: .whitespacesAndNewlines)
            if text != Prompts.defaultPrompt(start, e.id) {
                s.promptOverrides[e.id] = text.replacingOccurrences(of: Prompts.language(start), with: "{language}")
            }
        }
        if !s.allModes().contains(where: { $0.id == s.mode }) {
            s.mode = Settings.modes[0].id
        }
        app.settings = s
        changed = true
        close()
    }
}

struct ModesView: View {
    @ObservedObject var m: ModesModel
    @FocusState private var naming: Bool

    var body: some View {
        VStack(spacing: 12) {
            HStack(alignment: .top, spacing: 12) {
                VStack(spacing: 8) {
                    List(m.entries, selection: $m.selected) { entry in
                        Text(entry.name).tag(entry.id)
                    }
                    .frame(width: 160, height: 250)
                    HStack {
                        Button(L("Add")) {
                            m.add()
                            naming = true
                        }
                        Button(L("Delete")) { m.delete() }
                            .disabled(m.current?.builtin ?? true)
                    }
                }
                VStack(alignment: .leading, spacing: 8) {
                    TextField("", text: m.text(\.name))
                        .labelsHidden()
                        .disabled(m.current?.builtin ?? true)
                        .focused($naming)
                        .accessibilityIdentifier("modes.name")
                    TextEditor(text: m.text(\.prompt))
                        .font(.body)
                        .border(Color.secondary.opacity(0.4))
                        .frame(width: 320, height: 210)
                        .accessibilityIdentifier("modes.prompt")
                    Button(L("Reset")) { m.reset() }
                        .disabled(!(m.current?.builtin ?? false))
                }
            }
            Text(m.status)
                .foregroundStyle(.red)
                .frame(maxWidth: .infinity, minHeight: 20, alignment: .leading)
            HStack {
                Spacer()
                Button(L("Cancel")) { m.close() }
                    .keyboardShortcut(.cancelAction)
                Button(L("Ok")) { m.save() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
    }
}
