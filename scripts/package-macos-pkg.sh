#!/usr/bin/env bash
# Build a .pkg that installs the .app to /Applications and sign it with Developer ID Installer.
# The keychain that holds the installer identity must already be on the user search list.
set -euo pipefail

APP="${1:?path to the .app}"
OUT="${2:?output .pkg path}"
VERSION="${3:?version}"
INSTALLER_IDENTITY="${4:?Developer ID Installer identity}"

[[ -d "$APP" ]] || { echo "App not found: $APP" >&2; exit 1; }
mkdir -p "$(dirname "$OUT")"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# pkgbuild records the component's file name, so the install lands at
# /Applications/<app name>.app. scripts/postinstall removes the old
# /Applications/DysonHarness.app (the name shipped before the display name
# change) and nothing else: app data under ~/Library stays.
cat >"$WORKDIR/postinstall" <<'EOF'
#!/bin/bash
rm -rf "/Applications/DysonHarness.app"
exit 0
EOF
chmod 755 "$WORKDIR/postinstall"

pkgbuild \
  --component "$APP" \
  --install-location /Applications \
  --scripts "$WORKDIR" \
  --identifier com.entitysystems.dysonharness.pkg \
  --version "$VERSION" \
  "$WORKDIR/unsigned.pkg"

productsign --sign "$INSTALLER_IDENTITY" "$WORKDIR/unsigned.pkg" "$OUT"
pkgutil --check-signature "$OUT"
