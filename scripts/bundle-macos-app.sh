#!/usr/bin/env bash
# Wrap a DysonHarness.UI.macOS publish directory as DysonHarness.app and ad-hoc sign it.
# The publish directory is replaced by a folder that contains only the app bundle.
set -euo pipefail

PUBLISH="${1:?publish directory}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PLIST="$ROOT/src/native/macos/Info.plist"
STAGE="$(mktemp -d)"

cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

cp -R "$PUBLISH"/. "$STAGE/"
rm -rf "$PUBLISH"
mkdir -p "$PUBLISH/DysonHarness.app/Contents/MacOS" "$PUBLISH/DysonHarness.app/Contents/Resources"
cp -R "$STAGE"/. "$PUBLISH/DysonHarness.app/Contents/MacOS/"
cp "$PLIST" "$PUBLISH/DysonHarness.app/Contents/Info.plist"

LOGO="$ROOT/src/Harness/DysonHarness.UI.Windows/Assets/dyson.ico"
MASTER="$STAGE/DysonHarnessIcon.png"
swift "$ROOT/scripts/make-macos-icon.swift" "$LOGO" "$MASTER"
ICONSET="$STAGE/DysonHarness.iconset"
mkdir -p "$ICONSET"
icon_png() {
  sips -z "$1" "$1" "$MASTER" --out "$ICONSET/$2" >/dev/null
}
icon_png 16 icon_16x16.png
icon_png 32 icon_16x16@2x.png
icon_png 32 icon_32x32.png
icon_png 64 icon_32x32@2x.png
icon_png 128 icon_128x128.png
icon_png 256 icon_128x128@2x.png
icon_png 256 icon_256x256.png
icon_png 512 icon_256x256@2x.png
icon_png 512 icon_512x512.png
icon_png 1024 icon_512x512@2x.png
iconutil -c icns "$ICONSET" -o "$PUBLISH/DysonHarness.app/Contents/Resources/DysonHarness.icns"
cp "$MASTER" "$PUBLISH/DysonHarness.app/Contents/MacOS/DysonHarnessIcon.png"

codesign --force --deep --sign - "$PUBLISH/DysonHarness.app"
codesign --verify --deep --strict "$PUBLISH/DysonHarness.app"
