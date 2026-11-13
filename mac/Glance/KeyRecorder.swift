import Carbon.HIToolbox
import SwiftUI

@MainActor
final class Recorder: ObservableObject {
    unowned let app: AppDelegate
    let job: KeyJob
    @Published var status = ""
    var chosen: Hotkey?
    var close: () -> Void = {}
    private var monitor: Any?

    init(app: AppDelegate, job: KeyJob) {
        self.app = app
        self.job = job
    }

    var purpose: String {
        switch job {
        case .read: L("PurposeRead")
        case .region: L("ReadArea")
        case .close: L("PurposeClose")
        case .hide: L("PurposeHide")
        }
    }

    // every key goes to the recorder while its window is in front, shortcuts included
    func start() {
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, event.window === app.keyDialog else { return event }
            handle(event)
            return nil
        }
    }

    func stop() {
        if let monitor {
            NSEvent.removeMonitor(monitor)
        }
        monitor = nil
    }

    private func handle(_ event: NSEvent) {
        guard chosen == nil, !event.isARepeat else { return }
        let flags = event.modifierFlags
        var mods: UInt32 = 0
        for (flag, bit) in [(NSEvent.ModifierFlags.control, controlKey), (.option, optionKey), (.shift, shiftKey), (.command, cmdKey)] where flags.contains(flag) {
            mods |= UInt32(bit)
        }
        if event.keyCode == UInt16(kVK_Escape) && mods == 0 {
            close()
            return
        }
        let key = Hotkey(code: UInt32(event.keyCode), mods: mods)
        if !key.isValid {
            status = L("KeyNeedsMod")
        } else if mods == UInt32(cmdKey) && !Hotkey.fKeys.contains(key.code) {
            status = L("KeyCmdOnly")
        } else if let owner = KeyJob.allCases.first(where: { $0 != job && app.settings.key(for: $0) == key }) {
            status = L("KeyTaken", key.name, owner.name)
        } else if !app.canHold(job, key) {
            status = L("KeyHeld", key.name)
        } else {
            chosen = key
            close()
        }
    }
}

struct RecorderView: View {
    @ObservedObject var rec: Recorder

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(L("PressKeyFor", rec.purpose))
                .font(.title3)
            Text(L("KeyHint"))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text(rec.status)
                .foregroundStyle(.red)
                .frame(minHeight: 36, alignment: .topLeading)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityIdentifier("recorder.status")
            HStack {
                Spacer()
                Button(L("Cancel")) { rec.close() }
            }
        }
        .padding(16)
        .frame(width: 380)
    }
}
