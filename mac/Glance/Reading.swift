import AppKit

extension AppDelegate {
    func trigger(after delay: Duration, area: Bool = false) {
        if let keyDialog {
            NSApp.activate()
            keyDialog.makeKeyAndOrderFront(nil)
            return
        }
        if let picker {
            picker.makeKeyAndOrderFront(nil)
            return
        }
        start { try await self.read(after: delay, area: area) }
    }

    // a new job cancels the running one
    func start(_ job: @escaping () async throws -> Void) {
        current?.cancel()
        serial += 1
        let mine = serial
        setBusy(true)
        current = Task {
            do {
                try await job()
            } catch is CancellationError {
            } catch {
                Log.write("job failed", error)
                if !Task.isCancelled {
                    let text = (error as? AiError)?.message ?? L("Crashed")
                    showText(thread + text, copy: text, ask: !thread.isEmpty)
                }
            }
            if serial == mine {
                setBusy(false)
                current = nil
            }
        }
    }

    private func read(after delay: Duration, area: Bool) async throws {
        // taken first: closing the popup or waiting can change the frontmost app
        let window = settings.readDevice == AppDelegate.activeWindow && !area ? activeWindow() : nil
        var screen = window.map { NSScreen.holding($0.frame) } ?? NSScreen.with(uuid: settings.readDevice) ?? .underMouse
        // an area is drawn on one screen, so it falls back to the screen with the mouse; so does a window that can't be read
        let all = settings.readDevice == AppDelegate.allScreens && !area
        let open = popup
        // the pointer can rest on the answer screen; reading that one would only show the answer, so read another
        if window == nil, !all, let open, open.device == screen.uuid, settings.readDevice != open.device {
            let others = NSScreen.screens.filter { $0.uuid != open.device }
            screen = others.first { $0 == NSScreen.primary } ?? others.first ?? screen
        }
        // an answer on another screen stays up while the next one is read
        let clear = window.map { open?.frame.intersects($0.frame) == false } ?? (open?.device != screen.uuid)
        let keep = !all && open != nil && clear && open?.look == AnswerPopup.lookOf(settings)
        if !keep {
            closePopup()
        }
        closeLabels()
        guard hasAccess() else { return }
        let provider = try Ai.create(settings)
        let model = settings.activeModel()
        try await Task.sleep(for: delay)

        guard let shot = try await grab(screen: screen, all: all, window: window) else { return }
        try Task.checkCancellation()
        var crop: CGRect?
        if area {
            guard let picked = await pick(on: screen, image: shot, hint: L("DragReadArea")) else { return }
            crop = picked
        }
        let edge = all ? Capture.wideEdge : Capture.maxEdge
        let jpeg = try await Task.detached { try Capture.jpeg(shot, crop: crop, maxEdge: edge) }.value
        try Task.checkCancellation()

        let mine: AnswerPopup
        if keep, let kept = popup {
            chat = nil
            thread = ""
            shown = ""
            kept.busy(L("Reading"))
            mine = kept
        } else {
            closePopup()
            mine = newPopup(L("Reading"), reading: true)
            popup = mine
        }
        let chat = Conversation(jpeg: jpeg, model: model, mode: settings.mode, instruction: Prompts.instruction(settings, settings.mode), provider: provider)
        try await reply(chat, mine, first: true)
    }

    // nil when the capture failed in a way that has been reported
    private func grab(screen: NSScreen, all: Bool, window: WindowTarget?) async throws -> CGImage? {
        do {
            if all {
                return try await Capture.everything()
            }
            if let window, let shot = try? await Capture.window(window) {
                return shot
            }
            return try await Capture.screen(screen)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            Log.write("capture", error)
            if CGPreflightScreenCaptureAccess() {
                throw AiError(L("CaptureFailed"))
            }
            showAccessNeeded()
            return nil
        }
    }

    private func hasAccess() -> Bool {
        if CGPreflightScreenCaptureAccess() {
            return true
        }
        _ = CGRequestScreenCaptureAccess()
        showAccessNeeded()
        return false
    }

    private func showAccessNeeded() {
        let text = L("ScreenAccess")
        // the black screen would sit above System Settings
        if settings.plainAnswer {
            notify(text)
            return
        }
        shown = text
        let shownPopup = popup ?? newPopup(text, reading: false)
        popup = shownPopup
        shownPopup.needAccess(text) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
        }
    }

    private func reply(_ chat: Conversation, _ mine: AnswerPopup, first: Bool) async throws {
        let request = chat.request()
        let provider = chat.provider
        let run = serial
        // each partial carries the whole text so far, so only the newest one matters
        let (partials, feed) = AsyncStream.makeStream(of: String.self, bufferingPolicy: .bufferingNewest(1))
        let pump = Task {
            for await partial in partials {
                let text = clean(partial)
                if !text.isEmpty && popup === mine && serial == run {
                    mine.stream(thread + text)
                }
            }
        }
        let answer: String
        do {
            answer = clean(try await provider.ask(request) { feed.yield($0) })
        } catch {
            feed.finish()
            await pump.value
            throw error
        }
        feed.finish()
        await pump.value
        try Task.checkCancellation()
        if answer.isEmpty {
            throw AiError(L("AnswerEmpty"))
        }
        chat.complete(answer)
        self.chat = chat
        if first {
            history = Array(([answer] + history).prefix(History.limit))
            if settings.keepHistory {
                History.save(history)
            }
        }
        showText(thread + answer, copy: answer, ask: true)
    }

    private func follow(_ question: String) {
        guard let chat, let mine = popup else { return }
        thread = shown + "\n\n" + L("QuestionPrefix") + question + "\n\n"
        chat.ask(question)
        mine.busy(thread + L("Reading"))
        start { try await self.reply(chat, mine, first: false) }
    }

    // markers can be split across chunks, so this runs on the whole text
    private func clean(_ text: String) -> String {
        text.replacingOccurrences(of: "**", with: "").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func showText(_ text: String, copy: String? = nil, ask: Bool = false) {
        shown = text
        let target = popup ?? newPopup(text, reading: false)
        popup = target
        target.setText(text, copy: copy, ask: ask)
    }

    private func newPopup(_ text: String, reading: Bool) -> AnswerPopup {
        let made = AnswerPopup(screen: NSScreen.with(uuid: settings.showDevice) ?? .underMouse, text: text, reading: reading, settings: settings)
        made.onClose = { [weak self] in self?.closeAnswer() }
        made.onAsk = { [weak self] in self?.follow($0) }
        made.present()
        return made
    }

    func closePopup() {
        popup?.dismiss()
        popup = nil
        chat = nil
        thread = ""
        shown = ""
    }

    func closeAnswer() {
        serial += 1
        current?.cancel()
        current = nil
        closePopup()
        setBusy(false)
    }

    // stops any answer and leaves the answer screen up, black and empty
    func hideAnswer() {
        serial += 1
        current?.cancel()
        current = nil
        setBusy(false)
        chat = nil
        thread = ""
        shown = ""
        if let popup {
            popup.setText("")
        } else if settings.plainAnswer {
            popup = newPopup("", reading: false)
        }
    }

    func drawArea() {
        if let keyDialog {
            keyDialog.makeKeyAndOrderFront(nil)
            return
        }
        if let picker {
            picker.makeKeyAndOrderFront(nil)
            return
        }
        start {
            self.closePopup()
            self.closeLabels()
            guard self.hasAccess() else { return }
            let screen = NSScreen.with(uuid: self.settings.showDevice) ?? .underMouse
            // the menu has to be gone before the screen is captured
            try await Task.sleep(for: .milliseconds(300))
            guard let shot = try await self.grab(screen: screen, all: false, window: nil) else { return }
            try Task.checkCancellation()
            guard let drawn = await self.pick(on: screen, image: shot, hint: L("DragAnswerArea")) else { return }
            self.settings.answerArea = Layout.fractions(of: drawn, in: CGSize(width: shot.width, height: shot.height))
            self.settings.useArea = true
            self.save()
            self.popup = self.newPopup(L("AnswersHere"), reading: false)
        }
    }

    func identify() {
        closeLabels()
        for (i, screen) in NSScreen.screens.enumerated() {
            let label = ScreenLabel(screen: screen, number: i + 1)
            label.onDone = { [weak self] done in self?.labels.removeAll { $0 === done } }
            labels.append(label)
        }
    }

    func closeLabels() {
        for label in labels {
            label.dismiss()
        }
    }

    // the frozen picture is shown on top of the screen; nil if it was cancelled
    private func pick(on screen: NSScreen, image: CGImage, hint: String) async -> CGRect? {
        await withTaskCancellationHandler {
            await withCheckedContinuation { done in
                let frozen = AreaPicker(screen: screen, image: image, hint: hint) {
                    self.picker = nil
                    done.resume(returning: $0)
                }
                picker = frozen
                frozen.begin()
                if Task.isCancelled {
                    frozen.end(nil)
                }
            }
        } onCancel: {
            Task { @MainActor in self.picker?.end(nil) }
        }
    }
}
