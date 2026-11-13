import SwiftUI

// bumped when the window comes back to the front, since the menu or a dialog may have changed things meanwhile
@MainActor
final class Reload: ObservableObject {
    @Published var count = 0

    func bump() {
        count += 1
    }
}

struct SettingsView: View {
    let app: AppDelegate
    @ObservedObject var live: Reload

    private static let opacities = [100, 80, 60, 40, 20]

    private func bind<T>(_ path: WritableKeyPath<Settings, T>, then: @escaping (T) -> Void = { _ in }) -> Binding<T> {
        Binding(get: { app.settings[keyPath: path] }, set: { value in
            app.settings[keyPath: path] = value
            then(value)
            app.save()
            live.bump()
        })
    }

    // a screen that is gone shows as the screen with the mouse
    private func device(_ path: WritableKeyPath<Settings, String?>, _ choices: [(device: String?, text: String)]) -> Binding<String?> {
        let saved = bind(path)
        return Binding(get: { choices.contains { $0.device == saved.wrappedValue } ? saved.wrappedValue : nil }, set: { saved.wrappedValue = $0 })
    }

    private func screenPicker(_ title: String, _ path: WritableKeyPath<Settings, String?>, withAll: Bool) -> some View {
        let choices = app.screenChoices(withAll: withAll)
        return Picker(title, selection: device(path, choices)) {
            ForEach(choices, id: \.text) { Text($0.text).tag($0.device) }
        }
    }

    private var position: Binding<Int> {
        Binding(get: {
            let s = app.settings
            return s.useArea && s.answerArea != nil ? 2 : s.centerAnswer ? 1 : 0
        }, set: { choice in
            if choice == 2 {
                app.settings.useArea = true
            } else {
                app.settings.centerAnswer = choice == 1
                app.settings.useArea = false
            }
            app.save()
            live.bump()
        })
    }

    var body: some View {
        VStack(spacing: 0) {
            TabView {
                reading.tabItem { Text(L("TabReading")) }
                answer.tabItem { Text(L("TabAnswer")) }
                keys.tabItem { Text(L("TabKeys")) }
                ai.tabItem { Text(L("AI")) }
                general.tabItem { Text(L("TabGeneral")) }
            }
            .padding([.horizontal, .top], 12)
            HStack {
                Spacer()
                Button(L("Close")) { app.closeDialog(.settings) }
                    .keyboardShortcut(.cancelAction)
            }
            .padding(12)
        }
        .frame(width: 520, height: 400)
    }

    private var reading: some View {
        Form {
            screenPicker(L("ReadFrom"), \.readDevice, withAll: true)
            Picker(L("Mode"), selection: bind(\.mode)) {
                ForEach(app.settings.allModes(), id: \.id) { Text($0.name).tag($0.id) }
            }
            Picker(L("TranslateTo"), selection: bind(\.translateTo)) {
                ForEach(app.translateChoices(), id: \.value) { Text($0.name).tag($0.value) }
            }
            Button(L("EditModes")) { app.editModes() }
        }
        .formStyle(.grouped)
    }

    private var answer: some View {
        let s = app.settings
        var opacities = SettingsView.opacities
        if !opacities.contains(s.textOpacity) {
            opacities.append(s.textOpacity)
        }
        return Form {
            screenPicker(L("ShowAnswerOn"), \.showDevice, withAll: false)
            Picker(L("AnswerPosition"), selection: position) {
                Text(L("BottomRight")).tag(0)
                Text(L("CenterOfScreen")).tag(1)
                if s.answerArea != nil {
                    Text(L("DrawnArea")).tag(2)
                }
            }
            Button(L("DrawAnswerArea")) { app.drawArea() }
            Picker(L("TextOpacity"), selection: bind(\.textOpacity, then: { app.popup?.setTextOpacity($0) })) {
                ForEach(opacities, id: \.self) { Text("\($0)%").tag($0) }
            }
            Toggle(L("PlainAnswer"), isOn: bind(\.plainAnswer))
        }
        .formStyle(.grouped)
    }

    private var keys: some View {
        Form {
            ForEach(KeyJob.allCases, id: \.id) { job in
                LabeledContent(L("KeyTitle", job.name)) {
                    HStack {
                        Text(app.settings.key(for: job).name)
                        Button(L("Change")) { app.changeKey(job) }
                    }
                }
            }
        }
        .formStyle(.grouped)
    }

    private var ai: some View {
        let options = app.settings.options()
        let chain = app.settings.chain(options)
        let names = Dictionary(options.map { ($0.entry, $0.label) }, uniquingKeysWith: { first, _ in first })
        return Form {
            LabeledContent(L("Order")) {
                VStack(alignment: .trailing) {
                    ForEach(Array(chain.enumerated()), id: \.offset) { i, entry in
                        Text("\(i + 1). \(names[entry] ?? entry)")
                    }
                }
            }
            HStack {
                Button(L("ChangeOrder")) { app.changeOrder() }
                Button(L("ConnectAi")) { app.connectAI() }
            }
            if chain.contains(Settings.claude) {
                Picker(L("ClaudeCliModel"), selection: bind(\.model)) {
                    ForEach(Settings.models, id: \.id) { Text($0.name).tag($0.id) }
                }
            }
        }
        .formStyle(.grouped)
    }

    private var general: some View {
        Form {
            Toggle(L("OpenAtLogin"), isOn: Binding(get: { app.opensAtLogin }, set: {
                app.setOpenAtLogin($0)
                live.bump()
            }))
            Toggle(L("KeepAnswers"), isOn: Binding(get: { app.settings.keepHistory }, set: { _ in
                app.toggleHistory()
                live.bump()
            }))
            Toggle(L("CheckUpdates"), isOn: Binding(get: { app.settings.checkUpdates }, set: { _ in
                app.toggleUpdates()
                live.bump()
            }))
            Button(L("OpenLogFolder")) {
                try? FileManager.default.createDirectory(at: AppDir.url, withIntermediateDirectories: true)
                NSWorkspace.shared.open(AppDir.url)
            }
        }
        .formStyle(.grouped)
    }
}
