import AppKit

/// Notices file drags anywhere on screen, before they reach the island.
///
/// A global mouse-down monitor (which needs no Accessibility permission)
/// starts polling while the button is held: a change of the drag
/// pasteboard carrying file URLs means a file drag began; the button going
/// up ends it. Polling the button state instead of waiting for mouse-up
/// events also works while another app runs its drag session.
@MainActor
final class DragMonitor {
    var onStart: (([URL], NSPoint) -> Void)?
    var onEnd: (() -> Void)?

    private var monitor: Any?
    private var poll: Task<Void, Never>?
    private var changeCount = NSPasteboard(name: .drag).changeCount
    private(set) var isDragging = false

    func start() {
        monitor = NSEvent.addGlobalMonitorForEvents(matching: .leftMouseDown) { [weak self] _ in
            MainActor.assumeIsolated { self?.mouseDown() }
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        poll?.cancel()
    }

    private func mouseDown() {
        changeCount = NSPasteboard(name: .drag).changeCount
        poll?.cancel()
        poll = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(40))
                guard let self, self.tick() else { return }
            }
        }
    }

    /// One poll step; returns whether to keep polling.
    private func tick() -> Bool {
        let pressed = NSEvent.pressedMouseButtons & 1 != 0
        if isDragging {
            if pressed { return true }
            isDragging = false
            // The drop reaches the panel around the same moment; let it land
            // first so a successful drop isn't mistaken for a cancelled drag.
            Task { [weak self] in
                try? await Task.sleep(for: .milliseconds(150))
                self?.onEnd?()
            }
            return false
        }
        guard pressed else { return false }
        let pasteboard = NSPasteboard(name: .drag)
        guard pasteboard.changeCount != changeCount else { return true }
        changeCount = pasteboard.changeCount
        let urls = pasteboard.readObjects(
            forClasses: [NSURL.self],
            options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []
        guard !urls.isEmpty else { return true }
        isDragging = true
        onStart?(urls, NSEvent.mouseLocation)
        return true
    }
}
