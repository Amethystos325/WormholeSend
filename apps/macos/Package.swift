// swift-tools-version: 6.0
import Foundation
import PackageDescription

// libwormhole.a, copied here by scripts/build-core.sh. A directory of its own,
// so the linker can't pick the dylib that cargo builds next to it.
let vendorLib = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
    .appendingPathComponent("Vendor/lib").path

let package = Package(
    name: "WormholeSend",
    platforms: [.macOS(.v14)],
    targets: [
        .systemLibrary(name: "CWormhole", path: "Sources/CWormhole"),
        .executableTarget(
            name: "WormholeSend",
            dependencies: ["CWormhole"],
            path: "Sources/WormholeSend",
            linkerSettings: [
                .unsafeFlags(["-L", vendorLib]),
                .linkedLibrary("wormhole"),
                // From `cargo rustc -p wormhole-ffi --crate-type staticlib -- --print native-static-libs`.
                .linkedFramework("Security"),
                .linkedFramework("CoreFoundation"),
                .linkedLibrary("iconv"),
            ]
        ),
    ]
)
