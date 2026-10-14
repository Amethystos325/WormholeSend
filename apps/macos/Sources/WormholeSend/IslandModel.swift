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
        /// Sent to a LocalSend device: waiting for its user to accept.
        case awaiting
        case queued(position: Int)
        case done
        /// Without 重试 when the other side's user declined: retrying
        /// would only ask them again.
        case failed(String, canRetry: Bool)
        /// Released over a device that can't take files now: "iPad 离线，未发送".
        case refused(String)
        /// The dropped files are another app's data, which macOS keeps from
        /// this app (see `AppData`): names that app when known and opens Full
        /// Disk Access. Nothing was sent.
        case noAccess(app: String?)
        case receiving
        case received
        /// An incoming transfer stopped: "对方取消了发送" or "连接中断".
        case interrupted(String)
        /// Another device asks to pair: compare the code, then 拒绝 / 配对.
        /// Stays until the pairing ends.
        case pairRequest(pairId: String, alias: String, code: String)
        /// Accepted here, waiting for the other side.
        case pairWaiting(alias: String)
        /// A LocalSend device asks to send: 拒绝 / 接收, or for a text
        /// message 忽略 / 复制. Stays until answered, timed out or withdrawn.
        case receiveRequest(ReceiveRequest)
        /// A short message: paired, pairing failed, unpaired, copied; the
        /// detail shows on the right.
        case notice(text: String, detail: String?, success: Bool)

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
    /// The paired devices, default first, as the core lists them, then the
    /// LocalSend devices online.
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
    /// Changes with the language menu, so the island redraws in the new one.
    var language = Language.current

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
    /// Requests that need an answer (`pairRequest`, `receiveRequest`), in
    /// arrival order; the island shows the first whenever it is free.
    @ObservationIgnored private var pendingRequests: [Phase] = []
    /// LocalSend senders that asked, for the receive that follows.
    @ObservationIgnored private var requestSenders: [String: Device] = [:]
    /// The LocalSend devices online, kept up from `device.updated`. Listing
    /// them (`localsend.list`) announces this device, and a LocalSend device
    /// answers every announcement packet with a connection it keeps for about
    /// a minute: from a Mac on two interfaces, enough that a phone's next send
    /// found the transfer service full and was reset. So they are listed once,
    /// at start, the way the Windows app does it.
    @ObservationIgnored private var localSend: [Device] = []
    @ObservationIgnored private var waitingPair: String?

    var defaultDevice: Device? { devices.first { $0.isDefault } }
    var pairedDevices: [Device] { devices.filter { !$0.isLocalSend } }
    /// Devices that can take files now: online and still trusting this one.
    var usableDevices: [Device] { devices.filter(\.usable) }
    /// One send at a time, and no drags while a pairing waits for an answer.
    /// Receiving doesn't block a drag: the island shows the drag, and the
    /// incoming transfer again afterwards. Same rule as on Windows.
    var isBusy: Bool {
        switch phase {
        case .sending, .awaiting, .queued, .pairRequest, .pairWaiting, .receiveRequest: true
        default: false
        }
    }

    // MARK: - Drag input

    func dragStarted(files: [URL]) {
        guard !isBusy else { return }
        cancelTimers()
        reloadDevices()
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
            return sendIfReadable(files, to: device)
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
            return sendIfReadable(files, to: device)
        default:
            return false
        }
    }

    private func enterIsland() {
        if devices.isEmpty {
            phase = .unavailable(tr("还没有配对设备 · 点击配对", "No paired devices · Click to pair", "ペアリングしたデバイスがありません · クリックしてペアリング"))
        } else if usableDevices.isEmpty {
            phase = .unavailable(devices.count == 1 ? devices[0].cannotSend : tr("没有在线的设备", "No devices online", "オンラインのデバイスがありません"))
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
        if devices.isEmpty { return tr("还没有配对设备", "No paired devices yet", "ペアリングしたデバイスがありません") }
        if devices.count == 1 {
            let device = devices[0]
            return device.usable ? Self.moveHere(toSendTo: device.name) : device.cannotSend
        }
        if let device = defaultDevice, device.usable { return Self.moveHere(toSendTo: device.name) }
        guard let device = defaultDevice else {
            return tr("移到这里选择设备", "Move here to choose a device", "ここに移動してデバイスを選択")
        }
        return device.needsRepair
            ? tr("默认设备需要重新配对，移到这里选其他设备", "Default device needs re-pairing. Move here to choose another", "デフォルトのデバイスは再ペアリングが必要です。ここに移動して別のデバイスを選択")
            : tr("默认设备离线，移到这里选其他设备", "Default device offline. Move here to choose another", "デフォルトのデバイスはオフラインです。ここに移動して別のデバイスを選択")
    }

    private static func moveHere(toSendTo name: String) -> String {
        tr("移到这里发送到 \(name)", "Move here to send to \(name)", "ここに移動して \(name) に送信")
    }

    // MARK: - Transfers (through the core)

    /// Lists the LocalSend devices online, which announces this device: once,
    /// at start (see `localSend`).
    func loadLocalSend() {
        guard let core else { return }
        let nearby = (try? core.call("localsend.list", as: [DeviceInfo].self).get()) ?? []
        localSend = nearby.map(Device.init(info:))
        reloadDevices()
    }

    /// Reloads the paired devices from the core, then the LocalSend devices
    /// online.
    func reloadDevices() {
        guard let core, case .success(let paired) = core.call("devices.list", as: [DeviceInfo].self) else { return }
        let room = max(0, Spec.pickerMaxDevices - paired.count)
        devices = paired.map(Device.init(info:)) + localSend.prefix(room)
    }

    /// A LocalSend device came online or went away.
    private func localSendUpdated(_ event: CoreEvent) {
        guard let id = event.id else { return }
        localSend.removeAll { $0.id == id }
        if event.online == true {
            localSend.append(Device(
                id: id, name: event.alias ?? Self.unknownDevice, glyph: DeviceGlyph(deviceType: event.deviceType),
                online: true, isDefault: false, isLocalSend: true))
        }
    }

    private func send(_ files: [URL], to device: Device) {
        cancelTimers()
        transfer = Transfer(device: device, files: files, label: FileLabel.of(files))
        // Until the core reports the queue, the session or the wait.
        phase = device.isLocalSend ? .awaiting : .sending
        guard let core else {
            fail(tr("核心没有运行", "The core isn't running", "コアが起動していません"))
            return
        }
        switch core.call("send", ["deviceId": device.id, "paths": files.map(\.path)], as: String.self) {
        case .success(let id): transfer?.id = id
        case .failure(let failure): fail(failure.message)
        }
    }

    /// Sends unless another app's data protection would only let the upload
    /// fail halfway; then says why, and the files fly back.
    private func sendIfReadable(_ files: [URL], to device: Device) -> Bool {
        if let blocked = AppData.firstBlocked(files) {
            cancelTimers()
            Sound.failed.play()
            phase = .noAccess(app: AppData.appName(owning: blocked))
            schedule(after: Spec.noAccessHold) { [weak self] in
                if case .noAccess = self?.phase { self?.settle() }
            }
            return false
        }
        send(files, to: device)
        return true
    }

    func openFullDiskAccess() {
        AppData.openSettings()
        settle()
    }

    private func refuse(_ device: Device) {
        cancelTimers()
        phase = .refused(device.notSent)
        schedule(after: Spec.refusedHold) { [weak self] in self?.settle() }
    }

    private func fail(_ reason: String, canRetry: Bool = true) {
        Sound.failed.play()
        phase = .failed(reason, canRetry: canRetry)
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
            phase = transfer?.device.isLocalSend == true ? .awaiting : .sending
        case .failure(let failure):
            fail(failure.message)
        }
    }

    /// A core event, on the main actor.
    func handle(_ event: CoreEvent) {
        switch event.event {
        case "device.updated":
            if event.kind == "localsend", event.paired != true { localSendUpdated(event) }
            reloadDevices()
        case "pair.incoming":
            if let id = event.pairId { incomingPairs[id] = event.alias ?? Self.otherDevice }
        case "pair.code":
            if let id = event.pairId, let alias = incomingPairs[id], let code = event.code {
                ask(.pairRequest(pairId: id, alias: alias, code: code))
            }
        case "receive.request":
            receiveRequested(event)
        case "receive.closed":
            receiveRequestClosed(event)
        case "pair.result":
            reloadDevices()
            pairFinished(event)
        case "device.unpaired":
            reloadDevices()
            let alias = event.alias ?? Self.otherDevice
            showNotice(tr("\(alias) 已解除配对", "\(alias) unpaired this device", "\(alias) がペアリングを解除しました"), success: false)
        case "receive.waiting":
            waitingCount = event.count ?? 0
        case "transfer.queued" where isCurrentSend(event):
            queueCollapsed = false
            phase = .queued(position: event.position ?? 1)
            schedule(after: Spec.queuedHint) { [weak self] in self?.queueCollapsed = true }
        case "transfer.waiting" where isCurrentSend(event):
            cancelTimers()
            phase = .awaiting
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
            fail(event.error ?? tr("未知错误", "Unknown error", "不明なエラー"), canRetry: event.status != "declined")
        }
    }

    private func receiveStarted(_ event: CoreEvent) {
        let id = event.deviceId ?? ""
        let device = devices.first { $0.id == id } ?? requestSenders[id]
            ?? Device(id: id, name: event.alias ?? Self.unknownDevice, glyph: .desktop, online: true, isDefault: false)
        let count = event.files ?? 0
        incoming = Transfer(id: event.id, device: device, files: [], label: FileLabel.count(count), count: count)
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
            phase = .interrupted(
                event.status == "cancelled"
                    ? tr("对方取消了发送", "The sender cancelled", "送信者がキャンセルしました")
                    : tr("连接中断", "The connection dropped", "接続が切れました"))
            schedule(after: Spec.failedHold) { [weak self] in self?.settle() }
        }
    }

    /// Whether an incoming transfer may take over the island now.
    private var canShowIncoming: Bool {
        switch phase {
        case .idle, .done, .refused, .noAccess, .failed, .received, .interrupted, .notice, .receiving: true
        default: false
        }
    }

    /// Whether a request may take over the island now: not while sending,
    /// queued, dragging or answering another request.
    private var canShowRequest: Bool {
        canShowIncoming
    }

    /// Back to rest: a waiting request first (it needs an answer), then the
    /// incoming transfer if one is running, else idle.
    private func settle() {
        cancelTimers()
        if let request = pendingRequests.first {
            phase = request
        } else if let incoming {
            transfer = incoming
            phase = .receiving
        } else {
            phase = .idle
        }
    }

    // MARK: - Requests on the island

    /// For a device the core did not name.
    private static var otherDevice: String { tr("对方", "The other device", "相手") }
    private static var unknownDevice: String { tr("未知设备", "Unknown device", "不明なデバイス") }

    private static func paired(with alias: String) -> String {
        tr("已与 \(alias) 配对", "Paired with \(alias)", "\(alias) とペアリングしました")
    }

    private static func pairingFailed(_ reason: String) -> String {
        tr("配对失败：\(reason)", "Pairing failed: \(reason)", "ペアリングできませんでした：\(reason)")
    }

    /// Queues a request that needs an answer and shows it if the island is free.
    private func ask(_ request: Phase) {
        pendingRequests.append(request)
        Sound.pairRequest.play()
        if canShowRequest { settle() }
    }

    /// Takes requests off the queue, shown or not.
    private func drop(where matches: (Phase) -> Bool) {
        pendingRequests.removeAll(where: matches)
    }

    func acceptPair() {
        guard case .pairRequest(let pairId, let alias, _) = phase else { return }
        drop { $0 == phase }
        switch core?.call("pair.confirm", ["pairId": pairId, "accept": true]) {
        case .failure(let failure):
            showNotice(Self.pairingFailed(failure.message), success: false)
        default:
            waitingPair = pairId
            phase = .pairWaiting(alias: alias)
        }
    }

    func declinePair() {
        guard case .pairRequest(let pairId, _, _) = phase else { return }
        drop { $0 == phase }
        incomingPairs[pairId] = nil
        core?.call("pair.confirm", ["pairId": pairId, "accept": false])
        settle()
    }

    private func pairFinished(_ event: CoreEvent) {
        guard let pairId = event.pairId else { return }
        let alias = event.alias ?? incomingPairs[pairId] ?? Self.otherDevice
        incomingPairs[pairId] = nil
        let ok = event.ok == true
        drop {
            if case .pairRequest(let id, _, _) = $0 { id == pairId } else { false }
        }
        switch phase {
        case .pairRequest(let id, _, _) where id == pairId:
            // Declined by the other side or expired while still on screen.
            if ok { showNotice(Self.paired(with: alias), success: true) } else { settle() }
        case .pairWaiting where waitingPair == pairId:
            waitingPair = nil
            let reason = event.reason ?? tr("未知原因", "Unknown reason", "原因不明")
            showNotice(ok ? Self.paired(with: alias) : Self.pairingFailed(reason), success: ok)
        default:
            break
        }
    }

    private func receiveRequested(_ event: CoreEvent) {
        guard let id = event.requestId, let deviceId = event.deviceId else { return }
        let device = devices.first { $0.id == deviceId }
            ?? Device(
                id: deviceId, name: event.alias ?? Self.unknownDevice, glyph: DeviceGlyph(deviceType: event.deviceType),
                online: true, isDefault: false, isLocalSend: true)
        requestSenders[deviceId] = device
        let request = ReceiveRequest(
            id: id, device: device, label: event.label ?? FileLabel.count(event.files ?? 0),
            total: event.total ?? 0, text: event.text, deadline: .now + ReceiveRequest.timeout)
        ask(.receiveRequest(request))
    }

    /// Timed out (declined by the core; the countdown said so, the island
    /// just closes) or withdrawn by the sender.
    private func receiveRequestClosed(_ event: CoreEvent) {
        guard let id = event.requestId else { return }
        drop {
            if case .receiveRequest(let request) = $0 { request.id == id } else { false }
        }
        guard case .receiveRequest(let request) = phase, request.id == id else { return }
        if event.reason == "timeout" {
            settle()
        } else {
            let name = request.device.name
            showNotice(tr("\(name) 取消了发送", "\(name) cancelled sending", "\(name) が送信をキャンセルしました"), success: false)
        }
    }

    /// Accepts the request on the island: files start arriving, a text
    /// message goes to the clipboard.
    func acceptReceive() {
        guard case .receiveRequest(let request) = phase else { return }
        drop { $0 == phase }
        let open = respond(to: request, accept: true)
        if let text = request.text {
            // Copied even when the sender gave up meanwhile: the text is here.
            NSPasteboard.general.clearContents()
            guard NSPasteboard.general.setString(text, forType: .string) else {
                showNotice(tr("无法写入剪贴板", "Couldn't copy to the clipboard", "クリップボードにコピーできませんでした"), success: false)
                return
            }
            let preview = request.preview ?? text
            // The start of the text: 11 characters, as on Windows; English
            // words need more.
            let length = Language.current == .en ? 24 : 11
            showNotice(
                tr("已复制到剪贴板", "Copied", "コピーしました"),
                detail: preview.count > length ? "\(preview.prefix(length))…" : preview, success: true)
        } else if open {
            // The transfer's own events follow.
            settle()
        } else {
            let name = request.device.name
            showNotice(tr("\(name) 已取消发送", "\(name) already cancelled sending", "\(name) は送信をキャンセルしました"), success: false)
        }
    }

    func declineReceive() {
        guard case .receiveRequest(let request) = phase else { return }
        drop { $0 == phase }
        respond(to: request, accept: false)
        settle()
    }

    /// Returns whether the request was still waiting for the answer.
    @discardableResult
    private func respond(to request: ReceiveRequest, accept: Bool) -> Bool {
        let arguments: [String: Any] = ["requestId": request.id, "accept": accept]
        return (try? core?.call("receive.respond", arguments, as: Bool.self).get()) ?? false
    }

    private func showNotice(_ text: String, detail: String? = nil, success: Bool) {
        guard canShowIncoming || isPairing else { return }
        cancelTimers()
        phase = .notice(text: text, detail: detail, success: success)
        schedule(after: success ? Spec.doneHold : Spec.failedHold) { [weak self] in
            if case .notice = self?.phase { self?.settle() }
        }
    }

    /// A request or a pairing on screen, which a notice may replace.
    private var isPairing: Bool {
        switch phase {
        case .pairRequest, .pairWaiting, .receiveRequest: true
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
        // One line widens to fit its text (another language, a long name).
        case .hint: content = CGSize(width: fitting(hintText, size: 12.5, base: 260, extra: 59), height: 22)
        // The default device row, then the strip (`Spec.moreStripHeight`
        // including the bottom padding) along the bottom edge.
        case .target: content = CGSize(width: 360, height: 92)
        case .picker: content = devices.count == 1 ? CGSize(width: 380, height: 64) : CGSize(width: pickerWidth, height: 122)
        case .unavailable(let text): content = CGSize(width: fitting(text, size: 13, base: 300, extra: 62), height: 24)
        case .queued where queueCollapsed:
            if case .notch(let width, let height) = mode { return CGSize(width: width + 76, height: height) }
            return CGSize(width: 150, height: 30)
        case .sending: content = CGSize(width: 340, height: 56)
        case .queued, .awaiting: content = CGSize(width: 340, height: 40)
        case .refused(let text): content = CGSize(width: fitting(text, size: 13, base: 340, extra: 70), height: 28)
        case .noAccess(let app):
            content = CGSize(width: fitting(Self.noAccessText(app: app), size: 13, base: 340, extra: 170), height: 28)
        case .failed(let reason, let canRetry):
            content = CGSize(width: fitting(Self.failedText(reason), size: 13, base: 340, extra: canRetry ? 140 : 70), height: 28)
        case .notice(let text, let detail, _):
            let detailWidth = detail.map { Self.textWidth($0, size: 11, weight: .regular) + 8 } ?? 0
            content = CGSize(width: fitting(text, size: 13, base: 340, extra: 70 + detailWidth), height: 28)
        case .done, .interrupted, .pairWaiting: content = CGSize(width: 340, height: 28)
        case .pairRequest: content = CGSize(width: 380, height: 78)
        case .receiveRequest(let request): content = CGSize(width: 380, height: request.previewWraps ? 92 : 78)
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

    /// "发送失败：网络中断".
    static func failedText(_ reason: String) -> String {
        tr("发送失败：\(reason)", "Couldn't send: \(reason)", "送信失敗：\(reason)")
    }

    /// "读取 微信 的文件需要完全磁盘访问权限".
    static func noAccessText(app: String?) -> String {
        guard let app else {
            return tr("读取这个文件需要完全磁盘访问权限", "Sending this file needs Full Disk Access", "このファイルを送るにはフルディスクアクセスが必要です")
        }
        return tr("读取 \(app) 的文件需要完全磁盘访问权限", "Sending files from \(app) needs Full Disk Access", "\(app) のファイルを送るにはフルディスクアクセスが必要です")
    }

    /// Wide enough for a line of text: at least `base`, at most the
    /// picker's width. `extra` is the rest of the line (padding, icon,
    /// buttons).
    private func fitting(_ text: String, size: CGFloat, base: CGFloat, extra: CGFloat) -> CGFloat {
        min(Spec.pickerMaxWidth, max(base, Self.textWidth(text, size: size) + extra))
    }

    private static func textWidth(_ text: String, size: CGFloat, weight: NSFont.Weight = .medium) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: size, weight: weight)]).width)
    }

    /// Wider from five devices on, so each keeps a usable column (as on
    /// Windows).
    private var pickerWidth: CGFloat {
        min(Spec.pickerMaxWidth, max(Spec.pickerWidth, 28 + 84 * CGFloat(devices.count)))
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
