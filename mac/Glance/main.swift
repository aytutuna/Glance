import AppKit

// a second launch tells the running one and leaves
let others = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
    .filter { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }
if !others.isEmpty {
    DistributedNotificationCenter.default().postNotificationName(AppDelegate.showRunning, object: nil, userInfo: nil, deliverImmediately: true)
    RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.3))
    exit(0)
}

NSSetUncaughtExceptionHandler { exception in
    Log.write("unhandled", NSError(domain: exception.name.rawValue, code: 0, userInfo: [NSLocalizedDescriptionKey: exception.reason ?? ""]))
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
