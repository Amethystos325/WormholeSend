import AppKit
import Observation

/// The island's state machine (design doc, section 2.1), driven by drag
/// events from the panel and the drag monitor, and by the core's transfer
/// events.
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
        /// Released over a device that can't take files now; `reason` is
        /// "离线" or "需要重新配对".
        case refused(name: String, reason: String)
        case receiving
        case received
        /// An incoming transfer stopped: "对方取消了发送" or "连接中断".
        case interrupted(String)
        /// Another device asks to pair: compare the code, then 拒绝 / 配对.
        /// Stays until the pairing ends.
        case pairRequest(pairId: String, alias: String, code: String)
        /// Accepted here, waiting for the other side.
        case pairWaiting(alias: String)
        /// A short message: paired, pairing failed, unpaired.
        case notice(text: String, success: Bool)

        /// Phases in which the island is the drop target under the cursor.
        var isOverIsland: Bool {
            switch self {
            case .target, .picker, .unavailable: true
            default: false
            }
        }
    }

    var phase: Phase = .idle {
        didSet {
            if phase != oldValue { onPhaseChange?(phase) }
        }
    }
    var mode: ScreenMode = .capsule
    /// The paired devices, default first, as the core lists them.
    var devices: [Device] = []
    var dragLabel = ""
    var transfer: Transfer?
    /// The queued state shrinks to a compact badge after a few seconds.
    var queueCollapsed = false
    var hovering = false {
        didSet {
            if phase == .received, !hovering { holdReceived() }
        }
    }
    /// Paired devices waiting to send to this one.
    var waitingCount = 0

    @ObservationIgnored var core: Core?
    @ObservationIgnored var onPhaseChange: ((Phase) -> Void)?
    /// The incoming transfer, also while the island shows something else.
    @ObservationIgnored private var incoming: Transfer?
    @ObservationIgnored private var dragFiles: [URL] = []
    /// Set once the cursor reached the default device row in the target
    /// state. The cursor usually comes up from below, through where the strip
    /// appears; only a move back down into the strip opens the list.
    @ObservationIgnored private var armedForPicker = false
    @ObservationIgnored private var timer: Task<Void, Never>?
    /// Incoming pairing requests (from `pair.incoming`) by pairing ID; only
    /// these come onto the island. Pairings started here use a dialog.
    @ObservationIgnored private var incomingPairs: [String: String] = [:]
    /// A request whose code arrived while the island was busy.
    @ObservationIgnored private var pendingRequest: Phase?
    @ObservationIgnored private var waitingPair: String?

    var defaultDevice: Device? { devices.first { $0.isDefault } }
    /// Devices that can take files now: online and still trusting this one.
    var usableDevices: [Device] { devices.filter(\.usable) }
    /// One send at a time, and no drags while a pairing waits for an answer.
    /// Receiving doesn't block a drag: the island shows the drag, and the
    /// incoming transfer again afterwards. Same rule as on Windows.
    var isBusy: Bool {
        switch phase {
        case .sending, .queued, .pairRequest, .pairWaiting: true
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
            settle()
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
            guard device.usable else {
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
        } else if usableDevices.isEmpty {
            phase = .unavailable(devices.count == 1 ? "\(devices[0].name) \(devices[0].unusableReason)，无法发送" : "没有在线的设备")
        } else if devices.count == 1 {
            phase = .picker(hot: 0)
        } else if defaultDevice?.usable == true {
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
        if devices.count == 1, !devices[0].usable { return "\(devices[0].name) \(devices[0].unusableReason)，无法发送" }
        if let device = defaultDevice, device.usable { return "移到这里发送到 \(device.name)" }
        return "默认设备\(defaultDevice?.unusableReason ?? "离线")，移到这里选其他设备"
    }

    // MARK: - Transfers (through the core)

    /// Reloads the paired devices from the core.
    func reloadDevices() {
        guard let core else { return }
        if case .success(let list) = core.call("devices.list", as: [DeviceInfo].self) {
            devices = list.map(Device.init(info:))
        }
    }

    private func send(_ files: [URL], to device: Device) {
        cancelTimers()
        transfer = Transfer(device: device, files: files, label: FileLabel.of(files))
        // Until the core reports the queue or the session.
        phase = .sending
        guard let core else {
            fail("核心没有运行")
            return
        }
        switch core.call("send", ["deviceId": device.id, "paths": files.map(\.path)], as: String.self) {
        case .success(let id): transfer?.id = id
        case .failure(let failure): fail(failure.message)
        }
    }

    private func refuse(_ device: Device) {
        cancelTimers()
        phase = .refused(name: device.name, reason: device.unusableReason)
        schedule(after: Spec.refusedHold) { [weak self] in self?.settle() }
    }

    private func fail(_ reason: String) {
        Sound.failed.play()
        phase = .failed(reason)
        schedule(after: Spec.failedHold) { [weak self] in
            if case .failed = self?.phase { self?.settle() }
        }
    }

    func cancelTransfer() {
        cancelTimers()
        if let id = transfer?.id {
            core?.call("transfer.cancel", ["transferId": id], as: Bool.self)
        }
        settle()
    }

    func retry() {
        guard let id = transfer?.id, let core else { return }
        cancelTimers()
        switch core.call("transfer.retry", ["transferId": id], as: String.self) {
        case .success(let newId):
            transfer?.id = newId
            transfer?.progress = 0
            transfer?.stats = TransferStats()
            phase = .sending
        case .failure(let failure):
            fail(failure.message)
        }
    }

    /// A core event, on the main actor.
    func handle(_ event: CoreEvent) {
        switch event.event {
        case "device.updated":
            reloadDevices()
        case "pair.incoming":
            if let id = event.pairId { incomingPairs[id] = event.alias ?? "对方" }
        case "pair.code":
            if let id = event.pairId, let alias = incomingPairs[id], let code = event.code {
                pendingRequest = .pairRequest(pairId: id, alias: alias, code: code)
                Sound.pairRequest.play()
                if canShowPairRequest { settle() }
            }
        case "pair.result":
            reloadDevices()
            pairFinished(event)
        case "device.unpaired":
            reloadDevices()
            showNotice("\(event.alias ?? "对方") 已解除配对", success: false)
        case "receive.waiting":
            waitingCount = event.count ?? 0
        case "transfer.queued" where isCurrentSend(event):
            queueCollapsed = false
            phase = .queued(position: event.position ?? 1)
            schedule(after: Spec.queuedHint) { [weak self] in self?.queueCollapsed = true }
        case "transfer.started" where event.direction == "send" && isCurrentSend(event):
            cancelTimers()
            transfer?.progress = 0
            transfer?.stats = TransferStats(total: event.total ?? 0, files: event.files ?? 0)
            phase = .sending
        case "transfer.started" where event.direction == "receive":
            receiveStarted(event)
        case "transfer.progress":
            let progress = event.total.map { $0 > 0 ? Double(event.bytes ?? 0) / Double($0) : 0 } ?? 0
            if event.direction == "send", isCurrentSend(event) {
                transfer?.progress = progress
                transfer?.stats.update(
                    bytes: event.bytes ?? 0, total: event.total ?? 0, filesDone: event.filesDone ?? 0,
                    at: ProcessInfo.processInfo.systemUptime)
            } else if event.direction == "receive", incoming?.id == event.id {
                incoming?.progress = progress
                if phase == .receiving { transfer?.progress = progress }
            }
        case "transfer.finished" where event.direction == "send" && isCurrentSend(event):
            sendFinished(event)
        case "transfer.finished" where event.direction == "receive" && incoming?.id == event.id:
            receiveFinished(event)
        default:
            break
        }
    }

    private func isCurrentSend(_ event: CoreEvent) -> Bool {
        event.id != nil && event.id == transfer?.id && isBusy
    }

    private func sendFinished(_ event: CoreEvent) {
        cancelTimers()
        switch event.status {
        case "done":
            Sound.sent.play()
            transfer?.progress = 1
            phase = .done
            schedule(after: Spec.doneHold) { [weak self] in self?.settle() }
        case "cancelled":
            settle()
        default:
            fail(event.error ?? "发送失败")
        }
    }

    private func receiveStarted(_ event: CoreEvent) {
        let device = devices.first { $0.id == event.deviceId }
            ?? Device(id: event.deviceId ?? "", name: event.alias ?? "未知设备", symbol: "desktopcomputer", online: true, isDefault: false)
        let count = event.files ?? 0
        incoming = Transfer(id: event.id, device: device, files: [], label: "\(count) 个文件", count: count)
        if canShowIncoming {
            cancelTimers()
            transfer = incoming
            phase = .receiving
        }
    }

    private func receiveFinished(_ event: CoreEvent) {
        guard var finished = incoming else { return }
        incoming = nil
        let shown = phase == .receiving || canShowIncoming
        if event.status == "done" {
            finished.files = (event.savedPaths ?? []).map { URL(fileURLWithPath: $0) }
            finished.count = event.filesDone ?? finished.count
            finished.progress = 1
            Sound.received.play()
            guard shown else { return }
            cancelTimers()
            transfer = finished
            phase = .received
            holdReceived()
        } else if phase == .receiving {
            cancelTimers()
            // A cancel was the sender's choice; only a broken connection sounds.
            if event.status != "cancelled" { Sound.failed.play() }
            phase = .interrupted(event.status == "cancelled" ? "对方取消了发送" : "连接中断")
            schedule(after: Spec.failedHold) { [weak self] in self?.settle() }
        }
    }

    /// Whether an incoming transfer may take over the island now.
    private var canShowIncoming: Bool {
        switch phase {
        case .idle, .done, .refused, .failed, .received, .interrupted, .notice, .receiving: true
        default: false
        }
    }

    /// Whether a pairing request may take over the island now: not while
    /// sending, queued or dragging.
    private var canShowPairRequest: Bool {
        canShowIncoming
    }

    /// Back to rest: a waiting pairing request first (it needs an answer),
    /// then the incoming transfer if one is running, else idle.
    private func settle() {
        cancelTimers()
        if let request = pendingRequest {
            phase = request
        } else if let incoming {
            transfer = incoming
            phase = .receiving
        } else {
            phase = .idle
        }
    }

    // MARK: - Pairing on the island

    func acceptPair() {
        guard case .pairRequest(let pairId, let alias, _) = phase else { return }
        pendingRequest = nil
        switch core?.call("pair.confirm", ["pairId": pairId, "accept": true]) {
        case .failure(let failure):
            showNotice("配对失败：\(failure.message)", success: false)
        default:
            waitingPair = pairId
            phase = .pairWaiting(alias: alias)
        }
    }

    func declinePair() {
        guard case .pairRequest(let pairId, _, _) = phase else { return }
        pendingRequest = nil
        incomingPairs[pairId] = nil
        core?.call("pair.confirm", ["pairId": pairId, "accept": false])
        settle()
    }

    private func pairFinished(_ event: CoreEvent) {
        guard let pairId = event.pairId else { return }
        let alias = event.alias ?? incomingPairs[pairId] ?? "对方"
        incomingPairs[pairId] = nil
        let ok = event.ok == true
        if case .pairRequest(let id, _, _)? = pendingRequest, id == pairId {
            pendingRequest = nil
        }
        switch phase {
        case .pairRequest(let id, _, _) where id == pairId:
            // Declined by the other side or expired while still on screen.
            if ok { showNotice("已与 \(alias) 配对", success: true) } else { settle() }
        case .pairWaiting where waitingPair == pairId:
            waitingPair = nil
            showNotice(ok ? "已与 \(alias) 配对" : "配对失败：\(event.reason ?? "未知原因")", success: ok)
        default:
            break
        }
    }

    private func showNotice(_ text: String, success: Bool) {
        guard canShowIncoming || isPairing else { return }
        cancelTimers()
        phase = .notice(text: text, success: success)
        schedule(after: success ? Spec.doneHold : Spec.failedHold) { [weak self] in
            if case .notice = self?.phase { self?.settle() }
        }
    }

    private var isPairing: Bool {
        switch phase {
        case .pairRequest, .pairWaiting: true
        default: false
        }
    }

    private func holdReceived() {
        schedule(after: Spec.receivedHold) { [weak self] in
            guard let self, self.phase == .received, !self.hovering else { return }
            self.settle()
        }
    }

    func islandTapped() {
        if phase == .received {
            cancelTimers()
            settle()
        }
    }

    func revealReceived() {
        guard let files = transfer?.files, !files.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(files)
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
        case .sending: content = CGSize(width: 340, height: 56)
        case .queued: content = CGSize(width: 340, height: 40)
        case .done, .failed, .refused, .interrupted, .pairWaiting, .notice: content = CGSize(width: 340, height: 28)
        case .pairRequest: content = CGSize(width: 380, height: 78)
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
