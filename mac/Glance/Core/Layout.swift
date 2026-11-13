import CoreGraphics

// Where the answer goes. Rects are in screen coordinates with the origin at the bottom left, as AppKit has them.
enum Layout {
    static func corner(size: CGSize, in area: CGRect, margin: CGFloat) -> CGRect {
        CGRect(x: area.maxX - size.width - margin, y: area.minY + margin, width: size.width, height: size.height)
    }

    static func centered(size: CGSize, in screen: CGRect) -> CGRect {
        CGRect(x: screen.midX - size.width / 2, y: screen.midY - size.height / 2, width: size.width, height: size.height).integral
    }

    // area is left, top, width, height as fractions of the screen
    static func drawn(_ area: [Double], on screen: CGRect) -> CGRect {
        let w = min(screen.width, max(area[2] * screen.width, 160))
        let h = min(screen.height, max(area[3] * screen.height, 60))
        let x = min(screen.minX + area[0] * screen.width, screen.maxX - w)
        let top = min(area[1] * screen.height, screen.height - h)
        return CGRect(x: x, y: screen.maxY - top - h, width: w, height: h).integral
    }

    // a selection in pixels from the top left of an image, as the fractions Settings keeps
    static func fractions(of selection: CGRect, in size: CGSize) -> [Double] {
        [selection.minX / size.width, selection.minY / size.height, selection.width / size.width, selection.height / size.height]
    }
}
