#!/usr/bin/env bash
# Submit one .app or .pkg to Apple notarytool and staple the ticket.
# An .app is zipped for upload; the staple is applied to the bundle itself.
# Env: APPLE_API_KEY_ID, APPLE_API_ISSUER_ID, APPLE_API_PRIVATE_KEY
set -euo pipefail

ARTIFACT="${1:?path to .app or .pkg}"
[[ -e "$ARTIFACT" ]] || { echo "Artifact not found: $ARTIFACT" >&2; exit 1; }
[[ -n "${APPLE_API_KEY_ID:-}" && -n "${APPLE_API_ISSUER_ID:-}" && -n "${APPLE_API_PRIVATE_KEY:-}" ]] || {
  echo "Missing APPLE_API_KEY_ID, APPLE_API_ISSUER_ID, or APPLE_API_PRIVATE_KEY." >&2
  exit 1
}

KEY="${RUNNER_TEMP:-/tmp}/AuthKey.p8"
umask 077
if [[ "$APPLE_API_PRIVATE_KEY" != *$'\n'* && "$APPLE_API_PRIVATE_KEY" == *'\\n'* ]]; then
  printf '%b\n' "$APPLE_API_PRIVATE_KEY" >"$KEY"
else
  printf '%s\n' "$APPLE_API_PRIVATE_KEY" >"$KEY"
fi
chmod 600 "$KEY"

SUBMIT="$ARTIFACT"
TMPDIR_NOTARIZE=""
cleanup() { [[ -z "$TMPDIR_NOTARIZE" ]] || rm -rf "$TMPDIR_NOTARIZE"; }
trap cleanup EXIT
if [[ "$ARTIFACT" == *.app ]]; then
  TMPDIR_NOTARIZE="$(mktemp -d)"
  SUBMIT="$TMPDIR_NOTARIZE/notarize.zip"
  ditto -c -k --keepParent "$ARTIFACT" "$SUBMIT"
fi

RESULT="$(mktemp)"
set +e
xcrun notarytool submit "$SUBMIT" \
  --key "$KEY" \
  --key-id "$APPLE_API_KEY_ID" \
  --issuer "$APPLE_API_ISSUER_ID" \
  --wait \
  --output-format json >"$RESULT"
status=$?
set -e
jq -r '"notary id=\(.id // "") status=\(.status // "") message=\(.message // "")"' "$RESULT" || cat "$RESULT"
if [[ $status -ne 0 ]]; then
  id="$(jq -r '.id // empty' "$RESULT" 2>/dev/null || true)"
  if [[ -n "$id" ]]; then
    xcrun notarytool log "$id" \
      --key "$KEY" \
      --key-id "$APPLE_API_KEY_ID" \
      --issuer "$APPLE_API_ISSUER_ID" || true
  fi
  exit "$status"
fi

xcrun stapler staple "$ARTIFACT"
xcrun stapler validate "$ARTIFACT"
