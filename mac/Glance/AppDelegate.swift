import AppKit
import ServiceManagement
import UserNotifications

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate, UNUserNotificationCenterDelegate {
    nonisolated static let showRunning = Notification.Name("com.aytutuna.glance.show")
    static let allScreens = "all"
    static let windowDevice = "window"
    // apps that can be frontmost without the user having left what they were reading
    private static let passive: Set<String> = [
        "com.apple.systemuiserver", "com.apple.controlcenter", "com.apple.dock", "com.apple.loginwindow",
        "com.apple.notificationcenterui", "com.apple.Spotlight",
    ]

    var settings = Settings.load()
    var history: [String] = []
    var current: Task<Void, Never>?
    var serial = 0
    var popup: AnswerPopup?
    var picker: AreaPicker?
    var labels: [ScreenLabel] = []
    var keyDialog: NSWindow?
    var dialogs: [DialogKind: Dialog] = [:]
    // the app that had the focus before one of Glance's windows took it
    var lastApp: NSRunningApplication?
    var chat: Conversation?
    var thread = "", shown = ""
    var updateVersion: String?
    var updateUrl: String?

    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let hotkeys = Hotkeys()
    private let idleIcon = AppDelegate.icon(busy: false)
    private let busyIcon = AppDelegate.icon(busy: true)

    func applicationDidFinishLaunching(_ note: Notification) {
        NSApp.setActivationPolicy(.accessory)
        installEditMenu()
        UNUserNotificationCenter.current().delegate = self
        if settings.keepHistory {
            history = History.load()
        }

        let menu = NSMenu()
        menu.delegate = self
        menu.autoenablesItems = false
        item.menu = menu
        item.button?.image = idleIcon
        updateTooltip()

        hotkeys.pressed = { [weak self] job in
            guard let self else { return }
            switch job {
            case .read: trigger(after: .zero)
            case .region: trigger(after: .zero, area: true)
            case .close: closeAnswer()
            case .hide: hideAnswer()
            }
        }
        bindKeys()

        lastApp = NSWorkspace.shared.frontmostApplication.flatMap { usable($0) ? $0 : nil }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication
            MainActor.assumeIsolated {
                if let app, self?.usable(app) == true {
                    self?.lastApp = app
                }
            }
        }
        DistributedNotificationCenter.default().addObserver(forName: AppDelegate.showRunning, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                if let self {
                    self.notify(L("AlreadyRunning", self.settings.hotkey.name))
                }
            }
        }
        Task { await checkForUpdate() }
    }

    // an app without a main menu has no Cut, Copy, Paste or Select All in its text fields; the menu bar stays hidden
    private func installEditMenu() {
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let main = NSMenu()
        main.addItem(NSMenuItem())
        let item = NSMenuItem()
        item.submenu = edit
        main.addItem(item)
        NSApp.mainMenu = main
    }

    nonisolated func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner]
    }

    // Glance's own windows and the system bars don't count as the app being read
    private func usable(_ app: NSRunningApplication) -> Bool {
        app.processIdentifier != ProcessInfo.processInfo.processIdentifier && !AppDelegate.passive.contains(app.bundleIdentifier ?? "")
    }

    // the frontmost app's window, or the last one before Glance or the menu bar took over
    func activeWindow() -> WindowTarget? {
        var apps: [pid_t] = []
        if let front = NSWorkspace.shared.frontmostApplication, usable(front) {
            apps.append(front.processIdentifier)
        }
        if let last = lastApp, !last.isTerminated {
            apps.append(last.processIdentifier)
        }
        return Capture.frontWindow(ofApps: apps)
    }

    func bindKeys() {
        hotkeys.unregisterAll()
        var failed: [String] = []
        for job in KeyJob.allCases {
            let key = settings.key(for: job)
            if !hotkeys.register(job, key) {
                let what = switch job {
                case .read: "KeyReadsScreen"
                case .region: "KeyReadsArea"
                case .close: "KeyClosesAnswer"
                case .hide: "KeyHidesAnswer"
                }
                failed.append(L(what, key.name))
            }
        }
        if !failed.isEmpty {
            notify(L("KeysFailed", failed.joined(separator: L("ListJoin"))))
        }
    }

    // for the key recorder, so a key being chosen doesn't also fire its job
    func unbindKeys() {
        hotkeys.unregisterAll()
    }

    // whether the system lets Glance have the key; only meant for while the keys are unbound
    func canHold(_ job: KeyJob, _ key: Hotkey) -> Bool {
        let ok = hotkeys.register(job, key)
        hotkeys.unregisterAll()
        return ok
    }

    func updateTooltip() {
        item.button?.toolTip = L("Tooltip", settings.hotkey.name, settings.regionKey.name, settings.closeKey.name)
    }

    func save() {
        if !settings.save() {
            notify(L("SettingsNotSaved"))
        }
    }

    func setBusy(_ busy: Bool) {
        item.button?.image = busy ? busyIcon : idleIcon
    }

    func notify(_ text: String) {
        let center = UNUserNotificationCenter.current()
        Task {
            var status = await center.notificationSettings().authorizationStatus
            if status == .notDetermined {
                status = (try? await center.requestAuthorization(options: [.alert])) == true ? .authorized : .denied
            }
            guard status == .authorized || status == .provisional else {
                // a failure that nobody sees is worse than a dialog
                let alert = NSAlert()
                alert.messageText = "Glance"
                alert.informativeText = text
                NSApp.activate()
                alert.runModal()
                return
            }
            let content = UNMutableNotificationContent()
            content.title = "Glance"
            content.body = text
            try? await center.add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
        }
    }

    var opensAtLogin: Bool {
        SMAppService.mainApp.status == .enabled
    }

    func setOpenAtLogin(_ on: Bool) {
        if SMAppService.mainApp.status == .requiresApproval {
            SMAppService.openSystemSettingsLoginItems()
            return
        }
        do {
            if on {
                try SMAppService.mainApp.register()
                if SMAppService.mainApp.status == .requiresApproval {
                    SMAppService.openSystemSettingsLoginItems()
                }
            } else {
                try SMAppService.mainApp.unregister()
            }
        } catch {
            Log.write("open at login", error)
            notify(L("StartupFailed"))
        }
    }

    private func checkForUpdate() async {
        guard settings.checkUpdates, Date().timeIntervalSince(settings.lastUpdateCheck) >= 24 * 3600 else { return }
        do {
            let found = try await Updates.latest()
            settings.lastUpdateCheck = Date()
            _ = settings.save()
            let mine = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0"
            if Updates.isNewer(found.tag, than: mine) && Updates.trusted(found.url) {
                updateVersion = String(found.tag.drop { $0 == "v" })
                updateUrl = found.url
                notify(L("UpdateReady", updateVersion ?? ""))
            }
        } catch {
            Log.write("update check", error)
        }
    }

    func quit() {
        current?.cancel()
        picker?.end(nil)
        closePopup()
        closeLabels()
        dialogs.values.forEach { $0.window.close() }
        hotkeys.unregisterAll()
        NSStatusBar.system.removeStatusItem(item)
        NSApp.terminate(nil)
    }

    // a white G on a rounded square, as a template while idle so the menu bar colors it
    private static func icon(busy: Bool) -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            (busy ? NSColor(srgbRed: 232 / 255, green: 140 / 255, blue: 0, alpha: 1) : NSColor.black).setFill()
            NSBezierPath(roundedRect: rect.insetBy(dx: 0.5, dy: 0.5), xRadius: 4, yRadius: 4).fill()
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 14, weight: .bold), .foregroundColor: NSColor.white]
            let size = ("G" as NSString).size(withAttributes: attrs)
            if !busy {
                NSGraphicsContext.current?.compositingOperation = .destinationOut
            }
            ("G" as NSString).draw(at: NSPoint(x: (rect.width - size.width) / 2, y: (rect.height - size.height) / 2), withAttributes: attrs)
            return true
        }
        image.isTemplate = !busy
        return image
    }
}
