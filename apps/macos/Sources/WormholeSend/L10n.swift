import Foundation
import os

/// The app's language: Chinese, English or Japanese. By default macOS picks
/// it from the user's preferred languages among the bundle's localizations,
/// the same as for its own dialogs and the Local Network prompt. The menu
/// can choose one (`choose`), which switches the app's texts at once; the
/// core is told too, so its messages match.
enum Language: String, CaseIterable, Sendable {
    case zh, en, ja

    /// Its own name, as the language menu lists it.
    var name: String {
        switch self {
        case .zh: "简体中文"
        case .en: "English"
        case .ja: "日本語"
        }
    }

    /// The bundle's localization for it.
    var localization: String {
        switch self {
        case .zh: "zh-Hans"
        case .en: "en"
        case .ja: "ja"
        }
    }

    /// zh* is Chinese, ja* Japanese, anything else English.
    init(localization: String) {
        let code = localization.lowercased()
        self = code.hasPrefix("zh") ? .zh : code.hasPrefix("ja") ? .ja : .en
    }

    static var current: Language { state.withLock { $0 } }

    /// The language chosen in the menu, or nil when the app follows the
    /// system. It is this app's `AppleLanguages`, the setting System
    /// Settings › General › Language & Region › Applications writes too, so
    /// the two agree, and macOS's own dialogs follow from the next launch.
    static var choice: Language? {
        let domain = UserDefaults.standard.persistentDomain(forName: Bundle.main.bundleIdentifier ?? "")
        guard let first = (domain?["AppleLanguages"] as? [String])?.first else { return nil }
        return Language(localization: first)
    }

    /// The language the system's preferred languages give, read afresh:
    /// `Locale.preferredLanguages` keeps the list from launch, which
    /// includes the app's own choice.
    static var system: Language {
        let global = UserDefaults.standard.persistentDomain(forName: UserDefaults.globalDomain)
        let preferred = global?["AppleLanguages"] as? [String] ?? Locale.preferredLanguages
        let best = Bundle.preferredLocalizations(from: Bundle.main.localizations, forPreferences: preferred).first
        return Language(localization: best ?? "en")
    }

    /// Chooses a language, or nil to follow the system again.
    static func choose(_ language: Language?) {
        if let language {
            UserDefaults.standard.set([language.localization], forKey: "AppleLanguages")
        } else {
            UserDefaults.standard.removeObject(forKey: "AppleLanguages")
        }
        let now = language ?? system
        state.withLock { $0 = now }
    }

    /// From launch: what macOS picked for this app.
    private static let state = OSAllocatedUnfairLock(
        initialState: Language(localization: Bundle.main.preferredLocalizations.first ?? "en"))
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
