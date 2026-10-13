import AppKit

/// A paired device (`devices.list`) or an online LocalSend device
/// (`localsend.list`), as the core lists it. The samples are for demo mode.
struct Device: Identifiable, Equatable {
    let id: String
    var name: String
    /// SF Symbol of the device type.
    var symbol: String
    var online: Bool
    var isDefault: Bool
    /// It refused a transfer: it no longer trusts this device.
    var needsRepair = false
    /// Not paired: its user accepts every transfer, in LocalSend.
    var isLocalSend = false

    init(
        id: String, name: String, symbol: String, online: Bool, isDefault: Bool,
        needsRepair: Bool = false, isLocalSend: Bool = false
    ) {
        self.id = id
        self.name = name
        self.symbol = symbol
        self.online = online
        self.isDefault = isDefault
        self.needsRepair = needsRepair
        self.isLocalSend = isLocalSend
    }

    init(info: DeviceInfo) {
        self.init(
            id: info.fingerprint, name: info.alias, symbol: Device.symbol(for: info.deviceType),
            online: info.online, isDefault: info.isDefault, needsRepair: info.needsRepair,
            isLocalSend: info.kind == "localsend")
    }

    /// Can take files now. A device that needs pairing again counts as
    /// unavailable, like on Windows.
    var usable: Bool { online && !needsRepair }

    var unusableReason: String { needsRepair ? "需要重新配对" : "离线" }

    static func symbol(for deviceType: String?) -> String {
        switch deviceType {
        case "mobile": "iphone"
        case "web": "globe"
        case "server", "headless": "server.rack"
        default: "desktopcomputer"
        }
    }

    static let samples: [Device] = [
        Device(id: "desk", name: "书房台式机", symbol: "desktopcomputer", online: true, isDefault: true),
        Device(id: "air", name: "MacBook Air", symbol: "laptopcomputer", online: true, isDefault: false),
        Device(id: "ipad", name: "iPad", symbol: "ipad", online: false, isDefault: false),
    ]

    static let localSendSample = Device(
        id: "pixel", name: "Pixel 8", symbol: "iphone", online: true, isDefault: false, isLocalSend: true)
}

/// A LocalSend device asks to send files, or a text message (`receive.request`).
struct ReceiveRequest: Equatable {
    let id: String
    let device: Device
    /// "IMG_0001.jpg 等 3 个文件", as the core labels it.
    let label: String
    let total: Int64
    let text: String?
    /// When the core declines it unanswered: a minute after it arrived,
    /// also while it waited behind something else on the island.
    let deadline: Date

    /// The core's `ASK_TIMEOUT`.
    static let timeout: TimeInterval = 60

    /// A text message on one line, for the preview.
    var preview: String? {
        text.map { $0.split(whereSeparator: \.isNewline).joined(separator: " ") }
    }

    /// Whether the preview needs a second line beside the avatar (about
    /// 300 pt are left in the 380 pt island).
    var previewWraps: Bool {
        guard let preview else { return false }
        return (preview as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 11)]).width > 300
    }
}

struct Transfer: Equatable {
    /// The core's transfer ID, once it has one.
    var id: String?
    var device: Device
    /// The files sent, or once received, the saved files and folders.
    var files: [URL]
    /// "季度报告.pdf" or "3 个文件".
    var label: String
    var progress: Double = 0
    var count: Int
    var stats = TransferStats()

    init(id: String? = nil, device: Device, files: [URL], label: String, progress: Double = 0, count: Int? = nil) {
        self.id = id
        self.device = device
        self.files = files
        self.label = label
        self.progress = progress
        self.count = count ?? files.count
    }

    var fileCount: Int { count }
}

/// Bytes and files done of a transfer, and its speed, from the core's
/// progress events.
struct TransferStats: Equatable {
    var bytes: Int64 = 0
    var total: Int64 = 0
    var filesDone = 0
    /// Every file, those inside folders included.
    var files = 0
    /// Bytes per second over the last two seconds, once a second is measured.
    private(set) var speed: Double?
    private var samples: [Sample] = []

    private struct Sample: Equatable {
        let time: TimeInterval
        let bytes: Int64
    }

    private static let window: TimeInterval = 2

    init(total: Int64 = 0, files: Int = 0) {
        self.total = total
        self.files = files
    }

    mutating func update(bytes: Int64, total: Int64, filesDone: Int, at time: TimeInterval) {
        self.bytes = bytes
        self.total = total
        self.filesDone = filesDone
        samples.append(Sample(time: time, bytes: bytes))
        samples.removeAll { time - $0.time > Self.window }
        if let first = samples.first, time - first.time >= 1 {
            speed = Double(bytes - first.bytes) / (time - first.time)
        }
    }

    /// "1.2 GB / 2.1 GB · 45.3 MB/s" for one file, "3 / 12 个文件 · 45.3 MB/s"
    /// for several or a folder.
    var line: String {
        var parts = [files > 1 ? "\(filesDone) / \(files) 个文件" : "\(Self.size(bytes)) / \(Self.size(total))"]
        if let speed { parts.append("\(Self.size(Int64(speed)))/s") }
        return parts.joined(separator: " · ")
    }

    private static func size(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}

/// What a drag carries, named after its first item: "季度报告.pdf",
/// "文件夹 设计稿", "季度报告.pdf 等 3 个文件", "设计稿 等 2 个文件夹",
/// "季度报告.pdf 等 4 个文件或文件夹".
enum FileLabel {
    static func of(_ urls: [URL]) -> String {
        guard let first = urls.first else { return "文件" }
        let name = first.lastPathComponent
        let folders = urls.filter(isFolder).count
        if urls.count == 1 {
            return folders == 1 ? "文件夹 \(name)" : name
        }
        let kind = switch folders {
        case 0: "文件"
        case urls.count: "文件夹"
        default: "文件或文件夹"
        }
        return "\(name) 等 \(urls.count) 个\(kind)"
    }

    /// Packages such as apps are folders on disk but files to the user.
    private static func isFolder(_ url: URL) -> Bool {
        let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey])
        return values?.isDirectory == true && values?.isPackage != true
    }
}
