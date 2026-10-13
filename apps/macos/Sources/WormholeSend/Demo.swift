import AppKit

/// Freezes the island in one phase, for screenshots and visual checks:
///
///     WormholeSend --demo picker [--screen external] [--devices 1]
///
/// Phases: hint, target, picker, picker-offline, single, unavailable,
/// sending, queued, queued-compact, done, failed, refused, receiving,
/// received, interrupted, refused-repair, pair-request, pair-waiting,
/// notice, notice-failed. Sample data; the core is not started.
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
            case "single":
                model.devices = [Device.samples[0]]
                model.phase = .picker(hot: 0)
            case "unavailable": model.phase = .unavailable("没有在线的设备")
            case "sending":
                model.transfer = sample
                model.transfer?.progress = 0.45
                model.phase = .sending
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
                model.phase = .failed("网络中断")
            case "refused": model.phase = .refused(name: "iPad", reason: "离线")
            case "refused-repair": model.phase = .refused(name: "MacBook Air", reason: "需要重新配对")
            case "pair-request": model.phase = .pairRequest(pairId: "demo", alias: "书房台式机", code: "482019")
            case "pair-waiting": model.phase = .pairWaiting(alias: "书房台式机")
            case "notice": model.phase = .notice(text: "已与 书房台式机 配对", success: true)
            case "notice-failed": model.phase = .notice(text: "配对失败：对方拒绝了配对", success: false)
            case "receiving":
                let received = DemoFiles.make()
                model.transfer = Transfer(device: Device.samples[1], files: received, label: FileLabel.of(received), progress: 0.62)
                model.waitingCount = 1
                model.phase = .receiving
            case "interrupted": model.phase = .interrupted("对方取消了发送")
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
