import AppKit
import Carbon

enum KeyJob: CaseIterable {
    case read, region, close, hide

    var name: String {
        switch self {
        case .read: L("JobRead")
        case .region: L("JobArea")
        case .close: L("JobClose")
        case .hide: L("JobHide")
        }
    }

    var id: UInt32 {
        switch self {
        case .read: 1
        case .close: 2
        case .region: 3
        case .hide: 4
        }
    }

    init?(id: UInt32) {
        guard let job = KeyJob.allCases.first(where: { $0.id == id }) else { return nil }
        self = job
    }
}

extension Hotkey {
    // RegisterEventHotKey accepts some of the keys macOS itself uses, such as the screenshot shortcuts, and never delivers them
    var usedBySystem: Bool {
        var list: Unmanaged<CFArray>?
        guard CopySymbolicHotKeys(&list) == noErr, let entries = list?.takeRetainedValue() as? [[String: Any]] else { return false }
        var flags: UInt = 0
        for (carbon, flag) in [(controlKey, NSEvent.ModifierFlags.control), (optionKey, .option), (shiftKey, .shift), (cmdKey, .command)] where mods & UInt32(carbon) != 0 {
            flags |= flag.rawValue
        }
        return entries.contains {
            $0[kHISymbolicHotKeyEnabled as String] as? Bool == true
                && ($0[kHISymbolicHotKeyCode as String] as? NSNumber)?.uint32Value == code
                && ($0[kHISymbolicHotKeyModifiers as String] as? NSNumber)?.uintValue == flags
        }
    }
}

extension Settings {
    func key(for job: KeyJob) -> Hotkey {
        switch job {
        case .read: hotkey
        case .region: regionKey
        case .close: closeKey
        case .hide: hideKey
        }
    }

    mutating func setKey(_ key: Hotkey, for job: KeyJob) {
        switch job {
        case .read: hotkey = key
        case .region: regionKey = key
        case .close: closeKey = key
        case .hide: hideKey = key
        }
    }
}

// Carbon hot keys need no Accessibility or Input Monitoring permission
@MainActor
final class Hotkeys {
    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var handler: EventHandlerRef?
    var pressed: ((KeyJob) -> Void)?

    init() {
        var kind = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, data in
            var key = EventHotKeyID()
            guard let event, let data,
                  GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                                    MemoryLayout<EventHotKeyID>.size, nil, &key) == noErr else { return OSStatus(eventNotHandledErr) }
            let hotkeys = Unmanaged<Hotkeys>.fromOpaque(data).takeUnretainedValue()
            MainActor.assumeIsolated {
                if let job = KeyJob(id: key.id) {
                    hotkeys.pressed?(job)
                }
            }
            return noErr
        }, 1, &kind, Unmanaged.passUnretained(self).toOpaque(), &handler)
    }

    func register(_ job: KeyJob, _ key: Hotkey) -> Bool {
        if let old = refs.removeValue(forKey: job.id) {
            UnregisterEventHotKey(old)
        }
        var ref: EventHotKeyRef?
        let id = EventHotKeyID(signature: OSType(0x474C_4E43), id: job.id)
        guard RegisterEventHotKey(key.code, key.mods, id, GetApplicationEventTarget(), 0, &ref) == noErr, let ref else { return false }
        refs[job.id] = ref
        return true
    }

    func unregisterAll() {
        for ref in refs.values {
            UnregisterEventHotKey(ref)
        }
        refs = [:]
    }
}
