import SwiftUI

private extension String {
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}

@MainActor
final class ConnectModel: ObservableObject {
    static let ids = ["anthropic", "openai", "gemini", "custom"]

    unowned let app: AppDelegate
    @Published var id: String {
        didSet {
            if id != oldValue {
                pick()
            }
        }
    }
    @Published var key = ""
    @Published var url = ""
    @Published var model = ""
    @Published var models: [String] = []
    @Published var saved = 0
    @Published var status = ""
    @Published var failed = false
    @Published var loading = false
    var changed = false
    var close: () -> Void = {}
    private var load: Task<Void, Never>?
    private var run = 0

    init(app: AppDelegate) {
        self.app = app
        id = ConnectModel.ids.contains(app.settings.provider) ? app.settings.provider : ConnectModel.ids[0]
        pick()
    }

    var name: String {
        Settings.providers.first { $0.id == id }?.name ?? id
    }

    var stored: Bool {
        app.settings.hasKey(id)
    }

    func pick() {
        stop()
        key = ""
        saved = 0
        url = app.settings.customUrl ?? ""
        model = app.settings.providerModels[id] ?? ""
        models = []
        if id == "anthropic" {
            models = Settings.models.map(\.id)
            if model.isEmpty {
                model = models[0]
            }
        }
        say("", false)
    }

    func stop() {
        run += 1
        load?.cancel()
        load = nil
        loading = false
    }

    private func say(_ text: String, _ error: Bool) {
        status = text
        failed = error
    }

    func remove() {
        guard saved < (app.settings.apiKeys[id] ?? []).count else { return }
        app.settings.removeKey(id, at: saved)
        changed = true
        saved = 0
        objectWillChange.send()
    }

    func loadModels() {
        let typed = key.trimmed
        if !typed.isEmpty, let bad = Api.checkKey(typed) {
            say(bad, true)
            return
        }
        stop()
        let mine = run
        let (s, id, url, name) = (app.settings, id, url, name)
        loading = true
        say(L("LoadingModels"), false)
        load = Task {
            do {
                let found = try await withLimit(20, name: name) {
                    try await Ai.models(s, id: id, urlText: url, typedKey: typed.isEmpty ? nil : typed)
                }
                guard run == mine else { return }
                models = found
                say(L("ModelsLoaded", found.count), false)
            } catch is CancellationError {
                return
            } catch {
                guard run == mine else { return }
                say((error as? AiError)?.message ?? L("Crashed"), true)
                if !(error is AiError) {
                    Log.write("load models", error)
                }
            }
            loading = false
        }
    }

    func save() {
        var s = app.settings
        let typed = key.trimmed
        if !typed.isEmpty, let bad = Api.checkKey(typed) {
            return say(bad, true)
        }
        if typed.isEmpty && s.hasKey(id) && s.getKeys(id).isEmpty {
            return say(L("SavedKeysUnreadable"), true)
        }
        let hasKey = !typed.isEmpty || s.hasKey(id)
        if id != "custom" && !hasKey {
            return say(L("EnterKey"), true)
        }
        let picked = model.trimmed
        if picked.isEmpty {
            return say(L("PickModel"), true)
        }
        var address: String?
        if id == "custom" {
            let same = s.sameHost(url)
            let (error, normal) = Api.checkUrl(url, withKey: !typed.isEmpty || hasKey && same)
            if let error {
                return say(error, true)
            }
            address = normal
            // saved keys belong to the old address
            while !same && s.hasKey(id) {
                s.removeKey(id, at: 0)
            }
        }
        if !typed.isEmpty {
            do {
                try s.addKey(id, typed)
            } catch {
                Log.write("save key", error)
                return say(L("KeychainFailed"), true)
            }
        }
        if let address {
            s.customUrl = address
        }
        s.providerModels[id] = picked
        s.provider = id
        if !s.hasKey(id) {
            s.include(id)
        }
        app.settings = s
        changed = true
        close()
    }
}

struct ConnectView: View {
    @ObservedObject var m: ConnectModel

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Picker(L("AI"), selection: $m.id) {
                    ForEach(ConnectModel.ids, id: \.self) { id in
                        Text(Settings.providers.first { $0.id == id }?.name ?? id).tag(id)
                    }
                }
                .accessibilityIdentifier("connect.provider")
                if m.stored {
                    Section(L("SavedKeysCaption")) {
                        HStack {
                            Picker("", selection: $m.saved) {
                                ForEach(Array(m.app.settings.keyNames(m.id).enumerated()), id: \.offset) { i, name in
                                    Text(name).tag(i)
                                }
                            }
                            .labelsHidden()
                            Button(L("RemoveKey")) { m.remove() }
                        }
                    }
                }
                Section(m.stored ? L("AddKey") : m.id == "custom" ? L("ApiKeyOptional") : L("ApiKey")) {
                    SecureField("", text: $m.key, prompt: Text(m.stored ? L("PasteAnotherKey") : m.id == "custom" ? L("Optional") : L("PasteKey")))
                        .labelsHidden()
                        .accessibilityIdentifier("connect.key")
                }
                if m.id == "custom" {
                    Section(L("BaseUrl")) {
                        TextField("", text: $m.url, prompt: Text("http://localhost:11434/v1"))
                            .labelsHidden()
                            .accessibilityIdentifier("connect.url")
                    }
                }
                Section(L("Model")) {
                    HStack {
                        TextField("", text: $m.model)
                            .labelsHidden()
                            .accessibilityIdentifier("connect.model")
                        if !m.models.isEmpty {
                            Menu {
                                ForEach(m.models, id: \.self) { name in
                                    Button(name) { m.model = name }
                                }
                            } label: {
                                Image(systemName: "chevron.down")
                            }
                            .menuStyle(.borderlessButton)
                            .fixedSize()
                        }
                        Button(L("LoadModels")) { m.loadModels() }
                            .disabled(m.loading)
                    }
                }
                Section {
                    Text(L("ConnectNote")).foregroundStyle(.secondary)
                    Text(m.status)
                        .foregroundStyle(m.failed ? Color.red : Color.secondary)
                        .frame(minHeight: 32, alignment: .topLeading)
                        .accessibilityIdentifier("connect.status")
                }
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button(L("Cancel")) { m.close() }
                    .keyboardShortcut(.cancelAction)
                Button(L("Save")) { m.save() }
                    .keyboardShortcut(.defaultAction)
            }
            .padding(12)
        }
        .frame(width: 460)
    }
}
