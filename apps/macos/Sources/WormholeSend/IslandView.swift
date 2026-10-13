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
        case .queued: compactQueued ? "queued-compact" : "queued"
        case .done: "done"
        case .failed: "failed"
        case .refused: "refused"
        case .receiving: "receiving"
        case .received: "received"
        case .interrupted: "interrupted"
        case .pairRequest: "pair-request"
        case .pairWaiting: "pair-waiting"
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
                    device: transfer.device, title: "发送到 \(transfer.device.name)",
                    progress: transfer.progress, detail: transfer.stats.line, onCancel: model.cancelTransfer)
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
                    text: "已送达 \(transfer.device.name)", trailing: transfer.label)
            }
        case .failed(let reason):
            FailedContent(reason: reason, onRetry: model.retry)
        case .refused(let name, let reason):
            StatusLine(symbol: "nosign", color: Palette.warning, text: "\(name) \(reason)，未发送")
        case .receiving:
            if let transfer = model.transfer {
                ReceivingContent(transfer: transfer, waiting: model.waitingCount)
            }
        case .interrupted(let reason):
            StatusLine(symbol: "exclamationmark.triangle.fill", color: Palette.warning, text: "传输中断", trailing: reason)
        case .pairRequest(_, let alias, let code):
            PairRequestContent(alias: alias, code: code, onDecline: model.declinePair, onAccept: model.acceptPair)
        case .pairWaiting(let alias):
            StatusLine(symbol: "clock", color: Palette.secondary, text: "等待 \(alias) 确认…")
        case .notice(let text, let success):
            StatusLine(
                symbol: success ? "checkmark.circle.fill" : "exclamationmark.triangle.fill",
                color: success ? Palette.success : Palette.warning, text: text)
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
                    Text("松手发送到 \(device.name)")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(Palette.text)
                    Text("默认设备")
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
            Text("其他设备")
                .font(.system(size: 11.5, weight: .medium))
                .foregroundStyle(Color(white: 0.8))
            Spacer(minLength: 4)
            Label("往下拖动选择", systemImage: "chevron.down")
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

private struct SingleTargetContent: View {
    let device: Device
    let label: String

    var body: some View {
        HStack(spacing: 14) {
            Avatar(device: device, size: 50, highlighted: true)
            VStack(alignment: .leading, spacing: 4) {
                Text("松手发送到 \(device.name)")
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
            Text("把 \(label) 发送到")
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
        let state = device.needsRepair ? "需重新配对" : (device.online ? "在线" : "离线")
        return device.isDefault ? "\(state) · 默认" : state
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
                Text("排队中 · 前面还有 \(position) 个")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Palette.text)
                Text("对方正在接收其他设备的文件，轮到后自动发送")
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
                Text("排队中")
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
                    Text("\(alias) 请求配对")
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(Palette.text)
                        .lineLimit(1)
                    Text("两边显示的数字一致才配对")
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
                Button("拒绝", action: onDecline)
                    .buttonStyle(PillButtonStyle())
                Button("配对", action: onAccept)
                    .buttonStyle(PillButtonStyle(fill: Palette.accent))
            }
        }
    }
}

private struct FailedContent: View {
    let reason: String
    let onRetry: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill")
                .font(.system(size: 16))
                .foregroundStyle(Palette.warning)
            Text("发送失败：\(reason)")
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Palette.text)
            Spacer()
            Button("重试", action: onRetry)
                .buttonStyle(PillButtonStyle())
        }
    }
}

private struct ReceivingContent: View {
    let transfer: Transfer
    /// Other paired devices waiting to send.
    let waiting: Int

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 10) {
                Avatar(device: transfer.device, size: 30)
                VStack(alignment: .leading, spacing: 2) {
                    Text("\(transfer.device.name) 发来 \(transfer.fileCount) 个文件")
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(Palette.text)
                    Text(waiting > 0 ? "还有 \(waiting) 个设备等待发送" : "已配对 · 自动保存")
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
                Label("已收到 \(transfer.fileCount) 个文件", systemImage: "checkmark.circle.fill")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Palette.text)
                    .labelStyle(TintedIconLabelStyle(color: Palette.success))
                Spacer()
                Button("在访达中显示", action: onReveal)
                    .buttonStyle(LinkButtonStyle())
            }
            HStack(spacing: 6) {
                ForEach(transfer.files.prefix(3), id: \.self) { url in
                    FileChip(url: url)
                }
                if transfer.files.count > 3 {
                    Text("等 \(transfer.files.count) 个")
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
        .help("取消")
    }
}

private struct PillButtonStyle: ButtonStyle {
    var fill = Palette.tile

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 12, weight: .semibold))
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
