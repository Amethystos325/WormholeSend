import Foundation

/// A paired device. Fake data for now; the Rust core provides the real list.
struct Device: Identifiable, Equatable {
    let id: String
    var name: String
    /// SF Symbol of the device type.
    var symbol: String
    var online: Bool
    var isDefault: Bool

    static let samples: [Device] = [
        Device(id: "desk", name: "书房台式机", symbol: "desktopcomputer", online: true, isDefault: true),
        Device(id: "air", name: "MacBook Air", symbol: "laptopcomputer", online: true, isDefault: false),
        Device(id: "ipad", name: "iPad", symbol: "ipad", online: false, isDefault: false),
    ]
}

struct Transfer: Equatable {
    var device: Device
    var files: [URL]
    /// "季度报告.pdf" or "3 个文件".
    var label: String
    var progress: Double = 0

    var fileCount: Int { files.count }
}

enum FileLabel {
    static func of(_ urls: [URL]) -> String {
        switch urls.count {
        case 0: "文件"
        case 1: urls[0].lastPathComponent
        default: "\(urls.count) 个文件"
        }
    }
}
