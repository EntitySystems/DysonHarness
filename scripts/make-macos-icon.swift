import AppKit

// Opaque white mark on black. macOS masks CFBundleIconFile to the Dock squircle.
// Do not assign NSApp.applicationIconImage; that draws this square unclipped.
let args = CommandLine.arguments
guard args.count == 3 else {
    fputs("usage: make-macos-icon.swift <logo.png> <out.png>\n", stderr)
    exit(1)
}

let logoURL = URL(fileURLWithPath: args[1])
let outURL = URL(fileURLWithPath: args[2])
guard let loaded = NSImage(contentsOf: logoURL), loaded.isValid else {
    fputs("could not read logo\n", stderr)
    exit(1)
}
guard let best = loaded.representations.max(by: { $0.pixelsWide * $0.pixelsHigh < $1.pixelsWide * $1.pixelsHigh }) else {
    fputs("logo has no image\n", stderr)
    exit(1)
}
let logo = NSImage(size: best.size)
logo.addRepresentation(best)

let size = 1024
guard let rep = NSBitmapImageRep(
    bitmapDataPlanes: nil,
    pixelsWide: size,
    pixelsHigh: size,
    bitsPerSample: 8,
    samplesPerPixel: 4,
    hasAlpha: true,
    isPlanar: false,
    colorSpaceName: .deviceRGB,
    bytesPerRow: 0,
    bitsPerPixel: 0)
else {
    fputs("bitmap failed\n", stderr)
    exit(1)
}

rep.size = NSSize(width: size, height: size)
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
NSColor.black.setFill()
NSBezierPath(rect: NSRect(x: 0, y: 0, width: size, height: size)).fill()
let inset = CGFloat(size) * 0.16
let rect = NSRect(x: inset, y: inset, width: CGFloat(size) - inset * 2, height: CGFloat(size) - inset * 2)
logo.draw(
    in: rect,
    from: NSRect(origin: .zero, size: logo.size),
    operation: .sourceOver,
    fraction: 1)
NSGraphicsContext.restoreGraphicsState()

guard let data = rep.representation(using: .png, properties: [:]) else {
    fputs("png encode failed\n", stderr)
    exit(1)
}

do {
    try data.write(to: outURL)
} catch {
    fputs("\(error)\n", stderr)
    exit(1)
}
