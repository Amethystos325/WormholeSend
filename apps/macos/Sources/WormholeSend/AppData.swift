import AppKit

/// Files inside another app's data (macOS 14+ App Data protection). WeChat,
/// for one, drags out a plain file URL into its own container, without the
/// access grant a native drag carries, so this app can see the file but not
/// open it (EPERM; `stat` still works, so the core would only fail once the
/// upload starts). Full Disk Access lifts the protection.
enum AppData {
    /// The first dropped file that the protection keeps from being read.
    static func firstBlocked(_ urls: [URL]) -> URL? {
        urls.first { !canOpen($0.path) }
    }

    /// System Settings › Privacy & Security › Full Disk Access, with this app
    /// selected in Finder to drag into the list. There is no API to add it,
    /// and the system lists an app on its own only after it is denied one of
    /// the files that protection covers, like Mail's or Safari's, which this
    /// app has no business touching.
    static func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
        NSWorkspace.shared.activateFileViewerSelecting([Bundle.main.bundleURL])
    }

    /// False only when the system denies it, not for a missing file or a
    /// plain Unix permission, which the core reports as before.
    private static func canOpen(_ path: String) -> Bool {
        let fd = open(path, O_RDONLY | O_NONBLOCK)
        if fd >= 0 {
            close(fd)
            return true
        }
        return errno != EPERM
    }

    /// "WeChat" for a file under ~/Library/Containers/com.tencent.xinWeChat;
    /// nil when the path doesn't say which app.
    static func appName(owning url: URL) -> String? {
        let containers = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Containers", isDirectory: true).standardizedFileURL.pathComponents
        let components = url.standardizedFileURL.pathComponents
        guard components.count > containers.count, Array(components.prefix(containers.count)) == containers,
            let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: components[containers.count])
        else { return nil }
        let name = FileManager.default.displayName(atPath: app.path)
        return name.hasSuffix(".app") ? String(name.dropLast(4)) : name
    }
}
