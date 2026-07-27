# Inference providers

Per-provider reference for model slugs, auth/base URLs, and thinking/effort parameters for specific integrations. Not a general proxy catalog — **OpenRouter is later**.

Research date: **2026-07-25** (slugs and effort enums rot quickly; catalogs may drift).

## Providers

| Provider | Page |
| -------- | ---- |
| Ollama | [ollama.md](ollama.md) |
| Kimi Code | [kimi-code.md](kimi-code.md) |
| MiniMax Token Plan | [minimax-token-plan.md](minimax-token-plan.md) |
| Qwen Coding Plan | [qwen-coding-plan.md](qwen-coding-plan.md) |
| ChatGPT Codex | [chatgpt-codex.md](chatgpt-codex.md) |
| OpenCode Zen / Go | [opencode-zen-go.md](opencode-zen-go.md) |

## Managed CLIProxy providers

Dyson can import **ChatGPT Codex** and **Grok Build** as managed rows (`ManagedSource` = `cliproxy-codex` / `cliproxy-grok`) that talk to a pinned local [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) process:

- Binary pin: `DysonThirdPartyResources.CliProxyApi.ReleaseTagUrl` (currently `v7.2.102`); unpacked under `{AppContext.BaseDirectory}/external/cliproxy/{version}/`
- Host: `DysonCliProxyHost` — `IsInstalled`, lazy `EnsureInstalledAsync` (streamed download progress), `EnsureRunningAsync` (writes `config.yaml` + `keys.json`, supervises process)
- Auth: Management API OAuth (`BeginConnection` / `CompleteConnection` / `VerifyConnection` on `ManagedCodexInferenceProvider` / `ManagedGrokInferenceProvider`)
- Inference: unchanged OpenAI-compatible session path — `BaseUrl=http://127.0.0.1:8317/v1`, `OpenAiApiMode=Responses`. Explicit `prompt_cache_options` are omitted (CLIProxy rejects them); `prompt_cache_key` + stable transcript ordering still apply.
- UI: Settings → Models **Third-party managed providers** section (Import / Connect / Verify); session resolve calls `EnsureRunningAsync` when the selected slug’s provider has `ManagedSource`

Anthropic/Claude managed import is reserved on `ManagedEndpointKind` but not shipped in this pass.

## Harness mapping

Today: `ProviderKind=OpenAICompatible` with per-provider `BaseUrl` / `ApiKey` / `OpenAiApiMode`, and per-slug `Slug` + freeform `DefaultReasoningEffort` / `ReasoningModes` ([storage/models.md](../storage/models.md)). Non-empty effort → top-level `"reasoning_effort"` on Completions and Responses ([engine/README.md](../engine/README.md)); blank/null omits the field.

**Works today** with that path:

| Provider | Fit |
| -------- | --- |
| [Ollama](ollama.md) | Completions + `reasoning_effort` (`high` / `medium` / `low` / `max` / `none`) |
| [Kimi Code](kimi-code.md) | Completions + K3 `low` / `high` / `max`; omit effort for K2.7 Code |
| [ChatGPT Codex](chatgpt-codex.md) | Platform API key + Responses at `api.openai.com/v1`; **or** subscription via CLIProxy managed provider |
| [OpenCode Zen / Go](opencode-zen-go.md) | Zen/Go OpenAI Completions or Responses bases only |
| [Qwen Coding Plan](qwen-coding-plan.md) | Completions `reasoning_effort` for GLM/DeepSeek allowlist slugs only |
| [MiniMax Token Plan](minimax-token-plan.md) | Auth/base/slug only — top-level `reasoning_effort` does **not** drive MiniMax thinking |

**Needs extra wire / auth later** (real upstream contracts on each page; not wired yet):

- `thinking` / `thinking.type` — MiniMax; Anthropic dialect paths
- `enable_thinking` (+ `thinking_budget`) — Qwen Completions
- Nested `reasoning.effort` — Ollama, MiniMax/Qwen/Codex/OpenCode Responses
- Anthropic Messages dialect — Kimi, MiniMax, Qwen, OpenCode
- Direct Codex OAuth + `chatgpt.com/backend-api/codex` — prefer CLIProxy managed path instead
- OpenCode multi-dialect — Anthropic `x-api-key`, Google `x-goog-api-key` / `thinkingConfig`
