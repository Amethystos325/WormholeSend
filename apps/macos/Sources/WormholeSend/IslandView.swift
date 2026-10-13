import SwiftUI

/// The island: a black shape at the top center of the panel that morphs
/// between the phases, with each phase's content inside.
struct IslandView: View {
    @Bindable var model: IslandModel

    var body: some View {
        let size = model.bodySize
        let mode = model.mode
        ZStack(alignment: .top) {
            // Near-invisible halo: the window server routes drags and clicks
            // only to painted pixels, so this extends the drop area by
            // `Spec.hitOutset` while a drag is in progress.
            if acceptsDrag {
                RoundedRectangle(cornerRadius: 32, style: .continuous)
                    .fill(Color.black.opacity(0.004))
                    .frame(width: size.width + 2 * Spec.hitOutset, height: size.height + Spec.hitOutset)
            }
            island(size: size, mode: mode)
                .padding(.top, mode.topOffset)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .animation(Spec.spring, value: model.phase)
        .animation(Spec.spring, value: model.queueCollapsed)
    }

    private var acceptsDrag: Bool {
        switch model.phase {
        case .hint, .target, .picker, .unavailable: true
        default: false
        }
    }

    @ViewBuilder
    private func island(size: CGSize, mode: ScreenMode) -> some View {
        let shoulder: CGFloat = mode.isNotch ? 8 : 0
        let shape = islandShape(size: size, mode: mode)
        ZStack(alignment: .top) {
            shape.fill(Palette.island)
            // The compact form sits beside the camera, centered in the band;
            // a single line centers in the space below the camera; every
            // other phase lays out from the top, below it.
            content
                .padding(.top, compactQueued ? 0 : isSingleLine ? mode.cameraBand : (mode.isNotch ? mode.cameraBand + 6 : 12))
                .padding(.horizontal, (compactQueued ? 12 : 16) + shoulder)
                .frame(
                    width: size.width + 2 * shoulder, height: size.height,
                    alignment: compactQueued || isSingleLine ? .center : .top)
                .id(contentKey)
                .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .top)))
        }
        .frame(width: size.width + 2 * shoulder, height: size.height)
        .clipShape(shape)
        .opacity(model.phase == .idle ? 0 : 1)
        .animation(model.phase == .idle ? .easeOut(duration: 0.2).delay(0.3) : .easeOut(duration: 0.12), value: model.phase == .idle)
        .contentShape(shape)
        .onHover { model.hovering = $0 }
        .onTapGesture { model.islandTapped() }
    }

    private func islandShape(size: CGSize, mode: ScreenMode) -> AnyShape {
        switch mode {
        case .notch:
            let radius: CGFloat = model.phase == .idle || compactQueued ? 10 : min(26, size.height / 3)
            return AnyShape(NotchShape(shoulder: 8, bottomRadius: radius))
        case .capsule:
            return AnyShape(RoundedRectangle(cornerRadius: min(24, size.height / 2), style: .continuous))
        }
    }

    /// Phases whose content is one line.
    private var isSingleLine: Bool {
        switch model.phase {
        case .hint, .unavailable, .done, .failed, .refused, .interrupted, .pairWaiting, .notice: true
        default: false
        }
    }

    private var compactQueued: Bool {
        if case .queued = model.phase { model.queueCollapsed } else { false }
    }

    /// Changes when the content layout changes, not on every progress tick.
    private var contentKey: String {
        switch model.phase {
        case .idle: "idle"
        case .hint: "hint"
        case .target: "target"
        case .picker: "picker"
        case .unavailable: "unavailable"
        case .sending: "sending"
        case .awaiting: "awaiting"
        case .queued: compactQueued ? "queued-compact" : "queued"
        case .done: "done"
        case .failed: "failed"
        case .refused: "refused"
        case .receiving: "receiving"
        case .received: "received"
        case .interrupted: "interrupted"
        case .pairRequest: "pair-request"
        case .pairWaiting: "pair-waiting"
        case .receiveRequest: "receive-request"
        case .notice: "notice"
        }
    }

    @ViewBuilder
    private var content: some View {
        switch model.phase {
        case .idle:
            Color.clear
        case .hint:
            HintContent(text: model.hintText)
        case .target:
            if let device = model.defaultDevice {
                TargetContent(device: device, others: model.devices.filter { !$0.isDefault })
            }
        case .picker(let hot):
            if model.devices.count == 1 {
                SingleTargetContent(device: model.devices[0], label: model.dragLabel)
            } else {
                PickerContent(devices: model.devices, hot: hot, label: model.dragLabel)
            }
        case .unavailable(let text):
            StatusLine(symbol: "wifi.slash", color: Palette.secondary, text: text)
        case .sending:
            if let transfer = model.transfer {
                ProgressContent(
                    device: transfer.device,
                    title: tr("发送到 \(transfer.device.name)", "Sending to \(transfer.device.name)", "\(transfer.device.name) に送信中"),
                    progress: transfer.progress, detail: transfer.stats.line, onCancel: model.cancelTransfer)
            }
        case .awaiting:
            if let transfer = model.transfer {
                AwaitingContent(name: transfer.device.name, onCancel: model.cancelTransfer)
            }
        case .queued(let position):
            if compactQueued {
                CompactQueued(notch: model.mode)
            } else {
                QueuedContent(position: position, onCancel: model.cancelTransfer)
            }
        case .done:
            if let transfer = model.transfer {
                StatusLine(
                    symbol: "checkmark.circle.fill", color: Palette.success,
                    text: tr("已送达 \(transfer.device.name)", "Delivered to \(transfer.device.name)", "\(transfer.device.name) に届きました"),
                    trailing: transfer.label)
            }
        case .failed(let reason, let canRetry):
            FailedContent(reason: reason, canRetry: canRetry, onRetry: model.retry)
        case .refused(let text):
            StatusLine(symbol: "nosign", color: Palette.warning, text: text)
        case .receiving:
            if let transfer = model.transfer {
                ReceivingContent(transfer: transfer, waiting: model.waitingCount)
            }
        case .interrupted(let reason):
            StatusLine(
                symbol: "exclamationmark.triangle.fill", color: Palette.warning,
                text: tr("传输中断", "Transfer interrupted", "転送が中断されました"), trailing: reason)
        case .pairRequest(_, let alias, let code):
            PairRequestContent(alias: alias, code: code, onDecline: model.declinePair, onAccept: model.acceptPair)
        case .pairWaiting(let alias):
            StatusLine(
                symbol: "clock", color: Palette.secondary,
                text: tr("等待 \(alias) 确认…", "Waiting for \(alias) to confirm…", "\(alias) の確認を待っています…"))
        case .receiveRequest(let request):
            ReceiveRequestContent(request: request, onDecline: model.declineReceive, onAccept: model.acceptReceive)
        case .notice(let text, let detail, let success):
            StatusLine(
                symbol: success ? "checkmark.circle.fill" : "exclamationmark.triangle.fill",
                color: success ? Palette.success : Palette.warning, text: text, trailing: detail)
        case .received:
            if let transfer = model.transfer {
                ReceivedContent(transfer: transfer, onReveal: model.revealReceived)
            }
        }
    }
}

// MARK: - Phase contents

private struct HintContent: View {
    let text: String

    var body: some View {
        HStack(spacing: 7) {
            // The app icon's black hole, about as tall as the text.
            Image(nsImage: StatusIcon.colored(scale: 0.9))
            Text(text)
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(Color(white: 0.86))
                .lineLimit(1)
                .minimumScaleFactor(0.85)
        }
        .frame(maxWidth: .infinity)
    }
}

private struct Avatar: View {
    let device: Device
    var size: CGFloat = 40
    var highlighted = false

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            Circle()
                .fill(highlighted ? Palette.accent : Palette.tile)
                .frame(width: size, height: size)
                .overlay {
                    Image(systemName: device.symbol)
                        .font(.system(size: size * 0.45, weight: .regular))
                        .foregroundStyle(device.usable ? Palette.text : Palette.tertiary)
                }
            if device.usable, size >= 36 {
                Circle()
                    .fill(Palette.success)
                    .frame(width: 10, height: 10)
                    .overlay(Circle().stroke(Palette.island, lineWidth: 2))
            }
        }
    }
}

/// The default device on top; along the bottom edge the strip leading to
/// the other devices (its height is `Spec.moreStripHeight` with padding).
private struct TargetContent: View {
    let device: Device
    let others: [Device]

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 12) {
                Avatar(device: device, size: 44, highlighted: true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(releaseToSend(to: device.name))
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(Palette.text)
                    Text(tr("默认设备", "Default device", "デフォルトのデバイス"))
                        .font(.system(size: 11))
                        .foregroundStyle(Palette.secondary)
                }
                Spacer(minLength: 0)
            }
            Spacer(minLength: 0)
            MoreStrip(others: others)
                .padding(.bottom, 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
    }
}

private struct MoreStrip: View {
    let others: [Device]

    var body: some View {
        HStack(spacing: 8) {
            HStack(spacing: -6) {
                ForEach(others.prefix(3)) { device in
                    Circle()
                        .fill(Palette.tile)
                        .frame(width: 20, height: 20)
                        .overlay {
                            Image(systemName: device.symbol)
                                .font(.system(size: 9))
                                .foregroundStyle(device.online ? Palette.text : Palette.tertiary)
                        }
                        .overlay(Circle().stroke(Palette.island, lineWidth: 1.5))
                }
            }
            Text(tr("其他设备", "Other devices", "その他のデバイス"))
                .font(.system(size: 11.5, weight: .medium))
                .foregroundStyle(Color(white: 0.8))
            Spacer(minLength: 4)
            Label(tr("往下拖动选择", "Drag down to choose", "下にドラッグして選択"), systemImage: "chevron.down")
                .font(.system(size: 11))
                .foregroundStyle(Palette.secondary)
                .labelStyle(TrailingIconLabelStyle())
        }
        .padding(.horizontal, 10)
        .frame(height: 30)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color(white: 0.1)))
    }
}

private struct TrailingIconLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) {
            configuration.title
            configuration.icon
        }
    }
}

/// "松手发送到 书房台式机".
private func releaseToSend(to name: String) -> String {
    tr("松手发送到 \(name)", "Release to send to \(name)", "離すと \(name) に送信")
}

private struct SingleTargetContent: View {
    let device: Device
    let label: String

    var body: some View {
        HStack(spacing: 14) {
            Avatar(device: device, size: 50, highlighted: true)
            VStack(alignment: .leading, spacing: 4) {
                Text(releaseToSend(to: device.name))
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(Palette.text)
                Text(label)
                    .font(.system(size: 11.5))
                    .foregroundStyle(Palette.secondary)
                    .lineLimit(1)
                    // Keeps "等 3 个文件" when a long name has to give way.
                    .truncationMode(.middle)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.top, 4)
    }
}

private struct PickerContent: View {
    let devices: [Device]
    let hot: Int?
    let label: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(tr("把 \(label) 发送到", "Send \(label) to", "\(label) の送信先"))
                .font(.system(size: 11))
                .foregroundStyle(Palette.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .padding(.leading, 4)
            HStack(spacing: 6) {
                ForEach(Array(devices.enumerated()), id: \.element.id) { index, device in
                    DeviceTile(device: device, hot: hot == index)
                }
            }
        }
    }
}

private struct DeviceTile: View {
    let device: Device
    let hot: Bool

    var body: some View {
        VStack(spacing: 5) {
            Avatar(device: device, highlighted: hot && device.usable)
            Text(device.name)
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(device.usable ? Palette.text : Palette.secondary)
                .lineLimit(1)
            Text(status)
                .font(.system(size: 10.5))
                .foregroundStyle(Palette.secondary)
        }
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(hot ? (device.usable ? Palette.tile : Palette.tileHot) : .clear))
        .scaleEffect(hot && device.usable ? 1.05 : 1)
        .animation(.easeOut(duration: 0.15), value: hot)
    }

    private var status: String {
        if device.isLocalSend { return "LocalSend" }
        let state = device.needsRepair
            ? tr("需重新配对", "Needs re-pairing", "再ペアリングが必要")
            : (device.online ? tr("在线", "Online", "オンライン") : tr("离线", "Offline", "オフライン"))
        return device.isDefault ? "\(state) · \(tr("默认", "Default", "デフォルト"))" : state
    }
}

private struct StatusLine: View {
    let symbol: String
    let color: Color
    let text: String
    var trailing: String? = nil

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: symbol)
                .font(.system(size: 17, weight: .semibold))
                .foregroundStyle(color)
            Text(text)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Palette.text)
                .lineLimit(1)
                // Shrinks a little before it truncates, and keeps its room
                // ahead of the trailing detail.
                .minimumScaleFactor(0.85)
                .layoutPriority(1)
            Spacer(minLength: 8)
            if let trailing {
                Text(trailing)
                    .font(.system(size: 11))
                    .foregroundStyle(Palette.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
        }
    }
}

private struct ProgressContent: View {
    let device: Device
    let title: String
    let progress: Double
    /// Sizes or files done, and the speed.
    let detail: String
    let onCancel: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Avatar(device: device, size: 28, highlighted: true)
            VStack(spacing: 6) {
                HStack {
                    Text(title)
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(Palette.text)
                        .lineLimit(1)
                    Spacer()
                    Text("\(Int(progress * 100))%")
                        .font(.system(size: 11.5).monospacedDigit())
                        .foregroundStyle(Palette.secondary)
                }
                ProgressBar(value: progress, height: 4, fill: Palette.accent)
                Text(detail)
                    .font(.system(size: 11).monospacedDigit())
                    .foregroundStyle(Palette.secondary)
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            CancelButton(action: onCancel)
        }
    }
}

private struct QueuedContent: View {
    let position: Int
    let onCancel: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "clock.fill")
                .font(.system(size: 17))
                .foregroundStyle(Palette.warning)
            VStack(alignment: .leading, spacing: 2) {
                Text(tr("排队中 · 前面还有 \(position) 个", "Queued · \(position) ahead", "順番待ち · あと \(position) 件"))
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Palette.text)
                Text(tr(
                    "对方正在接收其他设备的文件，轮到后自动发送", "Sends once it's done receiving",
                    "相手の受信が終わると自動で送信します"))
                    .font(.system(size: 11))
                    .foregroundStyle(Palette.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            CancelButton(action: onCancel)
        }
    }
}

/// Sent to a LocalSend device, whose user accepts there first. Laid out
/// like the queued state.
private struct AwaitingContent: View {
    let name: String
    let onCancel: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "clock.fill")
                .font(.system(size: 17))
                .foregroundStyle(Palette.secondary)
            VStack(alignment: .leading, spacing: 2) {
                Text(tr("等待 \(name) 接收…", "Waiting for \(name) to accept…", "\(name) の受信を待っています…"))
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Palette.text)
                    .lineLimit(1)
                Text(tr("对方用 LocalSend，接收后开始发送", "Starts once they accept in LocalSend", "相手が LocalSend で受信すると送信を開始します"))
                    .font(.system(size: 11))
                    .foregroundStyle(Palette.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 4)
            CancelButton(action: onCancel)
        }
    }
}

/// The queued state after a few seconds: a dot beside the camera.
private struct CompactQueued: View {
    let notch: ScreenMode

    var body: some View {
        HStack {
            Image(systemName: "clock.fill")
                .font(.system(size: 12))
                .foregroundStyle(Palette.warning)
            Spacer()
            if !notch.isNotch {
                Text(tr("排队中", "Queued", "順番待ち"))
                    .font(.system(size: 11.5, weight: .medium))
                    .foregroundStyle(Palette.text)
                Spacer()
            }
            Circle()
                .fill(Palette.warning)
                .frame(width: 6, height: 6)
        }
    }
}

/// Another device asks to pair: its name, the code to compare, and the
/// answer. Same layout as on Windows.
private struct PairRequestContent: View {
    let alias: String
    let code: String
    let onDecline: () -> Void
    let onAccept: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(tr("\(alias) 请求配对", "\(alias) wants to pair", "\(alias) がペアリングを求めています"))
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(Palette.text)
                        .lineLimit(1)
                    Text(tr("两边显示的数字一致才配对", "Pair only if both show the same number", "両方の数字が同じならペアリング"))
                        .font(.system(size: 11))
                        .foregroundStyle(Palette.secondary)
                }
                Spacer(minLength: 8)
                Text(code.count == 6 ? "\(code.prefix(3)) \(code.suffix(3))" : code)
                    .font(.system(size: 24, weight: .semibold).monospacedDigit())
                    .foregroundStyle(Palette.text)
            }
            HStack(spacing: 8) {
                Spacer()
                Button(tr("拒绝", "Decline", "拒否"), action: onDecline)
                    .buttonStyle(PillButtonStyle())
                Button(tr("配对", "Pair", "ペアリング"), action: onAccept)
                    .buttonStyle(PillButtonStyle(fill: Palette.accent))
            }
        }
    }
}

/// A LocalSend device asks to send files, or a text message to copy.
private struct ReceiveRequestContent: View {
    let request: ReceiveRequest
    let onDecline: () -> Void
    let onAccept: () -> Void

    var body: some View {
        VStack(spacing: 10) {
            HStack(alignment: .top, spacing: 10) {
                Avatar(device: request.device, size: 30)
                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(Palette.text)
                        .lineLimit(1)
                    if let preview = request.preview {
                        Text(preview)
                            .font(.system(size: 11))
                            .foregroundStyle(Palette.secondary)
                            .lineLimit(2)
                    } else {
                        HStack(spacing: 8) {
                            Text(request.label)
                                .lineLimit(1)
                                // Keeps "等 3 个文件" when a long name has to give way.
                                .truncationMode(.middle)
                            Spacer(minLength: 0)
                            Text(ByteCountFormatter.string(fromByteCount: request.total, countStyle: .file))
                                .monospacedDigit()
                        }
                        .font(.system(size: 11))
                        .foregroundStyle(Palette.secondary)
                    }
                }
                // The size lines up with the buttons' right edge.
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            HStack(spacing: 8) {
                Text("LocalSend")
                    .font(.system(size: 10.5))
                    .foregroundStyle(Palette.tertiary)
                Spacer()
                // Counts down to the automatic decline.
                TimelineView(.periodic(from: .now, by: 0.25)) { context in
                    Button("\(declineTitle) \(remaining(at: context.date))", action: onDecline)
                        .buttonStyle(PillButtonStyle())
                }
                Button(request.text == nil ? tr("接收", "Accept", "受信") : tr("复制", "Copy", "コピー"), action: onAccept)
                    .buttonStyle(PillButtonStyle(fill: Palette.accent))
            }
        }
    }

    private var title: String {
        let name = request.device.name
        return request.text == nil
            ? tr("\(name) 想发送", "\(name) wants to send", "\(name) から受信リクエスト")
            : tr("\(name) 发来一段文字", "\(name) sent a text message", "\(name) からテキストメッセージ")
    }

    private var declineTitle: String {
        request.text == nil ? tr("拒绝", "Decline", "拒否") : tr("忽略", "Ignore", "無視")
    }

    /// "42s", until the automatic decline.
    private func remaining(at date: Date) -> String {
        "\(max(0, Int(request.deadline.timeIntervalSince(date).rounded(.up))))s"
    }

}

private struct FailedContent: View {
    let reason: String
    let canRetry: Bool
    let onRetry: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 16))
                .foregroundStyle(Palette.warning)
            Text(IslandModel.failedText(reason))
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Palette.text)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
            Spacer()
            if canRetry {
                Button(tr("重试", "Retry", "再試行"), action: onRetry)
                    .buttonStyle(PillButtonStyle())
            }
        }
    }
}

private struct ReceivingContent: View {
    let transfer: Transfer
    /// Other paired devices waiting to send.
    let waiting: Int

    private var waitingText: String {
        tr("还有 \(waiting) 个设备等待发送", "\(plural(waiting, "more device", "more devices")) waiting to send", "ほかに \(waiting) 台が送信待ち")
    }

    private var subtitle: String {
        transfer.device.isLocalSend
            ? tr("LocalSend · 已同意接收", "LocalSend · accepted", "LocalSend · 受信を許可")
            : tr("已配对 · 自动保存", "Paired · saved automatically", "ペアリング済み · 自動保存")
    }

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                Avatar(device: transfer.device, size: 30)
                VStack(alignment: .leading, spacing: 2) {
                    Text(tr(
                        "\(transfer.device.name) 发来 \(transfer.fileCount) 个文件",
                        "\(plural(transfer.fileCount, "file", "files")) from \(transfer.device.name)",
                        "\(transfer.device.name) から \(transfer.fileCount) 個のファイル"))
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(Palette.text)
                    Text(waiting > 0 ? waitingText : subtitle)
                        .font(.system(size: 11))
                        .foregroundStyle(Palette.secondary)
                }
                Spacer()
                Text("\(Int(transfer.progress * 100))%")
                    .font(.system(size: 11.5).monospacedDigit())
                    .foregroundStyle(Palette.secondary)
            }
            ProgressBar(value: transfer.progress, height: 4, fill: Palette.accent)
        }
    }
}

private struct ReceivedContent: View {
    let transfer: Transfer
    let onReveal: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Label(
                    tr("已收到 \(transfer.fileCount) 个文件", "Received \(plural(transfer.fileCount, "file", "files"))", "\(transfer.fileCount) 個のファイルを受信しました"),
                    systemImage: "checkmark.circle.fill")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Palette.text)
                    .labelStyle(TintedIconLabelStyle(color: Palette.success))
                Spacer()
                Button(tr("在访达中显示", "Show in Finder", "Finder で表示"), action: onReveal)
                    .buttonStyle(LinkButtonStyle())
            }
            HStack(spacing: 6) {
                ForEach(transfer.files.prefix(3), id: \.self) { url in
                    FileChip(url: url)
                }
                if transfer.files.count > 3 {
                    Text(tr("等 \(transfer.files.count) 个", "and \(transfer.files.count - 3) more", "など \(transfer.files.count) 個"))
                        .font(.system(size: 11))
                        .foregroundStyle(Palette.secondary)
                }
            }
        }
    }
}

/// A received file that can be dragged out of the island.
private struct FileChip: View {
    let url: URL

    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: "doc.fill")
                .font(.system(size: 10))
            Text(url.lastPathComponent)
                .font(.system(size: 11))
                .lineLimit(1)
        }
        .foregroundStyle(Palette.text)
        .padding(.horizontal, 8)
        .padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Palette.tile))
        .onDrag { NSItemProvider(contentsOf: url) ?? NSItemProvider() }
    }
}

// MARK: - Small parts

private struct ProgressBar: View {
    let value: Double
    let height: CGFloat
    let fill: Color

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Palette.track)
                Capsule().fill(fill).frame(width: geometry.size.width * max(0, min(1, value)))
            }
        }
        .frame(height: height)
    }
}

private struct CancelButton: View {
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark.circle.fill")
                .font(.system(size: 16))
                .foregroundStyle(Palette.tertiary)
        }
        .buttonStyle(.plain)
        .help(tr("取消", "Cancel", "キャンセル"))
    }
}

private struct PillButtonStyle: ButtonStyle {
    var fill = Palette.tile

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            // Digits keep their width, e.g. in a countdown.
            .font(.system(size: 12, weight: .semibold).monospacedDigit())
            .foregroundStyle(Palette.text)
            .padding(.horizontal, 14)
            .padding(.vertical, 5)
            .background(Capsule().fill(fill.opacity(configuration.isPressed ? 0.7 : 1)))
    }
}

private struct LinkButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12))
            .foregroundStyle(Palette.accent.opacity(configuration.isPressed ? 0.6 : 1))
    }
}

private struct TintedIconLabelStyle: LabelStyle {
    let color: Color

    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 6) {
            configuration.icon.foregroundStyle(color)
            configuration.title
        }
    }
}
