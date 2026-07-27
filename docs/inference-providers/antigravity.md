# Antigravity (CLIProxy)

Google / Gemini models via Antigravity OAuth mediated by local CLIProxyAPI. Model catalogs rotate; prefer live `/v1/models` after Verify.

Research date: **2026-07-28**.

## Product

Antigravity is Google’s coding-oriented Gemini surface exposed through CLIProxy. In Dyson it ships only as a **managed CLIProxy** provider (OpenAI/Responses dialect via the proxy — no Google native client).

| Path | Billing | Typical use |
| ---- | ------- | ----------- |
| Antigravity sign-in via CLIProxy | Subscription / plan credits | Settings → Models → Import **Antigravity (CLIProxy)** |

## Auth & base URL

Managed import (`ManagedSource=cliproxy-antigravity`):

| Item | Value |
| ---- | ----- |
| Local inference base | `http://127.0.0.1:8317/v1` |
| Auth | CLIProxy Management API `antigravity-auth-url?is_webui=true` + `get-auth-status`; session Bearer = local proxy API key |
| OAuth callback port | `51121` (web-UI forwarder; Connect preflight bind-checks this port) |
| API mode | Responses (`OpenAiApiMode=Responses`) |

See [inference-providers README](README.md)#managed-cliproxy-providers for binary pin, host lifecycle, and `EnsureRunningAsync` on session resolve.

## Model slugs

Discover at runtime via **Verify** → `GET /v1/models` (filtered by owned_by / type tokens `antigravity`, `google`, `gemini`). Do not hardcode slug tables here.

## Thinking / effort

Wire (Responses API): nested `reasoning.effort` — same Dyson Responses client as Codex/OpenAI.

| Parameter | Notes |
| --------- | ----- |
| `reasoning.effort` | Freeform slug `DefaultReasoningEffort` / `ReasoningModes`; blank/null omits |
| Default managed modes | `none`, `minimal`, `low`, `medium`, `high`, `xhigh` (`ManagedInferenceProviderBase.DefaultReasoningModes`) |

## Harness notes

1. **Managed only:** Settings → Models → Import **Antigravity (CLIProxy)** (`ManagedSource=cliproxy-antigravity`). Local CLIProxyAPI handles OAuth; Dyson sessions use OpenAI-compatible Responses against `http://127.0.0.1:8317/v1`.
2. Connect uses management `antigravity-auth-url?is_webui=true` with localhost:**51121** forwarder preflight (same shape as Codex port 1455).

| Dyson field | Antigravity mapping |
| ----------- | ------------------- |
| `Slug` | Id from CLIProxy `/v1/models` after Verify |
| `DefaultReasoningEffort` / `ReasoningModes` | Freeform; Responses sends nested `reasoning.effort` |
| Nested `reasoning.effort` | Wired on Responses |
| `prompt_cache_key` | Always sent (session-scoped) |
| Responses tool-loop (CLIProxy managed) | Stateless: `store: false`, no `previous_response_id`; full local `reasoning` → `function_call` → `function_call_output` replay |
| `prompt_cache_options` / explicit breakpoints | **Omitted** for CLIProxy managed |
| Direct Google / Gemini OAuth | **not wired** — prefer CLIProxy managed path |

## Gotchas

- Managed rows are view-only except Enable/Disable per slug + Default; manual edit of `BaseUrl` / `ApiKey` is rejected while `ManagedSource` is set.
- Explicit `prompt_cache_options` are rejected by CLIProxy — Dyson omits them for all managed sources.
- Slug lists rot; re-Verify after upstream catalog changes.
- Disconnect in the UI clears pending auth-session tracking only — it does not delete the managed row or stop the proxy.
- Connect fails visibly if port **51121** is already bound.

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)
- Managed path twins: [chatgpt-codex.md](chatgpt-codex.md), [grok-build.md](grok-build.md)
- Storage: [models.md](../storage/models.md)#managed-providers-cliproxy
