# Inference providers

Per-provider reference for model slugs, auth/base URLs, and thinking/effort parameters for specific integrations. This includes direct API-key managed providers and local CLIProxy-backed providers; the two lifecycles are distinct.

Research date: **2026-07-27** (slugs and effort enums rot quickly; catalogs may drift).

## Providers

| Provider | Page |
| -------- | ---- |
| OpenRouter | [openrouter.md](openrouter.md) |
| OrcaRouter | [orcarouter.md](orcarouter.md) |
| Ollama | [ollama.md](ollama.md) |
| Kimi Code | [kimi-code.md](kimi-code.md) |
| Kimi (CLIProxy) | [kimi-cliproxy.md](kimi-cliproxy.md) |
| MiniMax Token Plan | [minimax-token-plan.md](minimax-token-plan.md) |
| Qwen Coding Plan | [qwen-coding-plan.md](qwen-coding-plan.md) |
| ChatGPT Codex | [chatgpt-codex.md](chatgpt-codex.md) |
| Grok Build | [grok-build.md](grok-build.md) |
| Antigravity | [antigravity.md](antigravity.md) |
| Claude Code | [claude-code.md](claude-code.md) |
| OpenCode Zen / Go | [opencode-zen-go.md](opencode-zen-go.md) |

## Managed provider paths

### Direct managed (API key)

[OpenRouter](openrouter.md) and [OrcaRouter](orcarouter.md) are direct managed providers (`ManagedSource=openrouter` / `orcarouter`). Dyson stores the user-supplied Bearer API key on the provider row, calls the provider’s Completions base URL directly, and lets the user browse the live catalog and enable only selected models. Unchecking a model or using card **Remove** deletes the slug row. These paths do not use CLIProxy, loopback port 8317, OAuth Connect/Verify, or a local managed binary.

### Managed CLIProxy providers

Dyson can import **ChatGPT Codex**, **Grok Build**, **Antigravity**, **Kimi**, and **Claude Code** as managed rows (`ManagedSource` = `cliproxy-codex` / `cliproxy-grok` / `cliproxy-antigravity` / `cliproxy-kimi` / `cliproxy-claude`) that talk to a pinned local [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) process:

- Binary pin: `DysonThirdPartyResources.CliProxyApi.ReleaseTagUrl` (currently `v7.2.145`); unpacked under `{AppContext.BaseDirectory}/external/cliproxy/{version}/`
- Host: `DysonCliProxyHost` — `IsInstalled`, lazy `EnsureInstalledAsync` (streamed download progress), `EnsureRunningAsync` (writes `config.yaml` + `keys.json`, supervises process), `RestartAsync` (restart-only), `ReinstallAndRestartAsync` (re-download pin, prune leftover version dirs; keeps `auths/`, `config.yaml`, `keys.json`)
- Secrets: client + management keys are stable shared plaintext constants on `DysonCliProxyHost` (`DefaultApiKey` / `DefaultManagementKey`), not per-install random `keys.json`. Loopback-only (`127.0.0.1`); every Dyson build can attach to one local CLIProxy. Sidecar `keys.json` is a mirror.
- Auth: Management API OAuth (`BeginConnection` / `CompleteConnection` / `VerifyConnection` on the `Managed*InferenceProvider` subclasses in the catalog)
- Inference: unchanged OpenAI-compatible session path — `BaseUrl=http://127.0.0.1:8317/v1`, `OpenAiApiMode=Responses` (including Claude — proxy exposes OpenAI `/v1/responses`, not Anthropic Messages). Explicit `prompt_cache_options` are omitted (CLIProxy rejects them); `prompt_cache_key` + stable transcript ordering still apply.
- UI: Settings → Models **Third-party managed providers** section (Import / Connect / Verify); session resolve calls `EnsureRunningAsync` only when the selected slug’s `ManagedSource` is a `cliproxy-` source

**Skipped in v7.2.145:** Qwen Code and Z.ai/iFlow — pinned CLIProxy has no `*-auth-url` management OAuth for those providers. Anthropic Messages dialect / `ManagedEndpointKind.AnthropicCompatible` session work remains reserved and not shipped.

## Harness mapping

Today: `ProviderKind=OpenAICompatible` with per-provider `BaseUrl` / `ApiKey` / `OpenAiApiMode`, and per-slug `Slug` + freeform `DefaultReasoningEffort` / `ReasoningModes` ([storage/models.md](../storage/models.md)). Non-empty effort → Completions top-level `"reasoning_effort"`, except OpenRouter Completions uses nested `"reasoning": { "effort": "…" }` (OrcaRouter stays on top-level `reasoning_effort`); Responses also uses nested `reasoning.effort` ([engine/README.md](../engine/README.md)). Blank/null omits the field.

**Works today** with that path:

| Provider | Fit |
| -------- | --- |
| [OpenRouter](openrouter.md) | Direct managed API-key provider; Completions with nested `reasoning.effort`; live text-model discovery; uncheck/card Remove deletes the slug |
| [OrcaRouter](orcarouter.md) | Direct managed API-key provider; Completions with top-level `reasoning_effort`; live catalog browse; uncheck/card Remove deletes the slug |
| [Ollama](ollama.md) | Completions + top-level `reasoning_effort` (`high` / `medium` / `low` / `max` / `none`); Responses mode would send nested `reasoning.effort` |
| [Kimi Code](kimi-code.md) | Completions + K3 `low` / `high` / `max`; omit effort for K2.7 Code |
| [Kimi (CLIProxy)](kimi-cliproxy.md) | Subscription via CLIProxy managed provider (`cliproxy-kimi`); local Responses |
| [ChatGPT Codex](chatgpt-codex.md) | Platform API key + Responses at `api.openai.com/v1`; **or** subscription via CLIProxy managed provider (nested `reasoning.effort`) |
| [Grok Build](grok-build.md) | Subscription via CLIProxy managed provider (`cliproxy-grok`); local Responses |
| [Antigravity](antigravity.md) | Subscription via CLIProxy managed provider (`cliproxy-antigravity`); local Responses |
| [Claude Code](claude-code.md) | Subscription via CLIProxy managed provider (`cliproxy-claude`); OpenAI/Responses via proxy (not Anthropic Messages) |
| [OpenCode Zen / Go](opencode-zen-go.md) | Zen/Go OpenAI Completions (top-level) or Responses (nested effort) bases |
| [Qwen Coding Plan](qwen-coding-plan.md) | Completions `reasoning_effort` for GLM/DeepSeek allowlist slugs; Responses nested `reasoning.effort` when `OpenAiApiMode=Responses` |
| [MiniMax Token Plan](minimax-token-plan.md) | Auth/base/slug + Responses nested `reasoning.effort` when Responses mode; Completions top-level `reasoning_effort` does **not** drive MiniMax thinking |

**Needs extra wire / auth later** (real upstream contracts on each page; not wired yet):

- `thinking` / `thinking.type` — MiniMax Completions; Anthropic dialect paths
- `enable_thinking` (+ `thinking_budget`) — Qwen Completions
- Anthropic Messages dialect — Kimi, MiniMax, Qwen, OpenCode
- Direct Codex OAuth + `chatgpt.com/backend-api/codex` — prefer CLIProxy managed path instead
- OpenCode multi-dialect — Anthropic `x-api-key`, Google `x-goog-api-key` / `thinkingConfig`
