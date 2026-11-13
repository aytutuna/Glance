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
        let host = NSHostingController(rootView: content)
        // the window follows the view, which grows and shrinks with the choices in it
        host.sizingOptions = .preferredContentSize
        window = NSWindow(contentViewController: host)
        window.title = title
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        super.init()
        window.delegate = self
        host.view.layoutSubtreeIfNeeded()
        window.setContentSize(host.view.fittingSize)
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

    func connectAI() {
        let connect = ConnectModel(app: self)
        connect.close = { [unowned self] in closeDialog(.connect) }
        present(.connect, L("ConnectTitle"), ConnectView(m: connect), onClose: { [unowned self] in
            connect.stop()
            if connect.changed {
                // an open thread holds the old keys, and one may have just been removed
                closeAnswer()
                save()
            }
        })
    }

    func changeOrder() {
        let order = OrderModel(app: self)
        order.close = { [unowned self] in closeDialog(.order) }
        present(.order, L("OrderTitle"), OrderView(m: order), onClose: { [unowned self] in
            if order.changed {
                save()
            }
        })
    }

    func editModes() {
        let modes = ModesModel(app: self)
        modes.close = { [unowned self] in closeDialog(.modes) }
        present(.modes, L("EditModesTitle"), ModesView(m: modes), onClose: { [unowned self] in
            if modes.changed {
                save()
            }
        })
    }
    func changeKey(_ job: KeyJob) {
        if let keyDialog {
            raise(keyDialog)
            return
        }
        // paused so the key being chosen doesn't also fire its job
        unbindKeys()
        let rec = Recorder(app: self, job: job)
        rec.close = { [unowned self] in closeDialog(.key) }
        present(.key, L("KeyTitle", job.name), RecorderView(rec: rec), onClose: { [unowned self] in
            rec.stop()
            keyDialog = nil
            if let key = rec.chosen {
                settings.setKey(key, for: job)
                save()
                updateTooltip()
            }
            bindKeys()
        })
        keyDialog = dialogs[.key]?.window
        rec.start()
    }

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
