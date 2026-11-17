import SwiftUI

private extension String {
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }
}

@MainActor
final class ConnectModel: ObservableObject {
    static let ids = [Settings.claude, Settings.codex, Settings.geminiCli, "anthropic", "openai", "gemini", "custom"]

    // the command, how to install it and how to sign in, for the accounts that work through a CLI
    static let accounts: [String: (exe: String, install: String, login: String)] = [
        Settings.claude: ("claude", "npm install -g @anthropic-ai/claude-code", "claude"),
        Settings.codex: ("codex", "npm install -g @openai/codex", "codex login"),
        Settings.geminiCli: ("gemini", "npm install -g @google/gemini-cli", "gemini"),
    ]

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
    @Published var effort = ""
    @Published var info = ""
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
        // the CLI account is the default provider, so a first visit starts on the Claude API instead
        let current = app.settings.provider
        id = current != Settings.claude && ConnectModel.ids.contains(current) ? current : "anthropic"
        pick()
    }

    var name: String {
        Settings.providers.first { $0.id == id }?.name ?? id
    }

    var stored: Bool {
        app.settings.hasKey(id)
    }

    var account: Bool {
        ConnectModel.accounts[id] != nil
    }

    var levels: [String] {
        [""] + Settings.effortLevels(id)
    }

    func pick() {
        stop()
        key = ""
        saved = 0
        url = app.settings.customUrl ?? ""
        model = id == Settings.claude ? app.settings.model : app.settings.providerModels[id] ?? ""
        models = []
        if id == "anthropic" {
            models = Settings.models.map(\.id)
            if model.isEmpty {
                model = models[0]
            }
        } else if id == Settings.geminiCli {
            models = GeminiCli.aliases
        }
        effort = app.settings.effortOf(id)
        info = ""
        say("", false)
        showAccount()
    }

    private func showAccount() {
        guard let cli = ConnectModel.accounts[id] else { return }
        let mine = run, id = id
        load = Task {
            guard let path = await Cli.shared.path(cli.exe) else {
                if run == mine {
                    info = L("AccountMissing", cli.install)
                }
                return
            }
            guard run == mine else { return }
            let found = L("AccountFound", path)
            info = found
            var signedIn: Bool?
            if id == Settings.codex {
                let env = await Cli.shared.env()
                signedIn = await Child.capture(path, ["login", "status"], env: env, limit: 20)?.code == 0
            } else if id == Settings.geminiCli {
                signedIn = FileManager.default.fileExists(atPath: NSHomeDirectory() + "/.gemini/oauth_creds.json")
            }
            if let signedIn, run == mine {
                info = found + "\n" + L(signedIn ? "SignedIn" : "NotSignedIn")
            }
        }
    }

    // a script in Terminal, since a sign-in needs a real terminal to answer in
    func signIn() {
        guard let login = ConnectModel.accounts[id]?.login else { return }
        let script = FileManager.default.temporaryDirectory.appendingPathComponent("Glance-signin-" + shortId() + ".command")
        do {
            try "#!/bin/zsh -l\nrm -f -- \"$0\"\n\(login)\n".write(to: script, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: script.path)
        } catch {
            Log.write("sign in", error)
            return say(L("Crashed"), true)
        }
        NSWorkspace.shared.open([script], withApplicationAt: URL(fileURLWithPath: "/System/Applications/Utilities/Terminal.app"),
                                configuration: NSWorkspace.OpenConfiguration())
    }

    private func saveEffort(_ s: inout Settings) {
        if !Settings.effortLevels(id).isEmpty {
            s.effort[id] = effort
        }
    }

    private func saveAccount() {
        var s = app.settings
        if id == Settings.claude {
            s.model = model
        } else {
            s.providerModels[id] = model.trimmed
        }
        saveEffort(&s)
        s.provider = id
        s.include(id)
        app.settings = s
        changed = true
        close()
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
        app.save()
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
        if account {
            return saveAccount()
        }
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
        var stale: [String] = []
        if id == "custom" {
            let same = s.sameHost(url)
            let (error, normal) = Api.checkUrl(url, withKey: !typed.isEmpty || hasKey && same)
            if let error {
                return say(error, true)
            }
            address = normal
            // saved keys belong to the old address; they leave the Keychain only once the new settings are in place
            if !same {
                stale = (s.apiKeys[id] ?? []).map { "\(id):\($0)" }
                s.apiKeys[id] = nil
                s.order.removeAll { stale.contains($0) }
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
        saveEffort(&s)
        s.provider = id
        if !s.hasKey(id) {
            s.include(id)
        }
        app.settings = s
        stale.forEach(Keychain.delete)
        changed = true
        close()
    }
}

struct ConnectView: View {
    @ObservedObject var m: ConnectModel

    private func block(_ caption: String, @ViewBuilder _ content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(caption)
            content()
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            block(L("AI")) {
                Picker("", selection: $m.id) {
                    ForEach(ConnectModel.ids, id: \.self) { id in
                        Text(Settings.providers.first { $0.id == id }?.name ?? id).tag(id)
                    }
                }
                .labelsHidden()
                .accessibilityIdentifier("connect.provider")
            }
            if m.account {
                HStack(alignment: .top) {
                    Text(m.info)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .accessibilityIdentifier("connect.info")
                    Button(L("SignIn")) { m.signIn() }
                }
            } else {
                if m.stored {
                    block(L("SavedKeysCaption")) {
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
                block(m.stored ? L("AddKey") : m.id == "custom" ? L("ApiKeyOptional") : L("ApiKey")) {
                    SecureField("", text: $m.key, prompt: Text(m.stored ? L("PasteAnotherKey") : m.id == "custom" ? L("Optional") : L("PasteKey")))
                        .labelsHidden()
                        .accessibilityIdentifier("connect.key")
                }
                if m.id == "custom" {
                    block(L("BaseUrl")) {
                        TextField("", text: $m.url, prompt: Text("http://localhost:11434/v1"))
                            .labelsHidden()
                            .accessibilityIdentifier("connect.url")
                    }
                }
            }
            block(L("Model")) {
                if m.id == Settings.claude {
                    Picker("", selection: $m.model) {
                        ForEach(Settings.models, id: \.id) { Text($0.name).tag($0.id) }
                    }
                    .labelsHidden()
                    .accessibilityIdentifier("connect.model")
                } else {
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
                        if !m.account {
                            Button(L("LoadModels")) { m.loadModels() }
                                .disabled(m.loading)
                        }
                    }
                }
            }
            if m.levels.count > 1 {
                block(L("Effort")) {
                    Picker("", selection: $m.effort) {
                        ForEach(m.levels, id: \.self) { Text(Settings.effortName($0)).tag($0) }
                    }
                    .labelsHidden()
                    .accessibilityIdentifier("connect.effort")
                }
            }
            Text(L("ConnectNote"))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text(m.status)
                .foregroundStyle(m.failed ? Color.red : Color.secondary)
                .frame(maxWidth: .infinity, minHeight: 32, alignment: .topLeading)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityIdentifier("connect.status")
            HStack {
                Spacer()
                Button(L("Cancel")) { m.close() }
                    .keyboardShortcut(.cancelAction)
                Button(L("Save")) { m.save() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 440)
    }
}
