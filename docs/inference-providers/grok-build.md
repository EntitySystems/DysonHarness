# Grok Build

xAI Grok via the **Grok CLI chat-proxy**. Dyson signs in natively (OIDC device-code OAuth) and talks to `https://cli-chat-proxy.grok.com/v1` itself; the old CLIProxyAPI route (`cliproxy-grok`) remains as a **legacy, deprecated** fallback.

Research date: **2026-09-30**. The wire protocol is undocumented and ported from [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) (MIT, pinned sha in [THIRD-PARTY-NOTICES.md](../../THIRD-PARTY-NOTICES.md)); none of it was verified against live xAI from the automated tests.

## Product

| Path | `ManagedSource` | Notes |
| ---- | --------------- | ----- |
| **Native** (primary) | `xai-grok` | No CLIProxy binary, process, port or management key. Settings → Models → Import **Grok Build (xAI)** |
| Legacy CLIProxy | `cliproxy-grok` | Deprecated; card offers **Switch to native Grok** |

**ToS / identity warning.** To use subscription entitlements Dyson presents itself as the official Grok CLI (public Grok CLI OAuth client id, `X-XAI-Token-Auth: xai-grok-cli`, `x-grok-client-identifier: grok-shell`, `User-Agent: xai-grok-workspace/<ver>`). CLIProxy does exactly the same. This is unofficial, may violate xAI's terms, may be blocked or flagged, and xAI terms were **not** reviewed. The UI requires an explicit "I understand" once per subject (`xai_grok_consent_accepted`) before Connect.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| Discovery | `GET https://auth.x.ai/.well-known/openid-configuration`; endpoints must be `https` on `x.ai` / `*.x.ai` |
| Flow | RFC 8628 device code. `client_id=b1a00492-073a-47ea-816f-4c329264a828`, scope `openid profile email offline_access grok-cli:access api:access` |
| Polling | interval ≥ 5 s (server value floored), `slow_down` adds 5 s, bounded by `expires_in` / 30 min; UI polls with `Task.Delay`, never blocking the circuit |
| Refresh | within 5 min of expiry, single-flight per credential; the rotated tokens are **persisted before** the new access token is used; a stored `token_endpoint` is host-checked again |
| Inference base | `https://cli-chat-proxy.grok.com/v1` (code-owned; the stored `BaseUrl` is ignored), API mode Responses |
| Provider `ApiKey` | opaque handle `dyson-xai:<credential-guid>` (not a secret) |

### Where tokens live

One **plaintext** JSON row per credential in `app_settings`, key `xai_grok_credential:<guid>` (shape of CLIProxy's `xai-*.json`, so it imports 1:1). Same plaintext-local stance as provider `ApiKey` and `file_storage_s3`; no encryption layer, no EF migration. Tokens, credential JSON and `Authorization` headers are never logged or echoed in error text. Sign out deletes the row.

### Request path

`XaiGrokRequestHandler` (a `DelegatingHandler` on the default `IHttpClientFactory` client) only acts on `Authorization: Bearer dyson-xai:<guid>`; all other traffic passes through. It (1) refuses any host but `cli-chat-proxy.grok.com` over https, (2) swaps the handle for the real token, (3) stamps the header set below, (4) on 401, or 403 with a `bad-credentials` body, force-refreshes once and resends, (5) rewrites 426 and `free-usage-exhausted` 429 into readable errors. Token-resolution failures become synthetic 401 responses (this handler is the one non-`Result` seam, by framework contract).

### Header set (`XaiGrokClientProfile`)

| Header | Value |
| ------ | ----- |
| `Authorization` | `Bearer <access token>` |
| `X-XAI-Token-Auth` | `xai-grok-cli` |
| `x-grok-client-version` | `1.0.13` (default; see override) |
| `User-Agent` | `xai-grok-workspace/<same version>` |
| `x-grok-client-identifier` | `grok-shell` |
| `x-authenticateresponse` | `authenticate-response` |
| `x-grok-conv-id` | body `prompt_cache_key` (session-scoped) |
| `Accept` | `text/event-stream` (body `stream:true`) else `application/json` |
| `Connection` | `Keep-Alive` |

**Client version / 426.** xAI answers `426 Upgrade Required` below its minimum client version (CLIProxy's hardcoded `0.2.120` hit this; `>= 1.0.13` is required at research time). Bump `XaiGrokClientProfile.DefaultClientVersion` (both version headers derive from it) or override without a rebuild via config `Dyson:XaiGrok:ClientVersion` (env `Dyson__XaiGrok__ClientVersion`); `Dyson:XaiGrok:ExtraHeaders` adds/overrides headers (never `Authorization`). The card shows the resolved version read-only, and the 426 error names the sent version and the override key.

## Model slugs

Verify tries live `GET {base}/models` through the handler and falls back to the bundled table (`XaiGrokModelCatalog`, snapshot of CLIProxy's `xai` registry; status line says "live list" / "bundled list"). `grok-imagine-*` (image/video) ids are never offered as chat slugs. Slugs carry the model's real `ReasoningModes`; default effort is `high` when offered, else the middle level, else none.

## Thinking / effort

Wire (Responses API): nested `reasoning.effort`. Dyson's default modes (`none … xhigh`) are wrong for most Grok models, so `XaiResponsesRequestSanitizer` clamps the effort to the model's levels (`minimal`→`low`, unsupported `none`/models without levels → omitted; unknown models untouched).

## Harness notes

| Topic | Behaviour |
| ----- | --------- |
| Tool loop | Stateless like other managed providers: `store:false`, no `previous_response_id`, full local reasoning replay |
| Body shaping (xAI only) | drops `previous_response_id`, `prompt_cache_retention`, `safety_identifier`, `stream_options`, `stop`; keeps `prompt_cache_key`; ≤ 200 tools; null `content`/invalid `encrypted_content` stripped from reasoning items (foreign blobs from a provider switch: empty, padded, whitespace, `gAAAA…`, non-base64, too short, > 8 MiB); invalid `compaction` items dropped; adjacent summary-only reasoning items merged |
| Streaming | If `response.completed.output` is empty, output is rebuilt from `response.output_item.done` (port of `xaiPatchCompletedOutput`) |
| Attachments | `/files` uploads are skipped (data-URL path) |
| Summarizers / web-search summaries | Still POST `/chat/completions` on the session provider; whether the chat-proxy serves it is **unknown**, they degrade to their existing fallback on error |
| Not ported | images/video, hosted tools, websocket transport, `/responses/compact`, `using_api` key path |

## Switching from CLIProxy

**Switch to native Grok** on the legacy card converts the row in place (`IDysonModelRepository.ConvertManagedSourceAsync`: same provider/slug ids, enabled flags, defaults, efforts and favorites survive), tries to adopt the newest `external/cliproxy/auths/xai-*.json` login (one native refresh to validate), else asks you to Connect. Slugs native no longer offers (for example leaked `grok-imagine-*`) disappear on the next Verify. If xAI rotates refresh tokens, refreshing natively leaves CLIProxy's copy unusable. The CLIProxy auth file is never deleted.

## Gotchas

- `429 free-usage-exhausted` is treated as non-transient (no auto-retry); other 429s still retry.
- Anything beyond headers (TLS/JA3, HTTP/2 settings, header order, attestation) may still get Dyson blocked and cannot be tested offline.
- Device flows are pending in memory only; restarting Dyson mid-sign-in requires Connect again.
- Tool schemas with large top-level `oneOf`/`$ref` made xAI accept but never stream in CLIProxy's experience; not guarded here.

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) (`internal/auth/xai`, `internal/runtime/executor/xai_executor*.go`, `internal/registry/models/models.json`)
- Twin: [chatgpt-codex.md](chatgpt-codex.md)
- Storage: [models.md](../storage/models.md)#managed-providers-cliproxy
