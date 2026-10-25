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
| Meta Muse | [meta-muse.md](meta-muse.md) |
| Antigravity | [antigravity.md](antigravity.md) |
| Claude Code | [claude-code.md](claude-code.md) |
| OpenCode Zen / Go | [opencode-zen-go.md](opencode-zen-go.md) |

## Managed provider paths

### Direct managed (API key)

[OpenRouter](openrouter.md) and [OrcaRouter](orcarouter.md) are direct managed providers (`ManagedSource=openrouter` / `orcarouter`). Dyson stores the user-supplied Bearer API key on the provider row, calls the provider’s Completions base URL directly, and lets the user browse the live catalog and enable only selected models. Unchecking a model or using card **Remove** deletes the slug row. These paths do not use CLIProxy, loopback port 8317, OAuth Connect/Verify, or a local managed binary. Completions rebuilds the transcript on every hop with a stable prefix and `prompt_cache_key`. They do not get Anthropic `cache_control`, the Claude cache-anchor line, or `prompt_cache_options` (`ManagedSource` is set). Session resolve does not call `EnsureRunningAsync` for these rows.

### Native xAI / Grok (no CLIProxy)

**Grok Build (xAI)** (`ManagedSource=xai-grok`) signs in with xAI's OIDC device-code flow and calls `https://cli-chat-proxy.grok.com/v1` directly (Responses). The provider `ApiKey` is a `dyson-xai:<guid>` handle that `XaiGrokRequestHandler` swaps for the real token (stored plaintext in `app_settings`, key `xai_grok_credential:<guid>`). No binary download, process, port 8317 or management key. Full Responses replay keeps a stable prefix and `prompt_cache_key`, which the handler copies to `x-grok-conv-id`. There is no Anthropic `cache_control` and no Claude cache-anchor line. `prompt_cache_options` stays omitted because `ManagedSource` is set. The Grok sanitizer is unchanged. Session resolve does not call `EnsureRunningAsync`. Details, header set, `Dyson:XaiGrok:ClientVersion` override and gotchas: [grok-build.md](grok-build.md). The legacy CLIProxy Grok row (`cliproxy-grok`) is deprecated; its card offers **Switch to native Grok**. That legacy row is a `cliproxy-*` source: full replay, `prompt_cache_key`, no `cache_control`, and it does start with the CLIProxy off switch.

### Managed CLIProxy providers

Dyson can import **ChatGPT Codex**, **Grok Build (legacy)**, **Antigravity**, **Kimi**, **Claude Code**, and **Meta Muse** as managed rows (`ManagedSource` = `cliproxy-codex` / `cliproxy-grok` / `cliproxy-antigravity` / `cliproxy-kimi` / `cliproxy-claude` / `cliproxy-meta`) that talk to a pinned local [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) process:

- Binary pin: `DysonThirdPartyResources.CliProxyApi.ReleaseTagUrl` (currently `v7.3.15`); unpacked under `{AppContext.BaseDirectory}/external/cliproxy/{version}/`
- Host: `DysonCliProxyHost` — `IsInstalled`, lazy `EnsureInstalledAsync` (streamed download progress), `EnsureRunningAsync` (writes `config.yaml` + `keys.json`, supervises process), `RestartAsync` (restart-only), `ReinstallAndRestartAsync` (re-download pin, prune leftover version dirs; keeps `auths/`, `config.yaml`, `keys.json`, and the off-switch file). An install-global flag file named `disabled` at `DysonCliProxyPaths.InstallRoot` (`{AppContext.BaseDirectory}/external/cliproxy/disabled`) turns the process off. Absent means on. When the file is present, `EnsureRunningAsync` does not start, kills a running `cli-proxy-api` for that install, and returns `CLIProxy is turned off. Turn it on in Settings → Models.` Import, Connect, and Verify go through that method, so they stop with it. Settings → Models writes the file on **Turn CLI proxy off** (and stops the process) and deletes it on **Turn CLI proxy on** (does not start). Reset and Reinstall stay disabled while the switch is off; the host methods also refuse to start while the file exists.
- Secrets: client + management keys are stable shared plaintext constants on `DysonCliProxyHost` (`DefaultApiKey` / `DefaultManagementKey`), not per-install random `keys.json`. Loopback-only (`127.0.0.1`); every Dyson build can attach to one local CLIProxy. Sidecar `keys.json` is a mirror.
- Auth: Management API OAuth (`BeginConnection` / `CompleteConnection` / `VerifyConnection` on the `Managed*InferenceProvider` subclasses in the catalog)
- Inference: unchanged OpenAI-compatible session path — `BaseUrl=http://127.0.0.1:8317/v1`, `OpenAiApiMode=Responses` (including Claude — proxy exposes OpenAI `/v1/responses`, not Anthropic Messages). Every `cliproxy-*` tool hop is a full local replay with a stable prefix and `prompt_cache_key`. `prompt_cache_options` stays omitted (`ManagedSource` is set). Anthropic `cache_control` (`type: ephemeral`) and the builder-only cache-anchor user line are sent only for `cliproxy-claude` ([claude-code.md](claude-code.md)). Codex, Kimi, Antigravity, Meta Muse, and legacy `cliproxy-grok` keep the prefix and `prompt_cache_key` and do not get those markers.
- UI: Settings → Models **Third-party managed providers** section (Import / Connect / Verify) plus the toolbar off switch. Session resolve calls `EnsureRunningAsync` only when the selected slug’s `ManagedSource` is a `cliproxy-` source. Native Grok, OpenRouter, OrcaRouter, and other custom base URLs do not.

**Skipped in v7.3.15:** Qwen Code and Z.ai/iFlow — pinned CLIProxy has no `*-auth-url` management OAuth for those providers. Anthropic Messages dialect / `ManagedEndpointKind.AnthropicCompatible` session work remains reserved and not shipped.

## Custom OpenAI-compatible endpoints

A user-added provider (`ManagedSource` null, `ProviderKind=OpenAICompatible`) refreshes with one `GET {normalized base}/models` (`OpenAiCompatibleModelDiscovery`). `NormalizeBaseUrl` keeps a trailing `/vN` and otherwise appends `/v1`, so a self-hosted CLIProxyAPI (`http://127.0.0.1:8317`), LM Studio (`http://127.0.0.1:1234`), and `https://api.openai.com` all hit `/v1/models`. The `Authorization: Bearer` header is sent only when an API key is set. There is no owner-token filter, no query string, and no second URL. New slugs insert disabled; the user enables the ones they want. Hand-typed slugs the server does not return are removed. This is not CLIProxy **Verify** (that starts Dyson's managed proxy and filters by owner) and not OpenRouter / OrcaRouter **Browse**. Session resolve does not call `EnsureRunningAsync`. Completions full-replays with a stable prefix and `prompt_cache_key`, and does not send `cache_control` or the Claude anchor. A custom Responses row still uses store chaining (`store: true` + `previous_response_id`). A GPT-5.6-shaped slug with `ManagedSource` null still sends `prompt_cache_options.mode=explicit` on Completions and on Responses. A custom base URL of `http://127.0.0.1:8317` is the same local CLIProxy process the Settings off switch stops; it is not a `cliproxy-*` source, so resolve will not start it.

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
| [Grok Build](grok-build.md) | Native xAI device-code sign-in (`xai-grok`, Responses at `cli-chat-proxy.grok.com`); legacy CLIProxy row (`cliproxy-grok`) deprecated |
| [Meta Muse](meta-muse.md) | Subscription via CLIProxy managed provider (`cliproxy-meta`); device-code `meta-auth-url`; local Responses |
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
