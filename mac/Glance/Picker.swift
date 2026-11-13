import AppKit

private final class PickerView: NSView {
    private static let minSize: CGFloat = 8

    let image: CGImage
    let hint: String
    var finish: ((CGRect?) -> Void)?
    private var start = CGPoint.zero
    private var selection = CGRect.zero
    private var dragging = false

    init(image: CGImage, hint: String) {
        self.image = image
        self.hint = hint
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { nil }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    override func mouseDown(with event: NSEvent) {
        start = convert(event.locationInWindow, from: nil)
        dragging = true
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard dragging else { return }
        let end = convert(event.locationInWindow, from: nil)
        let x = min(max(end.x, 0), bounds.width), y = min(max(end.y, 0), bounds.height)
        let old = selection
        selection = CGRect(x: min(start.x, x), y: min(start.y, y), width: abs(start.x - x), height: abs(start.y - y))
        setNeedsDisplay(old.union(selection).insetBy(dx: -4, dy: -4))
    }

    override func mouseUp(with event: NSEvent) {
        guard dragging else { return }
        dragging = false
        guard selection.width >= PickerView.minSize, selection.height >= PickerView.minSize else {
            finish?(nil)
            return
        }
        // in pixels of the captured image, whatever the scale of the screen
        let sx = CGFloat(image.width) / bounds.width, sy = CGFloat(image.height) / bounds.height
        let pixels = CGRect(x: selection.minX * sx, y: selection.minY * sy, width: selection.width * sx, height: selection.height * sy).integral
            .intersection(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        finish?(pixels.isEmpty ? nil : pixels)
    }

    override func rightMouseDown(with event: NSEvent) {
        finish?(nil)
    }

    override func draw(_ dirtyRect: NSRect) {
        let dim = NSBezierPath(rect: bounds)
        if !selection.isEmpty {
            dim.append(NSBezierPath(rect: selection))
            dim.windingRule = .evenOdd
        }
        NSColor(white: 0, alpha: 120 / 255).setFill()
        dim.fill()

        if !selection.isEmpty {
            NSColor.white.setStroke()
            let frame = NSBezierPath(rect: selection)
            frame.lineWidth = 1.5
            frame.stroke()
        } else if !dragging {
            let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 16), .foregroundColor: NSColor(white: 240 / 255, alpha: 1)]
            let size = (hint as NSString).size(withAttributes: attrs)
            let pad = size.height / 2
            let box = NSRect(x: (bounds.width - size.width) / 2 - pad, y: bounds.height / 8, width: size.width + 2 * pad, height: size.height + pad)
            NSColor(white: 32 / 255, alpha: 1).setFill()
            box.fill()
            (hint as NSString).draw(at: NSPoint(x: box.minX + pad, y: box.minY + pad / 2), withAttributes: attrs)
        }
    }
}

// A full-screen copy of what was captured, to drag a rectangle over. Losing the focus or pressing Esc cancels.
@MainActor
final class AreaPicker: NSPanel {
    private let picker: PickerView
    private var done: ((CGRect?) -> Void)?
    private var active = false

    // the selection is in pixels of the image, from its top left
    init(screen: NSScreen, image: CGImage, hint: String, done: @escaping (CGRect?) -> Void) {
        picker = PickerView(image: image, hint: hint)
        self.done = done
        super.init(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        level = .screenSaver
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backgroundColor = .black
        // the picture sits in a layer so a drag only repaints the dim and the frame
        let backdrop = NSView()
        backdrop.wantsLayer = true
        backdrop.layer?.contents = image
        backdrop.layer?.contentsGravity = .resize
        let root = NSView()
        for view in [backdrop, picker] {
            view.frame = NSRect(origin: .zero, size: screen.frame.size)
            view.autoresizingMask = [.width, .height]
            root.addSubview(view)
        }
        contentView = root
        picker.finish = { [weak self] in self?.end($0) }
        setFrame(screen.frame, display: false)
    }

    override var canBecomeKey: Bool { true }

    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }

    func begin() {
        makeKeyAndOrderFront(nil)
        makeFirstResponder(picker)
    }

    override func becomeKey() {
        super.becomeKey()
        active = true
    }

    override func resignKey() {
        super.resignKey()
        if active {
            end(nil)
        }
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            end(nil)
        }
    }

    func end(_ selection: CGRect?) {
        active = false
        orderOut(nil)
        guard let finish = done else { return }
        done = nil
        finish(selection)
    }
}

// a large number in the middle of a screen for a moment
@MainActor
final class ScreenLabel: NSPanel {
    var onDone: ((ScreenLabel) -> Void)?
    private var timer: Task<Void, Never>?

    init(screen: NSScreen, number: Int) {
        let size: CGFloat = 240
        let b = screen.frame
        super.init(contentRect: NSRect(x: b.midX - size / 2, y: b.midY - size / 2, width: size, height: size),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        ignoresMouseEvents = true
        hasShadow = false
        isOpaque = false
        level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 2)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backgroundColor = NSColor(white: 32 / 255, alpha: 0.92)
        contentView = NumberView(number: number)
        orderFrontRegardless()
        timer = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            if !Task.isCancelled, let self {
                dismiss()
            }
        }
    }

    func dismiss() {
        timer?.cancel()
        orderOut(nil)
        onDone?(self)
        onDone = nil
    }
}

private final class NumberView: NSView {
    let number: String

    init(number: Int) {
        self.number = String(number)
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: 160), .foregroundColor: NSColor.white]
        let size = (number as NSString).size(withAttributes: attrs)
        (number as NSString).draw(at: NSPoint(x: (bounds.width - size.width) / 2, y: (bounds.height - size.height) / 2), withAttributes: attrs)
    }
}
