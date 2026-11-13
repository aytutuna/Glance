import AppKit

private final class ActionItem: NSMenuItem {
    private let run: () -> Void

    init(_ title: String, checked: Bool = false, enabled: Bool = true, run: @escaping () -> Void) {
        self.run = run
        super.init(title: title, action: #selector(fire), keyEquivalent: "")
        target = self
        state = checked ? .on : .off
        isEnabled = enabled
    }

    required init(coder: NSCoder) { fatalError() }

    @objc private func fire() {
        run()
    }
}

private func submenu(_ title: String, _ items: [NSMenuItem]) -> NSMenuItem {
    let parent = NSMenuItem(title: title, action: nil, keyEquivalent: "")
    let menu = NSMenu()
    menu.autoenablesItems = false
    items.forEach(menu.addItem)
    parent.submenu = menu
    return parent
}

extension AppDelegate {
    func menuNeedsUpdate(_ menu: NSMenu) {
        updateTooltip()
        menu.removeAllItems()
        let options = settings.options()
        let chain = settings.chain(options)
        var items: [NSMenuItem] = []
        if let url = updateUrl.flatMap(URL.init(string:)) {
            items.append(ActionItem(L("UpdateTo", updateVersion ?? "")) { NSWorkspace.shared.open(url) })
            items.append(.separator())
        }
        items.append(ActionItem(L("MenuReadNow")) { self.trigger(after: .milliseconds(300)) })
        items.append(ActionItem(L("ReadArea")) { self.trigger(after: .milliseconds(300), area: true) })
        items.append(ActionItem(L("MenuHideText")) { self.hideAnswer() })
        items.append(screenMenu(L("ReadFrom"), selected: settings.readDevice, withAll: true) { self.settings.readDevice = $0 })
        items.append(screenMenu(L("ShowAnswerOn"), selected: settings.showDevice, withAll: false) { self.settings.showDevice = $0 })
        items.append(positionMenu())
        items.append(ActionItem(L("PlainAnswer"), checked: settings.plainAnswer) {
            self.settings.plainAnswer.toggle()
            self.save()
        })
        items.append(opacityMenu())
        items.append(aiMenu(chain, options))
        if chain.contains(Settings.claude) {
            items.append(modelMenu())
        }
        items.append(modeMenu())
        items.append(translateMenu())
        items.append(ActionItem(L("IdentifyScreens")) { self.identify() })
        for job in KeyJob.allCases {
            items.append(ActionItem(L("KeyMenu", job.name, settings.key(for: job).name)) { self.changeKey(job) })
        }
        let login = opensAtLogin
        items.append(ActionItem(L("OpenAtLogin"), checked: login) { self.setOpenAtLogin(!login) })
        items.append(ActionItem(L("CheckUpdates"), checked: settings.checkUpdates) {
            self.settings.checkUpdates.toggle()
            self.save()
        })
        items.append(.separator())
        items.append(recentMenu())
        items.append(.separator())
        items.append(ActionItem(L("MenuSettings")) { self.openSettings() })
        items.append(ActionItem(L("MenuQuit")) { self.quit() })
        items.forEach(menu.addItem)
    }

    private func change(_ edit: @escaping () -> Void) -> () -> Void {
        {
            edit()
            self.save()
        }
    }

    private func screenMenu(_ title: String, selected: String?, withAll: Bool, choose: @escaping (String?) -> Void) -> NSMenuItem {
        var choices: [(device: String?, text: String)] = [(nil, L("MouseScreen"))]
        if withAll {
            choices.append((AppDelegate.allScreens, L("AllScreens")))
            choices.append((AppDelegate.windowDevice, L("ActiveWindow")))
        }
        for (i, screen) in NSScreen.screens.enumerated() {
            choices.append((screen.uuid, screen.describe(i)))
        }
        // a screen that is gone counts as the screen with the mouse
        let on = choices.contains { $0.device == selected } ? selected : nil
        return submenu(title, choices.map { c in
            ActionItem(c.text, checked: c.device == on, run: change { choose(c.device) })
        })
    }

    private func positionMenu() -> NSMenuItem {
        var items = [(L("BottomRight"), false), (L("CenterOfScreen"), true)].map { name, center in
            ActionItem(name, checked: !settings.useArea && settings.centerAnswer == center, run: change {
                self.settings.centerAnswer = center
                self.settings.useArea = false
            })
        }
        if settings.answerArea != nil {
            items.append(ActionItem(L("DrawnArea"), checked: settings.useArea, run: change { self.settings.useArea = true }))
        }
        return submenu(L("AnswerPosition"), items + [.separator(), ActionItem(L("DrawAnswerArea")) { self.drawArea() }])
    }

    private func opacityMenu() -> NSMenuItem {
        submenu(L("TextOpacity"), [100, 80, 60, 40, 20].map { percent in
            ActionItem("\(percent)%", checked: settings.textOpacity == percent) {
                self.settings.textOpacity = percent
                self.popup?.setTextOpacity(percent)
                self.save()
            }
        })
    }

    private func aiMenu(_ chain: [String], _ options: [(entry: String, label: String)]) -> NSMenuItem {
        let names = Dictionary(options.map { ($0.entry, $0.label) }, uniquingKeysWith: { first, _ in first })
        // picking one moves it to the top; the rest keep their order behind it
        var items: [NSMenuItem] = chain.enumerated().map { i, entry in
            ActionItem("\(i + 1). \(names[entry] ?? entry)", checked: i == 0, run: change {
                self.settings.order = [entry] + chain.filter { $0 != entry }
            })
        }
        items += [.separator(), ActionItem(L("ChangeOrder")) { self.changeOrder() }, ActionItem(L("ConnectAi")) { self.connectAI() }]
        return submenu(L("AI"), items)
    }

    private func modelMenu() -> NSMenuItem {
        submenu(L("ClaudeCliModel"), Settings.models.map { model in
            ActionItem(model.name, checked: settings.model == model.id, run: change { self.settings.model = model.id })
        })
    }

    private func modeMenu() -> NSMenuItem {
        var items: [NSMenuItem] = settings.allModes().map { mode in
            ActionItem(mode.name, checked: settings.mode == mode.id, run: change { self.settings.mode = mode.id })
        }
        items += [.separator(), ActionItem(L("EditModes")) { self.editModes() }]
        return submenu(L("Mode"), items)
    }

    private func translateMenu() -> NSMenuItem {
        let id = Locale.preferredLanguages.first ?? "en"
        let system = Locale(identifier: id).localizedString(forIdentifier: id) ?? id
        let choices = [("", L("SystemLanguage", system))] + Settings.languages.map { ($0, $0) }
        return submenu(L("TranslateTo"), choices.map { value, name in
            ActionItem(name, checked: settings.translateTo == value, run: change { self.settings.translateTo = value })
        })
    }

    private func recentMenu() -> NSMenuItem {
        var items: [NSMenuItem] = history.map { answer in
            ActionItem(History.preview(answer)) {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(answer, forType: .string)
            }
        }
        if !history.isEmpty {
            items.append(.separator())
        }
        items.append(ActionItem(L("KeepAnswers"), checked: settings.keepHistory) {
            self.settings.keepHistory.toggle()
            if self.settings.keepHistory {
                History.save(self.history)
            } else {
                History.delete()
            }
            self.save()
        })
        items.append(ActionItem(L("ClearRecent"), enabled: !history.isEmpty) {
            self.history = []
            if self.settings.keepHistory {
                History.save([])
            }
        })
        return submenu(L("RecentAnswers"), items)
    }
}
