# Claude Code (CLIProxy)

Anthropic Claude via Claude Code OAuth mediated by local CLIProxyAPI. Exposed to Dyson as **OpenAI/Responses** through the proxy — not Anthropic Messages dialect. Model catalogs rotate; prefer live `/v1/models` after Verify.

Research date: **2026-07-28**.

## Product

Claude Code subscription surface via CLIProxy. In Dyson it ships only as a **managed CLIProxy** provider (`ProviderKind=OpenAICompatible`, `OpenAiApiMode=Responses`). There is no Anthropic Messages session provider in this pass.

| Path | Billing | Typical use |
| ---- | ------- | ----------- |
| Claude Code sign-in via CLIProxy | Subscription / plan credits | Settings → Models → Import **Claude Code (CLIProxy)** |

## Auth & base URL

Managed import (`ManagedSource=cliproxy-claude`):

| Item | Value |
| ---- | ----- |
| Local inference base | `http://127.0.0.1:8317/v1` |
| Auth | CLIProxy Management API `anthropic-auth-url?is_webui=true` + `get-auth-status`; session Bearer = local proxy API key |
| OAuth callback port | `54545` (web-UI forwarder; Connect preflight bind-checks this port) |
| API mode | Responses (`OpenAiApiMode=Responses`) — OpenAI-compatible path through the proxy |

See [inference-providers README](README.md)#managed-cliproxy-providers for binary pin, host lifecycle, and `EnsureRunningAsync` on session resolve.

## Model slugs

Discover at runtime via **Verify** → `GET /v1/models` (filtered by owned_by / type tokens `claude`, `anthropic`). Do not hardcode slug tables here.

## Thinking / effort

Wire (Responses API): nested `reasoning.effort` — same Dyson Responses client as Codex/OpenAI.

| Parameter | Notes |
| --------- | ----- |
| `reasoning.effort` | Freeform slug `DefaultReasoningEffort` / `ReasoningModes`; blank/null omits |
| Default managed modes | `none`, `minimal`, `low`, `medium`, `high`, `xhigh` (`ManagedInferenceProviderBase.DefaultReasoningModes`) |

## Harness notes

1. **Managed only:** Settings → Models → Import **Claude Code (CLIProxy)** (`ManagedSource=cliproxy-claude`). Local CLIProxyAPI handles OAuth; Dyson sessions use OpenAI-compatible Responses against `http://127.0.0.1:8317/v1`.
2. Connect uses management `anthropic-auth-url?is_webui=true` with localhost:**54545** forwarder preflight (same shape as Codex port 1455).
3. Claude models are reached via the proxy’s OpenAI `/v1/chat/completions` and `/v1/responses` surfaces — **not** Anthropic Messages.

| Dyson field | Claude mapping |
| ----------- | -------------- |
| `Slug` | Id from CLIProxy `/v1/models` after Verify |
| `DefaultReasoningEffort` / `ReasoningModes` | Freeform; Responses sends nested `reasoning.effort` |
| Nested `reasoning.effort` | Wired on Responses |
| `prompt_cache_key` | Always sent (session-scoped) |
| Responses tool-loop (CLIProxy managed) | Stateless: `store: false`, no `previous_response_id`; full local `reasoning` → `function_call` → `function_call_output` replay |
| `prompt_cache_options` / explicit breakpoints | **Omitted** for CLIProxy managed |
| Anthropic Messages dialect / `ProviderKind=Anthropic` | **not wired** |

## Gotchas

- Managed rows are view-only except Enable/Disable per slug + Default; manual edit of `BaseUrl` / `ApiKey` is rejected while `ManagedSource` is set.
- Explicit `prompt_cache_options` are rejected by CLIProxy — Dyson omits them for all managed sources.
- Slug lists rot; re-Verify after upstream catalog changes.
- Disconnect in the UI clears pending auth-session tracking only — it does not delete the managed row or stop the proxy.
- Connect fails visibly if port **54545** is already bound.

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)
- Managed path twins: [chatgpt-codex.md](chatgpt-codex.md), [grok-build.md](grok-build.md)
- Storage: [models.md](../storage/models.md)#managed-providers-cliproxy
