# Meta Muse (CLIProxy)

Meta Muse Code via device-code OAuth mediated by local CLIProxyAPI. Model catalogs rotate; prefer live `/v1/models` after Verify.

Research date: **2026-09-24**.

## Product

Muse Code is Meta’s coding surface (`api.meta.ai`). In Dyson it ships only as a **managed CLIProxy** provider. Upstream’s provider key is `meta` (not `muse`), so the managed source is `cliproxy-meta`.

| Path | Billing | Typical use |
| ---- | ------- | ----------- |
| Meta device-code sign-in via CLIProxy | Subscription / plan credits | Settings → Models → Import **Meta Muse (CLIProxy)** |

## Auth & base URL

Managed import (`ManagedSource=cliproxy-meta`):

| Item | Value |
| ---- | ----- |
| Local inference base | `http://127.0.0.1:8317/v1` |
| Auth | CLIProxy Management API `meta-auth-url` + `get-auth-status`; session Bearer = local proxy API key |
| Flow | Device code (`flow=device`, `user_code`, `expires_in`). No localhost callback port and no `is_webui` forwarder |
| API mode | Responses (`OpenAiApiMode=Responses`) |

See [inference-providers README](README.md)#managed-cliproxy-providers for binary pin, host lifecycle, and `EnsureRunningAsync` on session resolve.

CLIProxyAPI **v7.3.15** already registers `GET /v0/management/meta-auth-url` (device flow added in `54d4f4c`, an ancestor of that tag). Dyson stays on that pin; v7.3.16 only persists mint subscription metadata and is not required.

## Model slugs

Discover at runtime via **Verify** → `GET /v1/models` (filtered by owned_by / type / id tokens `meta`, `muse`). Do not hardcode slug tables here.

At the v7.3.15 pin the registry’s `meta` channel includes `muse-spark-1.3`, `muse-spark-1.3-contributor`, `muse-spark-1.2`, `muse-spark-1.2-contributor`, and `muse-spark-1.1` (`owned_by` / `type` = `meta`). Re-Verify after a pin bump; the list rots.

## Thinking / effort

Wire (Responses API): nested `reasoning.effort` — same Dyson Responses client as Codex/OpenAI. CLIProxy’s Meta executor speaks the Codex/Responses dialect upstream.

| Parameter | Notes |
| --------- | ----- |
| `reasoning.effort` | Freeform slug `DefaultReasoningEffort` / `ReasoningModes`; blank/null omits |
| Default managed modes | `none`, `minimal`, `low`, `medium`, `high`, `xhigh` (`ManagedInferenceProviderBase.DefaultReasoningModes`) |

Upstream’s Muse Spark registry also lists `max` on some entries. Dyson does not special-case a Muse mode list (same freeform default as Grok).

## Harness notes

1. **Managed only:** Settings → Models → Import **Meta Muse (CLIProxy)** (`ManagedSource=cliproxy-meta`). Local CLIProxyAPI handles the device flow; Dyson sessions use OpenAI-compatible Responses against `http://127.0.0.1:8317/v1`.
2. Connect uses management `meta-auth-url` (no Codex-style localhost forwarder preflight). The provider card shows `user_code` when CLIProxy returns one.

| Dyson field | Meta Muse mapping |
| ----------- | ----------------- |
| `Slug` | Id from CLIProxy `/v1/models` after Verify |
| `DefaultReasoningEffort` / `ReasoningModes` | Freeform; Responses sends nested `reasoning.effort` |
| Nested `reasoning.effort` | Wired on Responses |
| `prompt_cache_key` | Always sent (session-scoped) |
| Responses tool-loop (CLIProxy managed) | Stateless: `store: false`, no `previous_response_id`; full local `reasoning` → `function_call` → `function_call_output` replay (same as Codex managed) |
| `prompt_cache_options` / explicit breakpoints | **Omitted** for CLIProxy managed (`ManagedSource=cliproxy-meta`) |
| Direct Meta OAuth / non-proxy base | **not wired** — prefer CLIProxy managed path |

## Gotchas

- Managed rows are view-only except Enable/Disable per slug + Default; manual edit of `BaseUrl` / `ApiKey` is rejected while `ManagedSource` is set.
- Explicit `prompt_cache_options` are rejected by CLIProxy — Dyson omits them for all managed sources.
- Slug lists rot; re-Verify after upstream catalog changes.
- Disconnect in the UI clears pending auth-session tracking only — it does not delete the managed row or stop the proxy.
- Login is a device code, not a localhost redirect. Finish it in the browser CLIProxy opens, then click Complete.

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) `v7.3.15` — `GET /v0/management/meta-auth-url`, `internal/auth/meta` device flow (`auth.meta.com` OIDC device authorization, then mint `https://api.meta.ai/muse-code/key`)
- Managed path twin: [grok-build.md](grok-build.md)
- Storage: [models.md](../storage/models.md)#managed-providers
