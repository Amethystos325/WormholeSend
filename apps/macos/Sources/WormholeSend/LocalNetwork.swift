import AppKit
import Network

/// Local Network privacy (macOS 15+). Until the user allows it, the system
/// silently drops this app's multicast announcements and its outgoing
/// connections to the LAN, while incoming connections still work: other
/// devices can't find this Mac and sending fails, with no error anywhere.
///
/// There is no API to read the setting. The check opens a UDP connection to
/// the discovery group, which sends nothing, and reads why its path is
/// unusable.
enum LocalNetwork {
    /// Whether access is denied: the path still says so after three seconds
    /// (or the connection failed for that reason). The first launch of a
    /// newly signed build briefly reports "Local network prohibited" even
    /// when access is allowed.
    static func isDenied() async -> Bool {
        await withCheckedContinuation { continuation in
            Probe(continuation).start()
        }
    }

    /// System Settings › Privacy & Security › Local Network.
    static func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Whether this macOS asks for the permission at all.
    static var isAsked: Bool {
        ProcessInfo.processInfo.isOperatingSystemAtLeast(OperatingSystemVersion(majorVersion: 15, minorVersion: 0, patchVersion: 0))
    }
}

/// Everything runs on `queue`, so only the first answer resumes.
private final class Probe: @unchecked Sendable {
    private let queue = DispatchQueue(label: "wormhole.local-network")
    private let connection = NWConnection(host: "224.0.0.167", port: 53320, using: .udp)
    private var continuation: CheckedContinuation<Bool, Never>?

    init(_ continuation: CheckedContinuation<Bool, Never>) {
        self.continuation = continuation
    }

    func start() {
        connection.stateUpdateHandler = { [self] state in
            switch state {
            case .ready:
                finish(denied: false)
            case .failed:
                finish(denied: isDenied)
            default:
                // Waiting can be momentary while the path is evaluated, or
                // last until the user answers the system prompt: the
                // connection becomes ready if access is allowed in time.
                break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 3) { [self] in finish(denied: isDenied) }
    }

    private var isDenied: Bool {
        connection.currentPath?.unsatisfiedReason == .localNetworkDenied
    }

    private func finish(denied: Bool) {
        guard let continuation else { return }
        self.continuation = nil
        connection.stateUpdateHandler = nil
        connection.cancel()
        continuation.resume(returning: denied)
    }
}
