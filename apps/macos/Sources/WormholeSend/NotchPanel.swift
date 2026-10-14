import AppKit
import SwiftUI

/// A borderless panel above the menu bar that never takes focus. It has a
/// fixed size; the island is drawn at its top center and everything around
/// it is transparent, which the window server lets clicks pass through.
final class NotchPanel: NSPanel {
    init(model: IslandModel) {
        super.init(
            contentRect: NSRect(origin: .zero, size: Spec.panelSize),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)
        isFloatingPanel = true
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        isMovable = false
        hidesOnDeactivate = false
        becomesKeyOnlyIfNeeded = true
        isReleasedWhenClosed = false
        contentView = DropHostingView(model: model)
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    /// Puts the panel at the top center of `screen`.
    func place(on screen: NSScreen) {
        let frame = NSRect(
            x: screen.frame.midX - Spec.panelSize.width / 2,
            y: screen.frame.maxY - Spec.panelSize.height,
            width: Spec.panelSize.width,
            height: Spec.panelSize.height)
        setFrame(frame, display: true)
    }
}

/// Hosts the SwiftUI island and is the drop destination for files.
final class DropHostingView: NSHostingView<IslandView> {
    private let model: IslandModel

    init(model: IslandModel) {
        self.model = model
        super.init(rootView: IslandView(model: model))
        // Text too: VS Code gives the paths it drags as text (DragPaths).
        registerForDraggedTypes([.fileURL, .string])
    }

    @available(*, unavailable)
    required init(rootView: IslandView) {
        fatalError("init(rootView:) is not supported")
    }

    @available(*, unavailable)
    @MainActor required dynamic init?(coder: NSCoder) {
        fatalError("init(coder:) is not supported")
    }

    /// Buttons react to the first click although the panel never is key.
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func draggingEntered(_ sender: any NSDraggingInfo) -> NSDragOperation {
        track(sender)
    }

    override func draggingUpdated(_ sender: any NSDraggingInfo) -> NSDragOperation {
        track(sender)
    }

    override func draggingExited(_ sender: (any NSDraggingInfo)?) {
        model.dragLeftPanel()
    }

    override func prepareForDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        model.canDrop
    }

    override func performDragOperation(_ sender: any NSDraggingInfo) -> Bool {
        model.drop(DragPaths.of(sender.draggingPasteboard) ?? [])
    }

    private func track(_ sender: any NSDraggingInfo) -> NSDragOperation {
        let local = convert(sender.draggingLocation, from: nil)
        let point = CGPoint(x: local.x, y: isFlipped ? local.y : bounds.height - local.y)
        model.dragMoved(to: point, panelWidth: bounds.width)
        return model.canDrop ? .copy : []
    }
}
