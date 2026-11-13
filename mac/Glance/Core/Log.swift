import Foundation

enum AppDir {
    static let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Glance", isDirectory: true)
}

enum Log {
    private static let lock = NSLock()

    static func write(_ what: String, _ error: Error) {
        lock.lock()
        defer { lock.unlock() }
        let fm = FileManager.default
        let file = AppDir.url.appendingPathComponent("glance.log")
        let stamp = DateFormatter()
        stamp.locale = Locale(identifier: "en_US_POSIX")
        stamp.dateFormat = "yyyy-MM-dd HH:mm:ss"
        let line = Data("\(stamp.string(from: Date())) \(what): \(error)\n".utf8)
        do {
            try fm.createDirectory(at: AppDir.url, withIntermediateDirectories: true)
            if let size = try? fm.attributesOfItem(atPath: file.path)[.size] as? Int, size > 1_000_000 {
                let old = AppDir.url.appendingPathComponent("glance.old.log")
                try? fm.removeItem(at: old)
                try fm.moveItem(at: file, to: old)
            }
            if !fm.fileExists(atPath: file.path) {
                fm.createFile(atPath: file.path, contents: nil)
            }
            let handle = try FileHandle(forWritingTo: file)
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: line)
        } catch {
        }
    }
}
