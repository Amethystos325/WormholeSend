import AppKit
import Observation

/// The island's state machine (design doc, section 2.1), driven by drag
/// events from the panel and the drag monitor. Transfers are simulated.
@MainActor
@Observable
final class IslandModel {
    enum Phase: Equatable {
        case idle
        /// A file drag is in progress somewhere on screen.
        case hint
        /// Over the island with the default device online: release sends to
        /// it, dragging down into the "其他设备" strip opens the list.
        case target
        case picker(hot: Int?)
        case unavailable(String)
        case sending
        case queued(position: Int)
        case done
        case failed(String)
        case refused(String)
        case receiving
        case received

        /// Phases in which the island is the drop target under the cursor.
        var isOverIsland: Bool {
            switch self {
            case .target, .picker, .unavailable: true
            default: false
            }
        }
    }

    enum SendOutcome: String, CaseIterable {
        case success = "成功"
        case busy = "对方忙（排队）"
        case failure = "失败"
    }

    var phase: Phase = .idle {
        didSet {
            if phase != oldValue { onPhaseChange?(phase) }
        }
    }
    var mode: ScreenMode = .capsule
    var devices: [Device] = Device.samples
    var dragLabel = ""
    var transfer: Transfer?
    /// The queued state shrinks to a compact badge after a few seconds.
    var queueCollapsed = false
    var hovering = false {
        didSet {
            if phase == .received, !hovering { holdReceived() }
        }
    }
    var nextOutcome: SendOutcome = .success

    @ObservationIgnored var onPhaseChange: ((Phase) -> Void)?
    @ObservationIgnored private var dragFiles: [URL] = []
    /// Set once the cursor reached the default device row in the target
    /// state. The cursor usually comes up from below, through where the strip
    /// appears; only a move back down into the strip opens the list.
    @ObservationIgnored private var armedForPicker = false
    @ObservationIgnored private var timer: Task<Void, Never>?
    @ObservationIgnored private var progressTask: Task<Void, Never>?

    var defaultDevice: Device? { devices.first { $0.isDefault } }
    var onlineDevices: [Device] { devices.filter(\.online) }
    var isBusy: Bool {
        switch phase {
        case .sending, .queued, .receiving: true
        default: false
        }
    }

    // MARK: - Drag input

    func dragStarted(files: [URL]) {
        guard !isBusy else { return }
        cancelTimers()
        dragFiles = files
        dragLabel = FileLabel.of(files)
        phase = .hint
    }

    /// The cursor moved over the panel; `point` is in panel coordinates with
    /// the origin at the top left.
    func dragMoved(to point: CGPoint, panelWidth: CGFloat) {
        let rect = islandRect(panelWidth: panelWidth).insetBy(dx: -Spec.hitOutset, dy: -Spec.hitOutset)
        let over = rect.contains(point)
        if over, phase == .hint {
            enterIsland()
        } else if !over, phase.isOverIsland {
            leaveIsland()
        }
        if phase == .target {
            let stripTop = islandRect(panelWidth: panelWidth).maxY - Spec.moreStripHeight
            if point.y < stripTop - 4 {
                armedForPicker = true
            } else if armedForPicker {
                phase = .picker(hot: nil)
            }
        }
        if case .picker = phase, devices.count > 1 {
            let body = islandRect(panelWidth: panelWidth)
            let columnWidth = (body.width - 28) / CGFloat(devices.count)
            let index = Int((point.x - body.minX - 14) / columnWidth)
            phase = .picker(hot: min(max(index, 0), devices.count - 1))
        }
    }

    func dragLeftPanel() {
        if phase.isOverIsland { leaveIsland() }
    }

    /// The mouse button was released. A drop, if any, has been handled.
    func dragEnded() {
        switch phase {
        case .hint, .target, .picker, .unavailable:
            cancelTimers()
            phase = .idle
        default:
            break
        }
    }

    var canDrop: Bool {
        switch phase {
        case .target, .picker: true
        default: false
        }
    }

    /// Returns whether the drop was taken (otherwise the files fly back).
    func drop(_ urls: [URL]) -> Bool {
        let files = urls.isEmpty ? dragFiles : urls
        dragLabel = FileLabel.of(files)
        switch phase {
        case .target:
            guard let device = defaultDevice else { return false }
            send(files, to: device)
            return true
        case .picker(let hot):
            guard let index = devices.count == 1 ? 0 : hot else {
                phase = .idle
                return false
            }
            let device = devices[index]
            guard device.online else {
                refuse(device)
                return false
            }
            send(files, to: device)
            return true
        default:
            return false
        }
    }

    private func enterIsland() {
        if devices.isEmpty {
            phase = .unavailable("还没有配对设备 · 点击配对")
        } else if onlineDevices.isEmpty {
            phase = .unavailable(devices.count == 1 ? "\(devices[0].name) 离线，无法发送" : "没有在线的设备")
        } else if devices.count == 1 {
            phase = .picker(hot: 0)
        } else if defaultDevice?.online == true {
            armedForPicker = false
            phase = .target
        } else {
            phase = .picker(hot: nil)
        }
    }

    private func leaveIsland() {
        cancelTimers()
        phase = .hint
    }

    // MARK: - Hint text

    var hintText: String {
        if devices.isEmpty { return "还没有配对设备" }
        if devices.count == 1, !devices[0].online { return "\(devices[0].name) 离线，无法发送" }
        if let device = defaultDevice, device.online { return "移到这里发送到 \(device.name)" }
        return "默认设备离线，移到这里选其他设备"
    }

    // MARK: - Simulated transfers

    private func send(_ files: [URL], to device: Device) {
        cancelTimers()
        transfer = Transfer(device: device, files: files, label: FileLabel.of(files))
        switch nextOutcome {
        case .success:
            startSending(failAt: nil)
        case .failure:
            startSending(failAt: 0.42)
        case .busy:
            queueCollapsed = false
            phase = .queued(position: 1)
            schedule(after: Spec.queuedHint) { [weak self] in
                self?.queueCollapsed = true
                self?.schedule(after: .seconds(4)) { [weak self] in self?.startSending(failAt: nil) }
            }
        }
    }

    private func startSending(failAt: Double?) {
        transfer?.progress = 0
        phase = .sending
        animateProgress(over: 2.4, failAt: failAt) { [weak self] in
            guard let self else { return }
            self.phase = .done
            self.schedule(after: Spec.doneHold) { [weak self] in self?.phase = .idle }
        }
    }

    private func refuse(_ device: Device) {
        cancelTimers()
        phase = .refused(device.name)
        schedule(after: Spec.refusedHold) { [weak self] in self?.phase = .idle }
    }

    func cancelTransfer() {
        cancelTimers()
        phase = .idle
    }

    func retry() {
        guard transfer != nil else { return }
        cancelTimers()
        startSending(failAt: nil)
    }

    func simulateReceive() {
        guard phase == .idle else { return }
        let files = DemoFiles.make()
        transfer = Transfer(device: devices.first { $0.id == "air" } ?? Device.samples[1], files: files, label: FileLabel.of(files))
        phase = .receiving
        animateProgress(over: 1.6, failAt: nil) { [weak self] in
            self?.phase = .received
            self?.holdReceived()
        }
    }

    private func holdReceived() {
        schedule(after: Spec.receivedHold) { [weak self] in
            guard let self, self.phase == .received, !self.hovering else { return }
            self.phase = .idle
        }
    }

    func islandTapped() {
        if phase == .received {
            cancelTimers()
            phase = .idle
        }
    }

    func revealReceived() {
        guard let files = transfer?.files, !files.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(files)
    }

    private func animateProgress(over seconds: Double, failAt: Double?, completion: @escaping @MainActor () -> Void) {
        progressTask?.cancel()
        progressTask = Task { [weak self] in
            let start = Date()
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(33))
                guard let self, !Task.isCancelled else { return }
                let progress = min(1, Date().timeIntervalSince(start) / seconds)
                self.transfer?.progress = progress
                if let failAt, progress >= failAt {
                    self.phase = .failed("网络中断")
                    self.schedule(after: Spec.failedHold) { [weak self] in
                        if case .failed = self?.phase { self?.phase = .idle }
                    }
                    return
                }
                if progress >= 1 {
                    completion()
                    return
                }
            }
        }
    }

    private func schedule(after delay: Duration, _ action: @escaping @MainActor () -> Void) {
        timer?.cancel()
        timer = Task {
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            action()
        }
    }

    private func cancelTimers() {
        timer?.cancel()
        timer = nil
        progressTask?.cancel()
        progressTask = nil
    }

    // MARK: - Geometry

    /// The island body (without the notch shoulders) for the current phase.
    var bodySize: CGSize { bodySize(for: phase) }

    func bodySize(for phase: Phase) -> CGSize {
        let content: CGSize
        switch phase {
        case .idle:
            if case .notch(let width, let height) = mode { return CGSize(width: width, height: height) }
            return CGSize(width: 160, height: 30)
        case .hint: content = CGSize(width: 260, height: 22)
        // The default device row, then the strip (`Spec.moreStripHeight`
        // including the bottom padding) along the bottom edge.
        case .target: content = CGSize(width: 360, height: 92)
        case .picker: content = devices.count == 1 ? CGSize(width: 380, height: 64) : CGSize(width: Spec.pickerWidth, height: 122)
        case .unavailable: content = CGSize(width: 300, height: 24)
        case .queued where queueCollapsed:
            if case .notch(let width, let height) = mode { return CGSize(width: width + 76, height: height) }
            return CGSize(width: 150, height: 30)
        case .sending, .queued: content = CGSize(width: 340, height: 40)
        case .done, .failed, .refused: content = CGSize(width: 340, height: 28)
        case .receiving: content = CGSize(width: 384, height: 60)
        case .received: content = CGSize(width: 400, height: 76)
        }
        switch mode {
        case .notch(let width, let height):
            return CGSize(width: max(content.width, width + 60), height: height + content.height + 14)
        case .capsule:
            return CGSize(width: content.width, height: content.height + 24)
        }
    }

    /// The island body in panel coordinates (origin top left).
    func islandRect(panelWidth: CGFloat) -> CGRect {
        let size = bodySize
        return CGRect(x: (panelWidth - size.width) / 2, y: mode.topOffset, width: size.width, height: size.height)
    }
}

/// Files for the simulated receive, so they can be dragged out and revealed.
enum DemoFiles {
    static func make() -> [URL] {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("WormholeSendDemo", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return ["IMG_2041.heic", "IMG_2042.heic", "会议纪要.docx"].map { name in
            let url = dir.appendingPathComponent(name)
            if !FileManager.default.fileExists(atPath: url.path) {
                try? Data("Wormhole Send demo file\n".utf8).write(to: url)
            }
            return url
        }
    }
}
