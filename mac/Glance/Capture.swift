import AppKit
import ScreenCaptureKit
import UniformTypeIdentifiers

extension NSScreen {
    var displayID: CGDirectDisplayID {
        deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID ?? 0
    }

    // stays the same when the screens are rearranged or the Mac sleeps, unlike the display id
    var uuid: String {
        guard let id = CGDisplayCreateUUIDFromDisplayID(displayID)?.takeRetainedValue() else { return String(displayID) }
        return CFUUIDCreateString(nil, id) as String
    }

    // the one with the menu bar
    static var primary: NSScreen { screens[0] }

    static func with(uuid: String?) -> NSScreen? {
        uuid.flatMap { id in screens.first { $0.uuid == id } }
    }

    static var underMouse: NSScreen {
        let point = NSEvent.mouseLocation
        return screens.first { NSMouseInRect(point, $0.frame, false) } ?? primary
    }

    // the screen a window mostly sits on
    static func holding(_ frame: CGRect) -> NSScreen {
        screens.max { $0.frame.intersection(frame).area < $1.frame.intersection(frame).area } ?? primary
    }

    func describe(_ index: Int) -> String {
        L(self == NSScreen.primary ? "ScreenPrimary" : "ScreenPlain", index + 1, Int(frame.width), Int(frame.height))
    }
}

private extension CGRect {
    var area: CGFloat { isNull ? 0 : width * height }
}

struct WindowTarget {
    let id: CGWindowID
    // screen coordinates, origin at the bottom left
    let frame: CGRect
}

@MainActor
enum Capture {
    static let maxEdge = 2000
    // two full HD screens side by side stay at full size
    static let wideEdge = 4000

    static func screen(_ screen: NSScreen) async throws -> CGImage {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == screen.displayID }) else { throw AiError(L("CaptureFailed")) }
        // Glance's own windows stay out of the picture, which is what keeps an answer from being read back
        let own = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        return try await shoot(SCContentFilter(display: display, excludingApplications: own, exceptingWindows: []))
    }

    // every screen drawn into one image at the positions they have on the desktop
    static func everything() async throws -> CGImage {
        let screens = NSScreen.screens
        let union = screens.map(\.frame).reduce(CGRect.null) { $0.union($1) }
        let scale = min(screens.map(\.backingScaleFactor).max() ?? 1, CGFloat(wideEdge) / max(union.width, union.height))
        guard let context = bitmap(Int(union.width * scale), Int(union.height * scale)) else { throw AiError(L("CaptureFailed")) }
        for s in screens {
            let image = try await Capture.screen(s)
            context.draw(image, in: CGRect(x: (s.frame.minX - union.minX) * scale, y: (s.frame.minY - union.minY) * scale,
                                           width: s.frame.width * scale, height: s.frame.height * scale))
        }
        guard let all = context.makeImage() else { throw AiError(L("CaptureFailed")) }
        return all
    }

    // nil when the window is gone or can't be captured, so the caller reads the screen instead
    static func window(_ target: WindowTarget) async throws -> CGImage? {
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
        guard let found = content.windows.first(where: { $0.windowID == target.id }) else { return nil }
        return try await shoot(SCContentFilter(desktopIndependentWindow: found))
    }

    // the topmost ordinary window of the first app that has one
    static func frontWindow(ofApps pids: [pid_t]) -> WindowTarget? {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return nil }
        let top = NSScreen.primary.frame.height
        for pid in pids {
            for w in list where w[kCGWindowOwnerPID as String] as? pid_t == pid && w[kCGWindowLayer as String] as? Int == 0 {
                guard let dict = w[kCGWindowBounds as String] as? NSDictionary, let rect = CGRect(dictionaryRepresentation: dict),
                      let id = w[kCGWindowNumber as String] as? CGWindowID, (w[kCGWindowAlpha as String] as? Double ?? 1) > 0,
                      rect.width >= 40, rect.height >= 40 else { continue }
                return WindowTarget(id: id, frame: CGRect(x: rect.minX, y: top - rect.maxY, width: rect.width, height: rect.height))
            }
        }
        return nil
    }

    // Scaled down only, and drawn through a plain RGB bitmap so the JPEG never has an alpha channel.
    nonisolated static func jpeg(_ image: CGImage, crop: CGRect? = nil, maxEdge: Int) throws -> Data {
        let part = crop.flatMap { image.cropping(to: $0) } ?? image
        let scale = min(1, CGFloat(maxEdge) / CGFloat(max(part.width, part.height)))
        let w = max(1, Int((CGFloat(part.width) * scale).rounded())), h = max(1, Int((CGFloat(part.height) * scale).rounded()))
        guard let context = bitmap(w, h) else { throw AiError(L("CaptureFailed")) }
        context.draw(part, in: CGRect(x: 0, y: 0, width: w, height: h))
        let data = NSMutableData()
        guard let small = context.makeImage(), let out = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else {
            throw AiError(L("CaptureFailed"))
        }
        CGImageDestinationAddImage(out, small, [kCGImageDestinationLossyCompressionQuality: 0.9] as CFDictionary)
        guard CGImageDestinationFinalize(out) else { throw AiError(L("CaptureFailed")) }
        return data as Data
    }

    private static func shoot(_ filter: SCContentFilter) async throws -> CGImage {
        let info = SCShareableContent.info(for: filter)
        let config = SCStreamConfiguration()
        config.width = Int(info.contentRect.width * CGFloat(info.pointPixelScale))
        config.height = Int(info.contentRect.height * CGFloat(info.pointPixelScale))
        config.showsCursor = false
        return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    }

    private nonisolated static func bitmap(_ w: Int, _ h: Int) -> CGContext? {
        let context = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
        context?.interpolationQuality = .high
        return context
    }
}
