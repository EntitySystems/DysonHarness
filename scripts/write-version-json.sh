#!/usr/bin/env bash
# Writes version.json (shipped next to DysonHarness.exe) for the in-app updater.
# Version comes from MSBuild ($(Version) / $(InformationalVersion)); CI stamps CalVer.
set -euo pipefail

OUTPUT_PATH="${1:?OutputPath required}"
VERSION="${2:-}"
INFORMATIONAL_VERSION="${3:-}"
RID="${4:-win-x64}"
REPO="${5:-EntitySystems/DysonHarness}"

[[ -n "$VERSION" ]] || VERSION="1.0.0"
[[ -n "$INFORMATIONAL_VERSION" ]] || INFORMATIONAL_VERSION="$VERSION"
[[ -n "$RID" ]] || RID="win-x64"

content="{
  \"version\": \"${VERSION}\",
  \"informationalVersion\": \"${INFORMATIONAL_VERSION}\",
  \"rid\": \"${RID}\",
  \"repo\": \"${REPO}\"
}"

mkdir -p "$(dirname "$OUTPUT_PATH")"

if [[ -f "$OUTPUT_PATH" ]]; then
  existing="$(cat "$OUTPUT_PATH")"
  if [[ "$existing" == "$content" ]]; then
    exit 0
  fi
fi

printf '%s' "$content" > "$OUTPUT_PATH"
