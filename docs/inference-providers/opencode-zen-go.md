# OpenCode Zen / Go

Anomaly (opencode) hosted model gateways: **Zen** (pay-as-you-go curated catalog) and **Go** (fixed-price subscription, open-weight models). Shared account and API key; different base URLs and model sets. Dialects are upstream-native, not a single OpenAI-compatible surface.

Research date: **2026-07-25**.

## Product

| Product | Pricing | Models | Hosting notes |
| ------- | ------- | ------ | ------------- |
| [Zen](https://opencode.ai/docs/zen/) | PAYG credits (auto-reload); teams / spend caps / BYO OpenAI·Anthropic keys | Frontier proprietary + open + rotating free/stealth | US; zero-retention except free/stealth (may train) |
| [Go](https://opencode.ai/docs/go/) | Subscription ($5 first month, then $10/mo) with dollar caps: ~$12 / 5h, $30 / week, $60 / month | Open-weight coding models only | US, EU, Singapore |

Both are usable from any agent, not only the opencode CLI. Optional Go “Use balance” falls back to Zen credits when subscription windows are exhausted. Enterprise is a separate per-seat product ([docs](https://opencode.ai/docs/enterprise/)); treat “OpenCode Black” as unverified marketing.

## Auth & base URL

Key from [opencode.ai/auth](https://opencode.ai/auth). Env: `OPENCODE_API_KEY`.

| Product | Base URL |
| ------- | -------- |
| Zen | `https://opencode.ai/zen/v1` |
| Go | `https://opencode.ai/zen/go/v1` |

### Dialect matrix

| Path | Dialect | Auth header |
| ---- | ------- | ----------- |
| `/zen/v1/responses` | OpenAI Responses | `Authorization: Bearer <key>` |
| `/zen/v1/chat/completions` | OpenAI Chat Completions | `Authorization: Bearer <key>` |
| `/zen/v1/messages` | Anthropic Messages | `x-api-key: <key>` |
| `/zen/v1/models/{model}` | Google `generateContent` | `x-goog-api-key: <key>` |
| `/zen/go/v1/chat/completions` | OpenAI Chat Completions | `Authorization: Bearer <key>` |
| `/zen/go/v1/messages` | Anthropic Messages | `x-api-key: <key>` |

Anthropic and Google paths reject `Authorization: Bearer` (`401 Missing API key`). Unauthenticated catalogs: `GET /zen/v1/models`, `GET /zen/go/v1/models`. Literal key `public` works for **free Zen models only** (opencode client falls back to this when no key is set).

## Model slugs

Treat live `/models` as runtime truth. Docs tables and catalogs drift in both directions; deprecations are frequent.

In opencode config: Zen → `opencode/<slug>`, Go → `opencode-go/<slug>`.

### Zen (by dialect)

| Dialect | Slugs (2026-07-25 catalog sample) |
| ------- | --------------------------------- |
| Responses | `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.5-pro`, `gpt-5.4`, `gpt-5.4-pro`, `gpt-5.4-mini`, `gpt-5.4-nano`, `gpt-5.3-codex`, `gpt-5.3-codex-spark`, `gpt-5.2`, `gpt-5.2-codex`, `gpt-5.1`, `gpt-5.1-codex`, `gpt-5.1-codex-max`, `gpt-5.1-codex-mini`, `gpt-5`, `gpt-5-codex`, `gpt-5-nano` |
| Messages (Anthropic) | `claude-fable-5`, `claude-opus-5`, `claude-opus-4-8` … `claude-opus-4-1`, `claude-sonnet-5`, `claude-sonnet-4-6` … `claude-sonnet-4`, `claude-haiku-4-5`; Qwen in Anthropic dialect: `qwen3.7-max`, `qwen3.7-plus`, `qwen3.6-plus`, `qwen3.5-plus` |
| Google | `gemini-3.6-flash`, `gemini-3.5-flash`, `gemini-3.5-flash-lite`, `gemini-3.1-pro`, `gemini-3-flash` |
| Chat Completions | `grok-4.5`, `grok-build-0.1`, `deepseek-v4-pro`, `deepseek-v4-flash`, `glm-5.2`, `glm-5.1`, `glm-5`, `minimax-m3`, `minimax-m2.7`, `minimax-m2.5`, `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5` |
| Free | `big-pickle`, `deepseek-v4-flash-free`, `mimo-v2.5-free`, `ling-3.0-flash-free`, `nemotron-3-ultra-free`, `north-mini-code-free`, `laguna-s-2.1-free` |

### Go

| Routing | Slugs (sample) |
| ------- | -------------- |
| Chat Completions | `grok-4.5`, `glm-5.2`, `glm-5.1`, `glm-5`, `kimi-k3`, `kimi-k2.7-code`, `kimi-k2.6`, `kimi-k2.5`, `deepseek-v4-pro`, `deepseek-v4-flash`, `mimo-v2.5`, `mimo-v2.5-pro`, `mimo-v2-pro`, `mimo-v2-omni`, `hy3`, `hy3-preview` |
| Messages | `minimax-m3`, `minimax-m2.7`, `minimax-m2.5`, `qwen3.7-max`, `qwen3.7-plus`, `qwen3.6-plus`, `qwen3.5-plus` |

## Thinking / effort

Zen docs **do not fully specify** reasoning request fields. Practical source: [models.dev](https://models.dev/) provider metadata (`providers/opencode/`, `providers/opencode-go/`) and the opencode client’s `reasoningVariants()` mapping — labeled as such, not a published Zen API contract. Go’s `/zen/go/v1` is likewise thinly documented.

Per-model `reasoning_options` shapes on models.dev: `effort` (explicit `values`), `toggle`, `budget_tokens` (`min` / optional `max`).

| Dialect | Wire shape (via opencode / AI SDK) |
| ------- | ---------------------------------- |
| OpenAI Responses | `reasoning: { effort, summary }` (client often sets `summary: "auto"`, `include: ["reasoning.encrypted_content"]`) |
| Chat Completions | top-level `reasoning_effort` |
| Anthropic Messages | `thinking: { type: "enabled", budget_tokens: N }` or adaptive `thinking: { type: "adaptive", display: "summarized" }` + top-level `effort`; Kimi-on-Anthropic uses adaptive |
| Google | `thinkingConfig: { includeThoughts, thinkingLevel }` or `thinkingBudget` |

Example effort vocabularies (confirm per slug on models.dev):

| Model | Values / shape |
| ----- | -------------- |
| `gpt-5.6-sol` | `none`, `low`, `medium`, `high`, `xhigh`, `max` |
| `claude-opus-5` | `low`, `medium`, `high`, `xhigh`, `max` |
| `gemini-3.1-pro` | `low`, `medium`, `high` |
| `grok-4.5` | `low`, `medium`, `high` |
| `glm-5.2` | `high`, `max` |
| `deepseek-v4-pro` | toggle + `high` / `max` |
| Go `kimi-k3` | `max` only |
| Go `minimax-m3` | toggle only |
| `claude-sonnet-4-5` | `budget_tokens` (min 1024); client may derive `high` / `max` |

GLM / DeepSeek / Kimi may stream CoT in `reasoning_content` alongside `content`.

## Harness notes

OpenAI-dialect Zen/Go Completions or Responses map as `ProviderKind=OpenAICompatible` with the matching `BaseUrl` and `OPENCODE_API_KEY`, `OpenAiApiMode` Completions or Responses as appropriate.

| Dyson field | Zen / Go mapping |
| ----------- | ---------------- |
| `Slug` | Gateway model id (no `opencode/` prefix on the wire) |
| `DefaultReasoningEffort` / `ReasoningModes` | Per-slug from models.dev / live behavior; Dyson sends top-level `reasoning_effort` today |
| Anthropic `/messages` + `x-api-key` | **not wired yet** as OpenAICompatible (needs Anthropic dialect / header) |
| Google `x-goog-api-key` / `thinkingConfig` | **not wired yet** |
| Nested Responses `reasoning.effort` / Anthropic `thinking` | Upstream contracts; **not wired yet** beyond top-level `reasoning_effort` |
| `reasoning_content` parse / echo | **not wired yet** for models that interleave it |
| Free `public` key filter | **not wired yet** as a special-case catalog mode |

Poll `/models` rather than hardcoding slug tables. Route MiniMax/Qwen on Go to `/messages`, not Completions.

## Gotchas

- Auth header differs by dialect — do not assume Bearer everywhere.
- Docs vs live `/models` disagree; Docker and third-party lists may show free slugs absent from the live catalog.
- Deprecation cadence is aggressive (e.g. GPT-5.x Codex line retirements on Zen’s schedule).
- `budgetTokens` camelCase vs `budget_tokens` snake_case traps proxies that pass SDK options through unchanged.
- Key `public` is free models only — not a general anonymous key.
- Org rename: GitHub `anomalyco/opencode` (not `sst/opencode`); `open-code.ai` is an unofficial mirror.

## Sources

- [Zen docs](https://opencode.ai/docs/zen/) · [Go docs](https://opencode.ai/docs/go/)
- [Providers](https://opencode.ai/docs/providers/) · [Models](https://opencode.ai/docs/models/)
- [Auth / API key](https://opencode.ai/auth)
- Catalogs: [Zen `/models`](https://opencode.ai/zen/v1/models) · [Go `/models`](https://opencode.ai/zen/go/v1/models)
- [models.dev](https://models.dev/) / [api.json](https://models.dev/api.json) (`anomalyco/models.dev`)
- Third-party base URL confirmation: [Docker OpenCode Zen provider](https://docs.docker.com/ai/docker-agent/providers/opencode-zen/)
