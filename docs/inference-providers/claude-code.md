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

## Accounts

Up to **8** Claude credentials. OAuth files under install-global `external/cliproxy/auths` and existing `claude-api-key` entries share that cap (no create-key form; a key already on disk still counts and can be selected). The check runs in `PreflightBeginConnectionAsync` before the browser opens. At 8 the existing Models error banner shows: `Claude accounts are limited to 8. Remove one before adding another.`

Exactly one account is active: the credential with `disabled: false`. Dyson does not change `routing.strategy` and does not route requests itself. **Select**, and a new login, PATCH `/v0/management/auth-files/status` (`{"name","disabled"}`): enable the chosen name first, then disable every other Claude credential. A new login becomes the active account. That pin is `OnConnectionCompletedAsync` inside `CompleteConnectionAsync`, so both the Connect poll and the Complete button pin it.

On the Claude card: **Add account** once any account is listed (**Connect** when the list is empty), **Select**, **Remove**. Remove deletes that one credential (`DELETE /v0/management/auth-files?name=`). **Disconnect** still only clears pending auth-session tracking — it does not delete files, disable credentials, or drop the managed row.

An in-flight reply finishes on the previous account. The next proxy request uses only the enabled one.

Reconcile on card load: several enabled → keep the ordinal-first `name` and disable the rest. None enabled → leave them idle until the user selects.

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
- Claude Fable 5.1 requires CLIProxyAPI ≥ **v7.2.148** (Claude Code fingerprint ≥ 2.1.251). Dyson pins **v7.3.15** (Claude Opus 5.5 / `claude-opus-5-5` is in that release's model registry). Reinstall CLI proxy from Settings → Models after a pin bump; an old `external/cliproxy/7.2.145/` tree is not treated as installed.

## Sources

- [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)
- Managed path twins: [chatgpt-codex.md](chatgpt-codex.md), [grok-build.md](grok-build.md)
- Storage: [models.md](../storage/models.md)#managed-providers-cliproxy
