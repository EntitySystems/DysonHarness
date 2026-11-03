#!/usr/bin/env bash
# Build a .pkg that installs DysonHarness.app to /Applications and sign it with Developer ID Installer.
# The keychain that holds the installer identity must already be on the user search list.
set -euo pipefail

APP="${1:?path to DysonHarness.app}"
OUT="${2:?output .pkg path}"
VERSION="${3:?version}"
INSTALLER_IDENTITY="${4:?Developer ID Installer identity}"

[[ -d "$APP" ]] || { echo "App not found: $APP" >&2; exit 1; }
mkdir -p "$(dirname "$OUT")"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

pkgbuild \
  --component "$APP" \
  --install-location /Applications \
  --identifier com.entitysystems.dysonharness.pkg \
  --version "$VERSION" \
  "$WORKDIR/unsigned.pkg"

productsign --sign "$INSTALLER_IDENTITY" "$WORKDIR/unsigned.pkg" "$OUT"
pkgutil --check-signature "$OUT"
