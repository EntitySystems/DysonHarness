#!/usr/bin/env bash
# Writes version.json (shipped next to DysonHarness.exe) for the in-app updater.
# Version comes from MSBuild ($(Version) / $(InformationalVersion)); CI stamps CalVer + channel.
set -euo pipefail

OUTPUT_PATH="${1:?OutputPath required}"
VERSION="${2:-}"
INFORMATIONAL_VERSION="${3:-}"
RID="${4:-win-x64}"
REPO="${5:-EntitySystems/DysonHarness}"
CHANNEL="${6:-}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

channel_from_branch() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
    main|master) printf 'stable' ;;
    release-preview) printf 'preview' ;;
    *) printf 'preview' ;;
  esac
}

normalize_channel() {
  case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')" in
    stable) printf 'stable' ;;
    preview) printf 'preview' ;;
    *) printf '' ;;
  esac
}

[[ -n "$VERSION" ]] || VERSION="1.0.0"
[[ -n "$INFORMATIONAL_VERSION" ]] || INFORMATIONAL_VERSION="$VERSION"
[[ -n "$RID" ]] || RID="win-x64"

resolved_channel=""
if [[ -n "$CHANNEL" ]]; then
  resolved_channel="$(normalize_channel "$CHANNEL")"
fi

if [[ -z "$resolved_channel" ]]; then
  branch=""
  if [[ "${GITHUB_ACTIONS:-}" == "true" && -n "${GITHUB_REF_NAME:-}" ]]; then
    branch="$(printf '%s' "$GITHUB_REF_NAME" | tr -d '\r\n')"
  fi
  if [[ -z "$branch" ]]; then
    if branch="$(git -C "$REPO_ROOT" rev-parse --abbrev-ref HEAD 2>/dev/null)"; then
      branch="$(printf '%s' "$branch" | tr -d '\r\n')"
      if [[ -z "$branch" || "$branch" == "HEAD" ]]; then
        branch=""
      fi
    else
      branch=""
    fi
  fi
  if [[ -z "$branch" ]]; then
    resolved_channel="preview"
  else
    resolved_channel="$(channel_from_branch "$branch")"
  fi
fi

content="{
  \"version\": \"${VERSION}\",
  \"informationalVersion\": \"${INFORMATIONAL_VERSION}\",
  \"channel\": \"${resolved_channel}\",
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
