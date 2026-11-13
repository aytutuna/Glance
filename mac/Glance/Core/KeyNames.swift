import Carbon
import Foundation

extension Hotkey {
    private static let named: [Int: String] = [
        kVK_Return: "↩", kVK_Tab: "⇥", kVK_Space: "Space", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦", kVK_Escape: "⎋",
        kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6", kVK_F7: "F7",
        kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12", kVK_F13: "F13",
        kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17", kVK_F18: "F18", kVK_F19: "F19", kVK_F20: "F20",
    ]

    // the way menus write a shortcut, such as ⌃⇧Q
    var name: String {
        var text = ""
        for (flag, symbol) in [(controlKey, "⌃"), (optionKey, "⌥"), (shiftKey, "⇧"), (cmdKey, "⌘")] where mods & UInt32(flag) != 0 {
            text += symbol
        }
        return text + (Hotkey.named[Int(code)] ?? Hotkey.letter(code) ?? "#\(code)")
    }

    // the key code is a position on the keyboard, so the letter depends on the layout in use
    private static func letter(_ code: UInt32) -> String? {
        for copy in [TISCopyCurrentKeyboardLayoutInputSource, TISCopyCurrentASCIICapableKeyboardLayoutInputSource] {
            guard let source = copy()?.takeRetainedValue(),
                  let raw = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { continue }
            let layout = Unmanaged<CFData>.fromOpaque(raw).takeUnretainedValue() as Data
            var dead: UInt32 = 0
            var length = 0
            var chars = [UniChar](repeating: 0, count: 4)
            let status = layout.withUnsafeBytes { buffer in
                UCKeyTranslate(buffer.bindMemory(to: UCKeyboardLayout.self).baseAddress!, UInt16(code), UInt16(kUCKeyActionDisplay), 0,
                               UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysBit), &dead, chars.count, &length, &chars)
            }
            let text = String(utf16CodeUnits: chars, count: length).uppercased()
            if status == noErr, let first = text.unicodeScalars.first, first.value > 32 {
                return text
            }
        }
        return nil
    }
}
