import Foundation

/// The app's language: Chinese, English or Japanese. macOS picks it from
/// the user's preferred languages among the bundle's localizations (also
/// per app, in System Settings › General › Language & Region), the same as
/// for its own dialogs and the Local Network prompt. The core is started in
/// the same language, so its messages match.
enum Language: String {
    case zh, en, ja

    static let current: Language = {
        let preferred = Bundle.main.preferredLocalizations.first?.lowercased() ?? "en"
        if preferred.hasPrefix("zh") { return .zh }
        if preferred.hasPrefix("ja") { return .ja }
        return .en
    }()
}

/// The text in the app's language, all three written at the call site
/// (like the core's `tr!`).
func tr(_ zh: String, _ en: String, _ ja: String) -> String {
    switch Language.current {
    case .zh: zh
    case .en: en
    case .ja: ja
    }
}

/// "1 file", "3 files": English needs the singular.
func plural(_ count: Int, _ one: String, _ many: String) -> String {
    "\(count) \(count == 1 ? one : many)"
}
