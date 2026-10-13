import CWormhole
import Foundation

/// The Rust core behind its C interface (core-ffi/README.md). Commands
/// return at once; what happens afterwards arrives as events, delivered on
/// the main actor.
final class Core: @unchecked Sendable {
    struct Failure: Error, CustomStringConvertible {
        let message: String
        var description: String { message }
    }

    private let handle: OpaquePointer
    private let sink: EventSink

    private init(handle: OpaquePointer, sink: EventSink) {
        self.handle = handle
        self.sink = sink
    }

    static var version: String { String(cString: wh_version()) }

    /// Starts the services. `wh_start` blocks while ports are bound and, on
    /// the first run, while the certificate is generated, so it runs off the
    /// main thread.
    static func start(configJSON json: String, onEvent: @escaping @MainActor (CoreEvent) -> Void) async throws -> Core {
        let sink = EventSink(onEvent)
        return try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                var handle: OpaquePointer?
                let context = Unmanaged.passRetained(sink).toOpaque()
                let response = json.withCString { wh_start($0, eventCallback, context, &handle) }
                let result = Core.parse(response, as: Settings.self)
                switch (result, handle) {
                case (.success, let handle?):
                    continuation.resume(returning: Core(handle: handle, sink: sink))
                case (.failure(let failure), _):
                    Unmanaged<EventSink>.fromOpaque(context).release()
                    continuation.resume(throwing: failure)
                default:
                    Unmanaged<EventSink>.fromOpaque(context).release()
                    continuation.resume(throwing: Failure(message: "核心没有启动"))
                }
            }
        }
    }

    /// Stops the services; no events arrive afterwards.
    func stop() {
        wh_stop(handle)
        Unmanaged.passUnretained(sink).release()
    }

    /// Runs a command and decodes its `result`.
    @discardableResult
    func call<T: Decodable>(_ name: String, _ arguments: [String: Any] = [:], as type: T.Type = Nothing.self) -> Result<T, Failure> {
        var command = arguments
        command["command"] = name
        guard let data = try? JSONSerialization.data(withJSONObject: command) else {
            return .failure(Failure(message: "命令无法编码"))
        }
        let response = String(decoding: data, as: UTF8.self).withCString { wh_command(handle, $0) }
        return Core.parse(response, as: type)
    }

    private static func parse<T: Decodable>(_ response: UnsafeMutablePointer<CChar>?, as type: T.Type) -> Result<T, Failure> {
        guard let response else { return .failure(Failure(message: "核心没有返回结果")) }
        defer { wh_free_string(response) }
        let data = Data(String(cString: response).utf8)
        guard let envelope = try? JSONDecoder().decode(Envelope<T>.self, from: data) else {
            return .failure(Failure(message: "核心返回了无法识别的结果"))
        }
        if envelope.ok, let result = envelope.result {
            return .success(result)
        }
        if envelope.ok, let empty = Nothing() as? T {
            return .success(empty)
        }
        return .failure(Failure(message: envelope.error ?? "未知错误"))
    }
}

/// Holds the event handler for the C callback, which cannot capture.
private final class EventSink: @unchecked Sendable {
    let handler: @MainActor (CoreEvent) -> Void

    init(_ handler: @escaping @MainActor (CoreEvent) -> Void) {
        self.handler = handler
    }
}

/// Called on the core's event thread. The string is only valid during the
/// call, so it is copied before the asynchronous hop to the main thread
/// (asynchronous, never sync: `wh_stop` waits for this callback).
private let eventCallback: WhEventCallback = { context, json in
    guard let context, let json else { return }
    let sink = Unmanaged<EventSink>.fromOpaque(context).takeUnretainedValue()
    let data = Data(String(cString: json).utf8)
    guard let event = try? JSONDecoder().decode(CoreEvent.self, from: data) else { return }
    DispatchQueue.main.async {
        MainActor.assumeIsolated { sink.handler(event) }
    }
}

private struct Envelope<T: Decodable>: Decodable {
    let ok: Bool
    let result: T?
    let error: String?
}

/// For commands whose result is `null`.
struct Nothing: Decodable {
    init() {}
}

struct Settings: Decodable {
    let alias: String
    let saveDir: String
    let port: Int
    let fingerprint: String
}

struct DeviceInfo: Decodable {
    let fingerprint: String
    let alias: String
    let deviceType: String?
    let online: Bool
    let paired: Bool
    let isDefault: Bool
    let needsRepair: Bool
    /// "wormhole" or "localsend".
    let kind: String?
}

/// Every event of core/src/event.rs, with the union of their fields.
struct CoreEvent: Decodable {
    let event: String
    var id: String?
    var deviceId: String?
    var alias: String?
    var deviceType: String?
    var kind: String?
    var online: Bool?
    var paired: Bool?
    var direction: String?
    var files: Int?
    var total: Int64?
    var bytes: Int64?
    var status: String?
    var filesDone: Int?
    var savedPaths: [String]?
    var error: String?
    var position: Int?
    var count: Int?
    var pairId: String?
    var code: String?
    var ok: Bool?
    var reason: String?
    var message: String?
    var requestId: String?
    var label: String?
    var text: String?
}
