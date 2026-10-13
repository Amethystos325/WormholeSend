import AppKit

/// The menu bar icon: a black hole drawn as its photon ring, the accretion
/// disk tilted across it (assets/tray-icon.svg). Drawn on a 24-unit grid,
/// y down.
///
/// The disk's far half hides behind the ring and its near half passes in
/// front, so each part is cut out with a gap around what lies over it.
enum StatusIcon {
    /// The normal icon is a template image, tinted by the menu bar. The
    /// warning one carries an amber dot, so it draws its own colors.
    /// `scale` shrinks it for places like a menu item.
    static func image(warning: Bool, scale: CGFloat = 1) -> NSImage {
        let image = NSImage(size: NSSize(width: size.width * scale, height: size.height * scale), flipped: true) { _ in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            context.scaleBy(x: scale, y: scale)
            let color = warning ? NSColor.labelColor.cgColor : NSColor.black.cgColor
            draw(in: context, color: color, warning: warning)
            return true
        }
        image.isTemplate = !warning
        image.accessibilityDescription = "Wormhole Send"
        return image
    }

    /// 22 × 16 pt: the 24-unit grid cropped to x 1…23, y 4…20.
    static let size = NSSize(width: 22, height: 16)

    private static let ring = (radius: CGFloat(5.0), width: CGFloat(2.0))
    /// Open enough that the near half covers the bottom of the ring
    /// completely, instead of leaving a sliver below it.
    private static let disk = (rx: CGFloat(10.6), ry: CGFloat(4.0), width: CGFloat(1.8))
    /// Clearance between the ring and the disk where one passes the other.
    private static let gap: CGFloat = 1.3
    private static let tilt: CGFloat = -22 * .pi / 180
    private static let badge = (center: CGPoint(x: 20.0, y: 17.0), radius: CGFloat(2.9))
    private static let amber = CGColor(srgbRed: 0.94, green: 0.62, blue: 0.15, alpha: 1)

    private static func draw(in context: CGContext, color: CGColor, warning: Bool) {
        context.translateBy(x: -1, y: -4)
        context.saveGState()
        // Opaque strokes on one layer, faded as a whole: a translucent color
        // (labelColor) would darken where the disk's halves overlap.
        context.setAlpha(color.alpha)
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        let color = color.copy(alpha: 1) ?? color
        context.translateBy(x: 12, y: 12)
        context.rotate(by: tilt)
        context.setStrokeColor(color)
        context.setFillColor(color)
        context.setLineCap(.round)

        // Each part on a layer of its own, so a cut-out only affects it.
        // The far half of the disk, hidden behind the ring.
        layer(context) {
            $0.addPath(diskHalf(front: false))
            $0.setLineWidth(disk.width)
            $0.strokePath()
            clear($0) { $0.addEllipse(in: circle(radius: ring.radius + ring.width / 2 + gap + disk.width / 2)) }
        }

        // The ring, with a gap cut where the near half of the disk crosses it.
        layer(context) {
            $0.addEllipse(in: circle(radius: ring.radius))
            $0.setLineWidth(ring.width)
            $0.strokePath()
            clear($0) {
                $0.addPath(diskHalf(front: true))
                $0.setLineWidth(disk.width + 2 * gap)
                $0.strokePath()
            }
        }

        // The near half of the disk.
        context.addPath(diskHalf(front: true))
        context.setLineWidth(disk.width)
        context.strokePath()
        context.endTransparencyLayer()
        context.restoreGState()

        guard warning else { return }
        clear(context) { $0.addEllipse(in: circle(radius: badge.radius + gap, at: badge.center)) }
        context.setFillColor(amber)
        context.addEllipse(in: circle(radius: badge.radius, at: badge.center))
        context.fillPath()
    }

    /// The near half bulges toward the viewer, down on the grid.
    private static func diskHalf(front: Bool) -> CGPath {
        let path = CGMutablePath()
        path.addArc(
            center: .zero, radius: disk.rx, startAngle: .pi, endAngle: 0, clockwise: front,
            transform: CGAffineTransform(scaleX: 1, y: disk.ry / disk.rx))
        return path
    }

    private static func layer(_ context: CGContext, _ draw: (CGContext) -> Void) {
        context.beginTransparencyLayer(auxiliaryInfo: nil)
        draw(context)
        context.endTransparencyLayer()
    }

    private static func clear(_ context: CGContext, _ shape: (CGContext) -> Void) {
        context.saveGState()
        context.setBlendMode(.clear)
        shape(context)
        if !context.isPathEmpty { context.fillPath() }
        context.restoreGState()
    }

    private static func circle(radius: CGFloat, at center: CGPoint = .zero) -> CGRect {
        CGRect(x: center.x - radius, y: center.y - radius, width: 2 * radius, height: 2 * radius)
    }
}
