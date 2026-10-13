import AppKit

/// Short system sounds at the moments that matter: files delivered or
/// received, a transfer that failed, a pairing request. Off from the menu.
@MainActor
enum Sound: String {
    case sent = "Glass"
    case received = "Hero"
    case failed = "Basso"
    case pairRequest = "Ping"

    static var enabled: Bool {
        get { UserDefaults.standard.object(forKey: key) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }

    private static let key = "sounds"
    /// Kept until it has played.
    private static var playing: NSSound?

    func play() {
        guard Self.enabled, let sound = NSSound(named: rawValue)?.copy() as? NSSound else { return }
        sound.volume = 0.6
        Self.playing = sound
        sound.play()
    }
}
