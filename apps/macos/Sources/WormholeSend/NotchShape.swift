import SwiftUI

/// The island on a notched screen: flush with the screen top, with small
/// concave "shoulders" at the top corners like the hardware notch, and
/// rounded bottom corners. The shoulders sit outside the body, so the shape
/// is `2 * shoulder` wider than the body it frames.
struct NotchShape: Shape {
    var shoulder: CGFloat
    var bottomRadius: CGFloat

    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(shoulder, bottomRadius) }
        set {
            shoulder = newValue.first
            bottomRadius = newValue.second
        }
    }

    func path(in rect: CGRect) -> Path {
        let s = min(shoulder, rect.width / 4)
        let r = max(0, min(bottomRadius, rect.height - s, (rect.width - 2 * s) / 2))
        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: rect.minY))
        path.addQuadCurve(
            to: CGPoint(x: rect.minX + s, y: rect.minY + s),
            control: CGPoint(x: rect.minX + s, y: rect.minY))
        path.addLine(to: CGPoint(x: rect.minX + s, y: rect.maxY - r))
        path.addQuadCurve(
            to: CGPoint(x: rect.minX + s + r, y: rect.maxY),
            control: CGPoint(x: rect.minX + s, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.maxX - s - r, y: rect.maxY))
        path.addQuadCurve(
            to: CGPoint(x: rect.maxX - s, y: rect.maxY - r),
            control: CGPoint(x: rect.maxX - s, y: rect.maxY))
        path.addLine(to: CGPoint(x: rect.maxX - s, y: rect.minY + s))
        path.addQuadCurve(
            to: CGPoint(x: rect.maxX, y: rect.minY),
            control: CGPoint(x: rect.maxX - s, y: rect.minY))
        path.closeSubpath()
        return path
    }
}

/// How the island appears on the screen it is shown on.
enum ScreenMode: Equatable {
    /// A notched screen: the island grows out of the hardware notch.
    case notch(width: CGFloat, height: CGFloat)
    /// A screen without a notch: a capsule floating below the screen top.
    case capsule

    init(screen: NSScreen) {
        let top = screen.safeAreaInsets.top
        if top > 0,
            let left = screen.auxiliaryTopLeftArea,
            let right = screen.auxiliaryTopRightArea
        {
            self = .notch(width: screen.frame.width - left.width - right.width, height: top)
        } else {
            self = .capsule
        }
    }

    var isNotch: Bool {
        if case .notch = self { true } else { false }
    }

    /// Distance from the screen top to the island's top edge.
    var topOffset: CGFloat {
        isNotch ? 0 : Spec.capsuleTop
    }

    /// The camera band at the top of the island, where nothing is drawn.
    var cameraBand: CGFloat {
        if case .notch(_, let height) = self { height } else { 0 }
    }
}
