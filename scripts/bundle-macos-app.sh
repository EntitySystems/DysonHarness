#!/usr/bin/env bash
# Wrap a DysonHarness.UI.macOS publish directory as DysonHarness.app and sign it.
# The publish directory is replaced by a folder that contains only the app bundle.
#
# No CODESIGN_IDENTITY: ad-hoc signature (local builds and pull-request CI).
# CODESIGN_IDENTITY set: Developer ID, hardened runtime, timestamp, entitlements.
#   CODESIGN_KEYCHAIN      optional keychain path
#   CODESIGN_ENTITLEMENTS  defaults to packaging/macos/entitlements.plist
#   VERSION                CalVer stamped into Info.plist when set
set -euo pipefail

PUBLISH="${1:?publish directory}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PLIST="$ROOT/src/native/macos/Info.plist"
STAGE="$(mktemp -d)"
APP="$PUBLISH/DysonHarness.app"

cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

cp -R "$PUBLISH"/. "$STAGE/"
rm -rf "$PUBLISH"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$STAGE"/. "$APP/Contents/MacOS/"
cp "$PLIST" "$APP/Contents/Info.plist"

if [[ -n "${VERSION:-}" ]]; then
  /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${VERSION}" "$APP/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${VERSION}" "$APP/Contents/Info.plist"
fi

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
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/DysonHarness.icns"
cp "$MASTER" "$APP/Contents/MacOS/DysonHarnessIcon.png"

sign_one() {
  local target="$1"
  if [[ -n "${CODESIGN_KEYCHAIN:-}" ]]; then
    codesign --force --timestamp --options runtime \
      --keychain "$CODESIGN_KEYCHAIN" \
      --sign "$CODESIGN_IDENTITY" \
      "$target"
  else
    codesign --force --timestamp --options runtime \
      --sign "$CODESIGN_IDENTITY" \
      "$target"
  fi
}

sign_developer_id() {
  local entitlements="${CODESIGN_ENTITLEMENTS:-$ROOT/packaging/macos/entitlements.plist}"
  [[ -f "$entitlements" ]] || { echo "Missing entitlements: $entitlements" >&2; exit 1; }

  # codesign treats every executable file in the bundle as nested code.
  # Managed assemblies are not Mach-O; an executable .dll fails the bundle seal.
  while IFS= read -r -d '' bin; do
    file -b "$bin" | grep -q 'Mach-O' && continue
    chmod a-x "$bin"
  done < <(find "$APP" -type f -perm -111 -print0)

  # Longest path first. `read` keeps the rest of the line, including spaces.
  while IFS= read -r -d '' bin; do
    file -b "$bin" | grep -q 'Mach-O' || continue
    printf '%08d %s\n' "${#bin}" "$bin"
  done < <(find "$APP" -type f -print0) | sort -nr | while read -r _len bin; do
    sign_one "$bin"
  done

  if [[ -n "${CODESIGN_KEYCHAIN:-}" ]]; then
    codesign --force --timestamp --options runtime \
      --keychain "$CODESIGN_KEYCHAIN" \
      --entitlements "$entitlements" \
      --sign "$CODESIGN_IDENTITY" \
      "$APP"
  else
    codesign --force --timestamp --options runtime \
      --entitlements "$entitlements" \
      --sign "$CODESIGN_IDENTITY" \
      "$APP"
  fi
}

if [[ -n "${CODESIGN_IDENTITY:-}" ]]; then
  sign_developer_id
else
  codesign --force --deep --sign - "$APP"
fi
codesign --verify --deep --strict "$APP"
