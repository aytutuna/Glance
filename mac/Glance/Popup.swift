import AppKit

private final class Flipped: NSView {
    override var isFlipped: Bool { true }
}

// the answer text, drawn and measured the same way so the window fits it exactly
private final class Body: NSView {
    static let options: NSString.DrawingOptions = [.usesLineFragmentOrigin, .usesFontLeading]
    static let blank = NSCursor(image: NSImage(size: NSSize(width: 1, height: 1)), hotSpot: .zero)

    var string = ""
    var font = NSFont.systemFont(ofSize: 15)
    var color = NSColor.white
    var inset: CGFloat = 0
    var textWidth: CGFloat = 0
    var hidesCursor = false

    override var isFlipped: Bool { true }

    override func draw(_ dirtyRect: NSRect) {
        let area = NSRect(x: inset, y: inset, width: textWidth, height: max(bounds.height - 2 * inset, 1))
        (string as NSString).draw(with: area, options: Body.options, attributes: [.font: font, .foregroundColor: color])
    }

    override func resetCursorRects() {
        if hidesCursor {
            addCursorRect(bounds, cursor: Body.blank)
        }
    }

    static func height(_ text: String, font: NSFont, width: CGFloat) -> CGFloat {
        let box = (text as NSString).boundingRect(with: NSSize(width: max(width, 1), height: .greatestFiniteMagnitude), options: options, attributes: [.font: font])
        return ceil(box.height) + 2
    }
}

// Shows the answer without taking the focus. It becomes the key window only while the follow-up field is clicked.
@MainActor
final class AnswerPopup: NSPanel, NSTextFieldDelegate {
    private static let back = NSColor(srgbRed: 32 / 255, green: 32 / 255, blue: 32 / 255, alpha: 1)
    private static let grey = NSColor(srgbRed: 170 / 255, green: 170 / 255, blue: 170 / 255, alpha: 1)

    let screenID: String
    let look: String
    var onClose: (() -> Void)?
    var onAsk: ((String) -> Void)?

    private let centered: Bool
    private let plain: Bool
    private let area: [Double]?
    private let ink: NSColor
    private let backColor: NSColor
    private let root = Flipped()
    private let scroll = NSScrollView()
    private let body = Body()
    private let bar = NSView()
    private let copyButton = NSButton()
    private let closeButton = NSButton()
    private let inputBar = NSView()
    private let field = NSTextField()
    private let access = NSButton()
    private var accessAction: (() -> Void)?
    private var text: String
    private var copyText: String
    private var ask = false
    private var reading: Bool
    private var font: NSFont
    private var fit: NSFont?
    private var pending: String?
    private var lastLayout = ContinuousClock.now.advanced(by: .seconds(-1))
    private var flush: Task<Void, Never>?
    private var reset: Task<Void, Never>?

    static func lookOf(_ s: Settings) -> String {
        let area = (s.answerArea ?? []).map { "\($0)" }.joined(separator: ",")
        return "\(s.plainAnswer)|\(s.useArea)|\(s.centerAnswer)|\(area)"
    }

    init(screen: NSScreen, text: String, reading: Bool, settings: Settings) {
        screenID = screen.uuid
        look = AnswerPopup.lookOf(settings)
        area = settings.useArea ? settings.answerArea : nil
        centered = area == nil && settings.centerAnswer
        plain = settings.plainAnswer
        backColor = plain ? .black : AnswerPopup.back
        ink = plain ? .white : NSColor(srgbRed: 240 / 255, green: 240 / 255, blue: 240 / 255, alpha: 1)
        self.text = text
        copyText = text
        self.reading = reading
        font = .systemFont(ofSize: 15)
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        font = .systemFont(ofSize: 15 * unit)
        if area != nil || plain {
            fit = largestFit()
        }

        isReleasedWhenClosed = false
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
        isFloatingPanel = true
        level = plain ? .mainMenu : .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backgroundColor = backColor
        hasShadow = !plain
        appearance = NSAppearance(named: .darkAqua)
        contentView = root

        root.wantsLayer = true
        root.layer?.backgroundColor = backColor.cgColor
        body.color = fade(settings.textOpacity)
        if plain {
            // nothing but text on black: no buttons, scrollbar or pointer
            body.hidesCursor = true
            root.addSubview(body)
        } else {
            scroll.drawsBackground = false
            scroll.hasVerticalScroller = true
            scroll.scrollerStyle = .overlay
            scroll.documentView = body
            root.addSubview(scroll)
            setUpButtons()
            setUpField()
        }
        applyReading()
        relayout(text)
    }

    override var canBecomeKey: Bool { true }

    // there is no Edit menu in an app without a main menu, so the field would never see paste
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command, let key = event.charactersIgnoringModifiers else {
            return super.performKeyEquivalent(with: event)
        }
        let action: Selector? = switch key {
        case "v": #selector(NSText.paste(_:))
        case "c": #selector(NSText.copy(_:))
        case "x": #selector(NSText.cut(_:))
        case "a": #selector(NSText.selectAll(_:))
        default: nil
        }
        if let action, firstResponder is NSText {
            return NSApp.sendAction(action, to: nil, from: self)
        }
        return super.performKeyEquivalent(with: event)
    }

    // a borderless window is otherwise pushed below the menu bar
    override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }

    var device: String { screenID }

    func present() {
        orderFrontRegardless()
    }

    // copy is what the Copy button takes when the shown text is more than the answer
    func setText(_ text: String, copy: String? = nil, ask: Bool = false) {
        copyText = copy ?? text
        self.ask = ask && !plain
        accessAction = nil
        reading = false
        applyReading()
        resetFit()
        render(text)
    }

    func busy(_ text: String) {
        giveBackFocus()
        ask = false
        accessAction = nil
        reading = true
        applyReading()
        resetFit()
        render(text)
    }

    func needAccess(_ text: String, action: @escaping () -> Void) {
        giveBackFocus()
        ask = false
        accessAction = action
        copyText = text
        reading = false
        applyReading()
        resetFit()
        render(text)
    }

    func setTextOpacity(_ percent: Int) {
        body.color = fade(percent)
        body.needsDisplay = true
    }

    // partial text: relayout at most every 50 ms, and Copy stays off until setText
    func stream(_ partial: String) {
        if lastLayout.duration(to: .now) >= .milliseconds(50) {
            render(partial)
            return
        }
        pending = partial
        if flush == nil {
            flush = Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(50))
                guard !Task.isCancelled, let self else { return }
                flush = nil
                if let pending {
                    render(pending)
                }
            }
        }
    }

    func dismiss() {
        flush?.cancel()
        reset?.cancel()
        giveBackFocus()
        orderOut(nil)
    }

    // the mouse stays in the app the user was in, so giving the keyboard back is only a matter of leaving key status
    private func giveBackFocus() {
        if isKeyWindow {
            makeFirstResponder(nil)
            orderOut(nil)
            orderFrontRegardless()
        }
    }

    private func fade(_ percent: Int) -> NSColor {
        let a = CGFloat(min(max(percent, 0), 100)) / 100
        let from = backColor.usingColorSpace(.sRGB)!, to = ink.usingColorSpace(.sRGB)!
        return NSColor(srgbRed: from.redComponent + (to.redComponent - from.redComponent) * a,
                       green: from.greenComponent + (to.greenComponent - from.greenComponent) * a,
                       blue: from.blueComponent + (to.blueComponent - from.blueComponent) * a, alpha: 1)
    }

    private func setUpButtons() {
        root.addSubview(bar)
        for (button, title, action) in [(copyButton, L("Copy"), #selector(copyClicked)), (closeButton, "×", #selector(closeClicked))] {
            button.isBordered = false
            button.target = self
            button.action = action
            style(button, title, AnswerPopup.grey)
            bar.addSubview(button)
        }
    }

    private func setUpField() {
        root.addSubview(inputBar)
        field.font = font
        field.textColor = ink
        field.drawsBackground = true
        field.backgroundColor = NSColor(srgbRed: 52 / 255, green: 52 / 255, blue: 52 / 255, alpha: 1)
        field.isBordered = false
        field.focusRingType = .none
        field.wantsLayer = true
        field.layer?.borderWidth = 1
        field.layer?.borderColor = NSColor(white: 0.4, alpha: 1).cgColor
        field.placeholderAttributedString = NSAttributedString(string: L("FollowUp"), attributes: [.foregroundColor: NSColor(white: 0.6, alpha: 1), .font: font])
        field.delegate = self
        inputBar.addSubview(field)
        access.bezelStyle = .rounded
        access.title = L("OpenSystemSettings")
        access.target = self
        access.action = #selector(accessClicked)
        inputBar.addSubview(access)
    }

    private func style(_ button: NSButton, _ title: String, _ color: NSColor) {
        button.attributedTitle = NSAttributedString(string: title, attributes: [.font: font, .foregroundColor: color])
    }

    private func applyReading() {
        copyButton.isEnabled = !reading
        style(copyButton, L("Copy"), reading ? NSColor(white: 90 / 255, alpha: 1) : AnswerPopup.grey)
    }

    @objc private func copyClicked() {
        NSPasteboard.general.clearContents()
        style(copyButton, NSPasteboard.general.setString(copyText, forType: .string) ? L("Copied") : L("CopyFailed"), AnswerPopup.grey)
        reset?.cancel()
        reset = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(1500))
            if !Task.isCancelled {
                self?.applyReading()
            }
        }
    }

    @objc private func closeClicked() {
        onClose?()
    }

    @objc private func accessClicked() {
        accessAction?()
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
        if selector == #selector(NSResponder.insertNewline(_:)) {
            let question = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if !question.isEmpty {
                field.stringValue = ""
                onAsk?(question)
            }
            return true
        }
        if selector == #selector(NSResponder.cancelOperation(_:)) {
            if field.stringValue.isEmpty {
                onClose?()
            } else {
                field.stringValue = ""
            }
            return true
        }
        return false
    }

    private var unit: CGFloat { centered ? 1.8 : 1 }

    private var textFont: NSFont { fit ?? font }

    private func largestFit() -> NSFont {
        .systemFont(ofSize: plain ? 120 : 72)
    }

    private func resetFit() {
        if fit != nil {
            fit = largestFit()
        }
    }

    // shrinks only, so text streaming in doesn't make the size jump back up
    private func shrink(_ text: String, width: CGFloat, height: CGFloat) {
        let floor: CGFloat = plain ? 8 : 12
        while let current = fit, current.pointSize > floor, Body.height(text, font: current, width: width) > height {
            fit = .systemFont(ofSize: max(floor, current.pointSize * 0.9))
        }
    }

    private func render(_ text: String) {
        flush?.cancel()
        flush = nil
        pending = nil
        lastLayout = .now
        self.text = text
        relayout(text)
    }

    private func relayout(_ text: String) {
        let bounds = scroll.contentView.bounds
        let follow = bounds.maxY >= body.frame.height - 2
        let screen = NSScreen.with(uuid: screenID) ?? self.screen ?? .primary
        let wa = screen.visibleFrame, b = screen.frame
        let drawn = area.map { Layout.drawn($0, on: b) }
        // plain blacks out the whole screen; the text goes in the drawn area, or across all of it
        let room = plain ? drawn ?? b : drawn
        let margin: CGFloat = 16
        let pad = (plain && drawn == nil ? 72 : 14) * unit
        let width = room?.width ?? min(420 * unit, wa.width - 2 * margin)
        let maxHeight = room?.height ?? wa.height * (centered ? 0.85 : 0.6)
        var inner = width - 2 * pad

        let barHeight = plain ? 0 : 28 * unit
        let inputHeight = ask || accessAction != nil ? 38 * unit : 0
        let chrome = 2 * pad + barHeight + inputHeight
        if room != nil {
            shrink(text, width: inner, height: maxHeight - chrome)
        }
        body.font = textFont
        var textHeight = Body.height(text, font: textFont, width: inner)
        var height = room?.height ?? textHeight + chrome
        // the black screen has no scrollbar, so text that still doesn't fit is cut off
        if textHeight + chrome > maxHeight && plain {
            textHeight = maxHeight - chrome
        } else if textHeight + chrome > maxHeight {
            inner -= 20
            textHeight = Body.height(text, font: textFont, width: inner)
            height = maxHeight
        }

        let target = plain ? b : room ?? (centered ? Layout.centered(size: CGSize(width: width, height: height), in: b)
                                                    : Layout.corner(size: CGSize(width: width, height: height), in: wa, margin: margin))
        setFrame(target, display: false)
        root.frame = NSRect(origin: .zero, size: target.size)

        body.string = text
        body.inset = pad
        body.textWidth = inner
        if plain {
            let at = CGPoint(x: (room?.minX ?? b.minX) - b.minX, y: b.maxY - (room?.maxY ?? b.maxY))
            body.frame = NSRect(x: at.x, y: at.y, width: room?.width ?? b.width, height: room?.height ?? b.height)
            body.needsDisplay = true
            return
        }

        bar.frame = NSRect(x: 0, y: 0, width: width, height: barHeight)
        let closeWidth = 30 * unit, copyWidth = 90 * unit
        closeButton.frame = NSRect(x: width - pad - closeWidth, y: 0, width: closeWidth, height: barHeight)
        copyButton.frame = NSRect(x: closeButton.frame.minX - copyWidth, y: 0, width: copyWidth, height: barHeight)
        for button in [copyButton, closeButton] {
            button.font = font
        }
        applyReading()

        inputBar.isHidden = inputHeight == 0
        inputBar.frame = NSRect(x: 0, y: target.height - inputHeight, width: width, height: inputHeight)
        let fieldHeight = ceil(font.boundingRectForFont.height) + 8
        field.font = font
        field.isHidden = accessAction != nil
        field.frame = NSRect(x: pad, y: (inputHeight - fieldHeight) / 2, width: width - 2 * pad, height: fieldHeight)
        access.isHidden = accessAction == nil
        access.sizeToFit()
        access.frame.origin = CGPoint(x: pad, y: (inputHeight - access.frame.height) / 2)

        scroll.frame = NSRect(x: 0, y: barHeight, width: width, height: target.height - barHeight - inputHeight)
        body.frame = NSRect(x: 0, y: 0, width: width, height: max(textHeight + 2 * pad, scroll.frame.height))
        body.needsDisplay = true
        if follow {
            scroll.contentView.scroll(to: NSPoint(x: 0, y: max(0, body.frame.height - scroll.frame.height)))
            scroll.reflectScrolledClipView(scroll.contentView)
        }
    }
}
