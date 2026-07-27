# Grok Build

xAI Grok via ChatGPT-style subscription OAuth mediated by local CLIProxyAPI. Model catalogs rotate; prefer live `/v1/models` after Verify.

Research date: **2026-07-27**.

## Product

Grok Build is xAI’s coding-oriented Grok surface. In Dyson it ships only as a **managed CLIProxy** provider (no first-party direct OAuth against xAI in this pass).

| Path | Billing | Typical use |
| ---- | ------- | ----------- |
| xAI sign-in via CLIProxy | Subscription / plan credits | Settings → Models → Import **Grok Build (CLIProxy)** |

## Auth & base URL

Managed import (`ManagedSource=cliproxy-grok`):

| Item | Value |
| ---- | ----- |
| Local inference base | `http://127.0.0.1:8317/v1` |
| Auth | CLIProxy Management API `xai-auth-url` + `get-auth-status`; session Bearer = local proxy API key |
| API mode | Responses (`OpenAiApiMode=Responses`) |

See [inference-providers README](README.md)#managed-cliproxy-providers for binary pin, host lifecycle, and `EnsureRunningAsync` on session resolve.

## Model slugs

Discover at runtime via **Verify** → `GET /v1/models` (filtered by owned_by / type tokens `xai`, `x-ai`, `grok`). Do not hardcode slug tables here.

## Thinking / effort

Wire (Responses API): nested `reasoning.effort` — same Dyson Responses client as Codex/OpenAI.

| Parameter | Notes |
| --------- | ----- |
| `reasoning.effort` | Freeform slug `DefaultReasoningEffort` / `ReasoningModes`; blank/null omits |
| Default managed modes | `none`, `minimal`, `low`, `medium`, `high`, `xhigh` (`ManagedInferenceProviderBase.DefaultReasoningModes`) |

## Harness notes

1. **Managed only:** Settings → Models → Import **Grok Build (CLIProxy)** (`ManagedSource=cliproxy-grok`). Local CLIProxyAPI handles OAuth; Dyson sessions use OpenAI-compatible Responses against `http://127.0.0.1:8317/v1`.
2. Connect uses management `xai-auth-url` (no Codex-style localhost:1455 forwarder preflight).

| Dyson field | Grok mapping |
| ----------- | ------------ |
| `Slug` | Id from CLIProxy `/v1/models` after Verify |
| `DefaultReasoningEffort` / `ReasoningModes` | Freeform; Responses sends nested `reasoning.effort` |
| Nested `reasoning.effort` | Wired on Responses |
| `prompt_cache_key` | Always sent (session-scoped) |
| `prompt_cache_options` / explicit breakpoints | **Omitted** for CLIProxy managed (`ManagedSource=cliproxy-grok`) |
| `store: true` | Dyson always sends `store: true` on Responses for tool-loop chaining |
| Direct xAI OAuth / non-proxy base | **not wired** — prefer CLIProxy managed path |

## Gotchas

- Managed rows are view-only except Enable/Disable per slug + Default; manual edit of `BaseUrl` / `ApiKey` is rejected while `ManagedSource` is set.
- Explicit `prompt_cache_options` are rejected by CLIProxy — Dyson omits them for all managed sources.
- Slug lists rot; re-Verify after upstream catalog changes.
- Disconnect in the UI clears pending auth-session tracking only — it does not delete the managed row or stop the proxy.

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)
- Managed path twin: [chatgpt-codex.md](chatgpt-codex.md)
- Storage: [models.md](../storage/models.md)#managed-providers-cliproxy
