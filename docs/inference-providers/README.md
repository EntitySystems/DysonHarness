# Inference providers

Per-provider reference for model slugs, auth/base URLs, and thinking/effort parameters for specific integrations. Not a general proxy catalog — **OpenRouter is later**.

Research date: **2026-07-27** (slugs and effort enums rot quickly; catalogs may drift).

## Providers

| Provider | Page |
| -------- | ---- |
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

## Managed CLIProxy providers

Dyson can import **ChatGPT Codex**, **Grok Build**, **Antigravity**, **Kimi**, and **Claude Code** as managed rows (`ManagedSource` = `cliproxy-codex` / `cliproxy-grok` / `cliproxy-antigravity` / `cliproxy-kimi` / `cliproxy-claude`) that talk to a pinned local [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) process:

- Binary pin: `DysonThirdPartyResources.CliProxyApi.ReleaseTagUrl` (currently `v7.2.102`); unpacked under `{AppContext.BaseDirectory}/external/cliproxy/{version}/`
- Host: `DysonCliProxyHost` — `IsInstalled`, lazy `EnsureInstalledAsync` (streamed download progress), `EnsureRunningAsync` (writes `config.yaml` + `keys.json`, supervises process)
- Auth: Management API OAuth (`BeginConnection` / `CompleteConnection` / `VerifyConnection` on the `Managed*InferenceProvider` subclasses in the catalog)
- Inference: unchanged OpenAI-compatible session path — `BaseUrl=http://127.0.0.1:8317/v1`, `OpenAiApiMode=Responses` (including Claude — proxy exposes OpenAI `/v1/responses`, not Anthropic Messages). Explicit `prompt_cache_options` are omitted (CLIProxy rejects them); `prompt_cache_key` + stable transcript ordering still apply.
- UI: Settings → Models **Third-party managed providers** section (Import / Connect / Verify); session resolve calls `EnsureRunningAsync` when the selected slug’s provider has `ManagedSource`

**Skipped in v7.2.102:** Qwen Code and Z.ai/iFlow — pinned CLIProxy has no `*-auth-url` management OAuth for those providers. Anthropic Messages dialect / `ManagedEndpointKind.AnthropicCompatible` session work remains reserved and not shipped.

## Harness mapping

Today: `ProviderKind=OpenAICompatible` with per-provider `BaseUrl` / `ApiKey` / `OpenAiApiMode`, and per-slug `Slug` + freeform `DefaultReasoningEffort` / `ReasoningModes` ([storage/models.md](../storage/models.md)). Non-empty effort → Completions top-level `"reasoning_effort"`; Responses nested `"reasoning": { "effort": "…" }` ([engine/README.md](../engine/README.md)); blank/null omits the field.

**Works today** with that path:

| Provider | Fit |
| -------- | --- |
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
