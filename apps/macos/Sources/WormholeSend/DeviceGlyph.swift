import AppKit
import SwiftUI

/// The app's own device icons (assets/device-icons.svg), drawn like the menu
/// bar icon: a 24-unit grid, 1.8-unit strokes with round caps and joins.
enum DeviceGlyph {
    case desktop, laptop, phone, tablet, web, server

    /// From LocalSend's deviceType. Laptops and tablets don't announce
    /// themselves apart from desktops and phones.
    init(deviceType: String?) {
        switch deviceType {
        case "mobile": self = .phone
        case "web": self = .web
        case "server", "headless": self = .server
        default: self = .desktop
        }
    }

    private static let lineWidth: CGFloat = 1.8

    /// The strokes as one fillable outline, on the 24-unit grid, y down.
    var outline: CGPath {
        let path = CGMutablePath()
        var dots: [CGPoint] = []
        func box(_ x: CGFloat, _ y: CGFloat, _ width: CGFloat, _ height: CGFloat, _ radius: CGFloat) {
            path.addRoundedRect(
                in: CGRect(x: x, y: y, width: width, height: height), cornerWidth: radius, cornerHeight: radius)
        }
        func line(_ x1: CGFloat, _ y1: CGFloat, _ x2: CGFloat, _ y2: CGFloat) {
            path.move(to: CGPoint(x: x1, y: y1))
            path.addLine(to: CGPoint(x: x2, y: y2))
        }
        switch self {
        case .desktop:
            box(3, 4, 18, 12, 2.5)
            line(12, 16, 12, 19.5)
            line(8.5, 20, 15.5, 20)
        case .laptop:
            box(5, 5.5, 14, 10, 2)
            line(2.5, 18.5, 21.5, 18.5)
        case .phone:
            box(7, 2.5, 10, 19, 3)
            line(11, 18.5, 13, 18.5)
        case .tablet:
            box(4.5, 3, 15, 18, 2.5)
            line(11, 18, 13, 18)
        case .web:
            path.addEllipse(in: CGRect(x: 3.5, y: 3.5, width: 17, height: 17))
            path.addEllipse(in: CGRect(x: 8.4, y: 3.5, width: 7.2, height: 17))
            line(3.5, 12, 20.5, 12)
        case .server:
            box(4, 4, 16, 7, 2)
            box(4, 13, 16, 7, 2)
            dots = [CGPoint(x: 8, y: 7.5), CGPoint(x: 8, y: 16.5)]
        }
        let outline = CGMutablePath()
        // Normalized, so where strokes cross they can't cancel out in the fill.
        outline.addPath(
            path.copy(strokingWithWidth: Self.lineWidth, lineCap: .round, lineJoin: .round, miterLimit: 10)
                .normalized())
        // As wide as the strokes, like a round cap on a zero-length line.
        let radius = Self.lineWidth / 2
        for dot in dots {
            outline.addEllipse(in: CGRect(x: dot.x - radius, y: dot.y - radius, width: 2 * radius, height: 2 * radius))
        }
        return outline
    }

    /// A template image for menu rows, the size of an SF Symbol there.
    func menuImage(size: CGFloat = 16) -> NSImage {
        let outline = outline
        let image = NSImage(size: NSSize(width: size, height: size), flipped: true) { _ in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            context.scaleBy(x: size / 24, y: size / 24)
            context.addPath(outline)
            context.setFillColor(NSColor.black.cgColor)
            context.fillPath()
            return true
        }
        image.isTemplate = true
        return image
    }
}

/// A device glyph `size` points square.
struct DeviceGlyphShape: Shape {
    let glyph: DeviceGlyph

    func path(in rect: CGRect) -> Path {
        let scale = min(rect.width, rect.height) / 24
        return Path(glyph.outline)
            .applying(CGAffineTransform(scaleX: scale, y: scale))
            .offsetBy(dx: rect.midX - 12 * scale, dy: rect.midY - 12 * scale)
    }
}
