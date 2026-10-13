import AppKit

/// Short sounds at the moments that matter: files delivered or received, a
/// transfer that failed, a pairing request. Off from the menu.
///
/// The app's own files (assets/sounds, synthesized by make-sounds.py), the
/// same as the Windows app plays; bundle.sh copies them into Resources.
@MainActor
enum Sound: String {
    case sent
    case received
    case failed
    case pairRequest = "pair-request"

    static var enabled: Bool {
        get { UserDefaults.standard.object(forKey: key) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }

    private static let key = "sounds"
    /// Kept until it has played.
    private static var playing: NSSound?

    func play() {
        guard Self.enabled,
            let url = Bundle.main.url(forResource: rawValue, withExtension: "wav"),
            let sound = NSSound(contentsOf: url, byReference: true)
        else { return }
        sound.volume = 0.6
        Self.playing = sound
        sound.play()
    }
}
