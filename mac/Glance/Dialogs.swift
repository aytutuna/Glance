import SwiftUI

enum DialogKind {
    case settings, connect, order, modes, key
}

// a window around a SwiftUI view; the delegate is the window's only owner besides the app
@MainActor
final class Dialog: NSObject, NSWindowDelegate {
    let window: NSWindow
    var onKey: (() -> Void)?
    var onClose: (() -> Void)?

    init(title: String, content: some View) {
        window = NSWindow(contentViewController: NSHostingController(rootView: content))
        window.title = title
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        super.init()
        window.delegate = self
        let area = NSScreen.underMouse.visibleFrame
        window.setFrameOrigin(NSPoint(x: area.midX - window.frame.width / 2, y: area.midY - window.frame.height / 2))
    }

    func windowDidBecomeKey(_ note: Notification) {
        onKey?()
    }

    func windowWillClose(_ note: Notification) {
        onClose?()
    }
}

extension AppDelegate {
    func openSettings() {
        let live = Reload()
        present(.settings, L("SettingsTitle"), SettingsView(app: self, live: live), onKey: { live.bump() })
    }

    func connectAI() {}
    func changeOrder() {}
    func editModes() {}
    func changeKey(_ job: KeyJob) {}

    func toggleHistory() {
        settings.keepHistory.toggle()
        if settings.keepHistory {
            History.save(history)
        } else {
            History.delete()
        }
        save()
    }

    func toggleUpdates() {
        settings.checkUpdates.toggle()
        save()
    }

    func closeDialog(_ kind: DialogKind) {
        dialogs[kind]?.window.close()
    }

    func hideSettings() {
        dialogs[.settings]?.window.orderOut(nil)
    }

    // not made key, so the area picker that follows keeps the focus
    func showSettings() {
        dialogs[.settings]?.window.orderFrontRegardless()
    }

    func raise(_ window: NSWindow) {
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    func present(_ kind: DialogKind, _ title: String, _ content: some View, onKey: (() -> Void)? = nil, onClose: (() -> Void)? = nil) {
        if let shown = dialogs[kind] {
            raise(shown.window)
            return
        }
        let dialog = Dialog(title: title, content: content)
        dialog.onKey = onKey
        dialog.onClose = { [unowned self] in
            dialogs[kind] = nil
            onClose?()
            handBack()
        }
        dialogs[kind] = dialog
        raise(dialog.window)
    }

    // an app without windows keeps the focus when its last window closes, so give it back
    private func handBack() {
        if dialogs.isEmpty && NSApp.isActive {
            lastApp?.activate(from: .current, options: [])
        }
    }
}
