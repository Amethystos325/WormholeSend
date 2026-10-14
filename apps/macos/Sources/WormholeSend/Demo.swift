import AppKit

/// Freezes the island in one phase, for screenshots and visual checks:
///
///     WormholeSend --demo picker [--screen external] [--devices 1]
///
/// Phases: hint, target, picker, picker-offline, picker-localsend, single,
/// unavailable, sending, awaiting, queued, queued-compact, done, failed,
/// declined, refused, no-access, receiving, receiving-localsend, received,
/// interrupted, refused-repair, pair-request, pair-waiting, receive-request,
/// receive-text, notice, notice-copied, notice-failed. Sample data; the core
/// is not started.
@MainActor
enum Demo {
    static func apply(to model: IslandModel, place: @escaping (NSScreen) -> Void) {
        guard let name = Arguments.value(of: "--demo") else { return }
        if Arguments.value(of: "--screen") == "external",
            let external = NSScreen.screens.first(where: { !ScreenMode(screen: $0).isNotch })
        {
            place(external)
        }
        if let count = Arguments.value(of: "--devices").flatMap(Int.init) {
            model.devices = Array(Device.samples.prefix(count))
        }
        let files = [URL(fileURLWithPath: "/tmp/季度报告.pdf")]
        let sample = Transfer(device: Device.samples[0], files: files, label: "季度报告.pdf")

        // Let the island grow out of the notch, as it would for real.
        Task {
            try? await Task.sleep(for: .milliseconds(300))
            model.dragLabel = "季度报告.pdf"
            switch name {
            case "hint": model.phase = .hint
            case "target": model.phase = .target
            case "picker": model.phase = .picker(hot: 1)
            case "picker-offline": model.phase = .picker(hot: 2)
            case "picker-localsend":
                model.devices = Device.samples + [Device.localSendSample]
                model.phase = .picker(hot: 3)
            case "single":
                model.devices = [Device.samples[0]]
                model.phase = .picker(hot: 0)
            case "unavailable": model.phase = .unavailable(tr("没有在线的设备", "No devices online", "オンラインのデバイスがありません"))
            case "sending":
                model.transfer = sample
                model.transfer?.progress = 0.45
                // Two progress reports a second apart, so the speed shows.
                var stats = TransferStats(total: 2_100_000_000, files: 1)
                stats.update(bytes: 900_000_000, total: 2_100_000_000, filesDone: 0, at: 0)
                stats.update(bytes: 945_000_000, total: 2_100_000_000, filesDone: 0, at: 1)
                model.transfer?.stats = stats
                model.phase = .sending
            case "awaiting":
                model.transfer = Transfer(device: Device.localSendSample, files: files, label: "季度报告.pdf")
                model.phase = .awaiting
            case "queued":
                model.transfer = sample
                model.phase = .queued(position: 1)
            case "queued-compact":
                model.transfer = sample
                model.queueCollapsed = true
                model.phase = .queued(position: 1)
            case "done":
                model.transfer = sample
                model.phase = .done
            case "failed":
                model.transfer = sample
                model.phase = .failed(tr("网络中断", "The network dropped", "ネットワークが切断されました"), canRetry: true)
            case "declined":
                model.transfer = Transfer(device: Device.localSendSample, files: files, label: "季度报告.pdf")
                model.phase = .failed(tr("Pixel 8 拒绝了接收", "Pixel 8 declined", "Pixel 8 が受信を拒否しました"), canRetry: false)
            case "refused": model.phase = .refused(Device.samples[2].notSent)
            case "no-access": model.phase = .noAccess(app: tr("微信", "WeChat", "WeChat"))
            case "refused-repair":
                var device = Device.samples[1]
                device.needsRepair = true
                model.phase = .refused(device.notSent)
            case "pair-request": model.phase = .pairRequest(pairId: "demo", alias: "书房台式机", code: "482019")
            case "pair-waiting": model.phase = .pairWaiting(alias: "书房台式机")
            case "receive-request":
                let request = ReceiveRequest(
                    id: "demo", device: Device.localSendSample,
                    label: tr("IMG_2041.heic 等 3 个文件", "IMG_2041.heic and 2 more files", "IMG_2041.heic など 3 個のファイル"),
                    total: 8_400_000, text: nil, deadline: .now + 42)
                model.phase = .receiveRequest(request)
            case "receive-text":
                let request = ReceiveRequest(
                    id: "demo", device: Device.localSendSample, label: "", total: 64,
                    text: tr(
                        "明天下午三点在 3 楼会议室，记得带上季度报告的打印版，还有上次讨论的预算表。",
                        "Meeting tomorrow at 3 pm in room 3F. Please bring a printed copy of the quarterly report and the budget sheet.",
                        "明日の午後3時、3階の会議室で。四半期報告書の印刷版と、前回話した予算表を持ってきてください。"),
                    deadline: .now + 7)
                model.phase = .receiveRequest(request)
            case "notice-copied":
                model.phase = .notice(
                    text: tr("已复制到剪贴板", "Copied", "コピーしました"),
                    detail: tr("明天下午三点在 3 楼…", "Meeting tomorrow at 3 pm…", "明日の午後3時、3階の…"), success: true)
            case "notice":
                model.phase = .notice(
                    text: tr("已与 书房台式机 配对", "Paired with 书房台式机", "书房台式机 とペアリングしました"), detail: nil, success: true)
            case "notice-failed":
                model.phase = .notice(
                    text: tr("配对失败：对方拒绝了配对", "Pairing failed: they declined", "ペアリングできませんでした：相手が拒否しました"),
                    detail: nil, success: false)
            case "receiving":
                let received = DemoFiles.make()
                model.transfer = Transfer(device: Device.samples[1], files: received, label: FileLabel.of(received), progress: 0.62)
                model.waitingCount = 1
                model.phase = .receiving
            case "receiving-localsend":
                let received = DemoFiles.make()
                model.transfer = Transfer(
                    device: Device.localSendSample, files: received, label: FileLabel.of(received), progress: 0.4)
                model.phase = .receiving
            case "interrupted": model.phase = .interrupted(tr("对方取消了发送", "The sender cancelled", "送信者がキャンセルしました"))
            case "received":
                let received = DemoFiles.make()
                model.transfer = Transfer(device: Device.samples[1], files: received, label: FileLabel.of(received), progress: 1)
                model.phase = .received
            default:
                print("Unknown demo phase: \(name)")
            }
        }
    }
}
