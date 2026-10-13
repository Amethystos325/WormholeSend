import AppKit

/// Freezes the island in one phase, for screenshots and visual checks:
///
///     WormholeSend --demo picker [--screen external] [--devices 1]
///
/// Phases: hint, target, picker, picker-offline, single, unavailable,
/// sending, queued, queued-compact, done, failed, refused, receiving,
/// received.
@MainActor
enum Demo {
    static func apply(arguments: [String], to model: IslandModel, place: @escaping (NSScreen) -> Void) {
        guard let name = value(of: "--demo", in: arguments) else { return }
        if value(of: "--screen", in: arguments) == "external",
            let external = NSScreen.screens.first(where: { !ScreenMode(screen: $0).isNotch })
        {
            place(external)
        }
        if let count = value(of: "--devices", in: arguments).flatMap(Int.init) {
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
            case "refused": model.phase = .refused("iPad")
            case "receiving":
                let received = DemoFiles.make()
                model.transfer = Transfer(device: Device.samples[1], files: received, label: FileLabel.of(received), progress: 0.62)
                model.phase = .receiving
            case "received":
                let received = DemoFiles.make()
                model.transfer = Transfer(device: Device.samples[1], files: received, label: FileLabel.of(received), progress: 1)
                model.phase = .received
            default:
                print("Unknown demo phase: \(name)")
            }
        }
    }

    private static func value(of flag: String, in arguments: [String]) -> String? {
        guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }
}
