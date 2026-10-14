import SwiftUI

/// Timings and sizes from the interaction spec (design doc, section 2.3).
/// Both apps use the same values.
enum Spec {
    static let doneHold: Duration = .milliseconds(1500)
    static let refusedHold: Duration = .milliseconds(1800)
    static let failedHold: Duration = .seconds(4)
    static let receivedHold: Duration = .seconds(5)
    /// macOS only: long enough to read the reason and reach the button.
    static let noAccessHold: Duration = .seconds(8)
    /// The queued state collapses to its compact form after this long.
    static let queuedHint: Duration = .seconds(3)

    /// The drop area extends this far around the visible island.
    static let hitOutset: CGFloat = 24
    /// The "其他设备" strip at the bottom of the target island. Dragging down
    /// into it (or below it) opens the device list. It replaces the dwell
    /// timer: holding a drag at the top edge opens Mission Control on macOS
    /// 15+, so nothing may require lingering up there.
    static let moreStripHeight: CGFloat = 40
    static let pickerWidth: CGFloat = 420
    /// The picker widens with the devices, up to this; LocalSend devices
    /// beyond `pickerMaxDevices` are left out.
    static let pickerMaxWidth: CGFloat = 528
    static let pickerMaxDevices = 6
    /// Distance from the screen top of the floating capsule on screens
    /// without a notch.
    static let capsuleTop: CGFloat = 8

    /// The panel is fixed in size, large enough for the largest state; the
    /// island is drawn at its top center.
    static let panelSize = CGSize(width: 560, height: 260)

    static let spring = Animation.spring(response: 0.45, dampingFraction: 0.78)
}

enum Palette {
    static let island = Color.black
    static let text = Color.white
    static let secondary = Color(white: 0.62)
    static let tertiary = Color(white: 0.45)
    static let tile = Color(white: 0.16)
    static let tileHot = Color(white: 0.13)
    static let track = Color(white: 0.22)
    /// The app icon's colors: the accretion disk's warm light, the ring's
    /// cream and the deep-space plate (assets/app-icon.svg).
    static let accent = Color(red: 0xFF / 255, green: 0xB5 / 255, blue: 0x47 / 255)
    /// Text on `accent`.
    static let onAccent = plate
    /// The disk's gradient, along a progress bar.
    static let disk = LinearGradient(
        colors: [Color(red: 0xF2 / 255, green: 0x68 / 255, blue: 0x3C / 255), accent],
        startPoint: .leading, endPoint: .trailing)
    static let cream = Color(red: 0xF7 / 255, green: 0xF3 / 255, blue: 0xEA / 255)
    static let plate = Color(red: 0x1D / 255, green: 0x1A / 255, blue: 0x2E / 255)
    static let success = Color(red: 0.19, green: 0.82, blue: 0.35)
    static let warning = Color(red: 1.0, green: 0.62, blue: 0.04)
}
