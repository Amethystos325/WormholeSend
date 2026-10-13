// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "WormholeSend",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "WormholeSend", path: "Sources/WormholeSend")
    ]
)
