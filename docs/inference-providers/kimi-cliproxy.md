# Kimi (CLIProxy)

Moonshot / Kimi models via CLIProxy device-code OAuth. Distinct from the direct [Kimi Code](kimi-code.md) API-key path. Model catalogs rotate; prefer live `/v1/models` after Verify.

Research date: **2026-07-28**.

## Product

Kimi via local CLIProxyAPI. In Dyson this path ships only as a **managed CLIProxy** provider (OpenAI/Responses dialect via the proxy).

| Path | Billing | Typical use |
| ---- | ------- | ----------- |
| Kimi sign-in via CLIProxy | Subscription / plan credits | Settings → Models → Import **Kimi (CLIProxy)** |
| Direct Kimi Code API key | Membership quota | Manual OpenAICompatible provider — see [kimi-code.md](kimi-code.md) |

## Auth & base URL

Managed import (`ManagedSource=cliproxy-kimi`):

| Item | Value |
| ---- | ----- |
| Local inference base | `http://127.0.0.1:8317/v1` |
| Auth | CLIProxy Management API `kimi-auth-url` + `get-auth-status`; session Bearer = local proxy API key |
| OAuth callback port | None (device-code flow, like Grok) |
| API mode | Responses (`OpenAiApiMode=Responses`) |

See [inference-providers README](README.md)#managed-cliproxy-providers for binary pin, host lifecycle, and `EnsureRunningAsync` on session resolve.

## Model slugs

Discover at runtime via **Verify** → `GET /v1/models` (filtered by owned_by / type tokens `kimi`, `moonshot`). Do not hardcode slug tables here.

## Thinking / effort

Wire (Responses API): nested `reasoning.effort` — same Dyson Responses client as Codex/OpenAI.

| Parameter | Notes |
| --------- | ----- |
| `reasoning.effort` | Freeform slug `DefaultReasoningEffort` / `ReasoningModes`; blank/null omits |
| Default managed modes | `none`, `minimal`, `low`, `medium`, `high`, `xhigh` (`ManagedInferenceProviderBase.DefaultReasoningModes`) |

## Harness notes

1. **Managed path:** Settings → Models → Import **Kimi (CLIProxy)** (`ManagedSource=cliproxy-kimi`). Local CLIProxyAPI handles OAuth; Dyson sessions use OpenAI-compatible Responses against `http://127.0.0.1:8317/v1`.
2. Connect uses management `kimi-auth-url` (no localhost forwarder preflight).

| Dyson field | Kimi mapping |
| ----------- | ------------ |
| `Slug` | Id from CLIProxy `/v1/models` after Verify |
| `DefaultReasoningEffort` / `ReasoningModes` | Freeform; Responses sends nested `reasoning.effort` |
| Nested `reasoning.effort` | Wired on Responses |
| `prompt_cache_key` | Always sent (session-scoped) |
| Responses tool-loop (CLIProxy managed) | Stateless: `store: false`, no `previous_response_id`; full local `reasoning` → `function_call` → `function_call_output` replay |
| `prompt_cache_options` / explicit breakpoints | **Omitted** for CLIProxy managed |
| Direct Kimi Code API key | Separate manual provider — see [kimi-code.md](kimi-code.md) |

## Gotchas

- Managed rows are view-only except Enable/Disable per slug + Default; manual edit of `BaseUrl` / `ApiKey` is rejected while `ManagedSource` is set.
- Explicit `prompt_cache_options` are rejected by CLIProxy — Dyson omits them for all managed sources.
- Slug lists rot; re-Verify after upstream catalog changes.
- Disconnect in the UI clears pending auth-session tracking only — it does not delete the managed row or stop the proxy.
- Do not mix CLIProxy-synced slug ids with the direct Kimi Code base URL (or vice versa).

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)
- Direct API twin: [kimi-code.md](kimi-code.md)
- Managed path twins: [chatgpt-codex.md](chatgpt-codex.md), [grok-build.md](grok-build.md)
- Storage: [models.md](../storage/models.md)#managed-providers-cliproxy
