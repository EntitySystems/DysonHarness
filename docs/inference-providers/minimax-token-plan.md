# MiniMax Token Plan

Research date: **2026-07-25**.

## Product

MiniMax **Token Plan** is a monthly developer subscription (successor to the old Coding Plan). It covers language models plus image/speech/music/video on the API Platform through one shared usage bar.

| Tier | Price (intl) | Approx. concurrent agents |
| ---- | ------------ | ------------------------- |
| Plus | $20/mo | 3–4 |
| Max | $50/mo | 4–5 |
| Ultra | $120/mo | 6–7 |

Billing is **not** a monthly token cap. Usage is gated by a **5-hour rolling window** plus a **weekly window**; unused quota does not carry over. Exhausted windows can fall back to prepaid Credits (1,000 credits = $1, pay-as-you-go list prices), upgrade, swap to a pay-as-you-go key, or wait for reset.

Pay-as-you-go API keys are a separate product. Do not interchange them with Token Plan Subscription Keys.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| Key | Subscription Key prefix `sk-cp-…` (Billing → Token Plan). Not interchangeable with PAYG keys. |
| Auth header | `Authorization: Bearer <key>` |
| Intl base | `https://api.minimax.io/v1` |
| China base | `https://api.minimaxi.com/v1` |

| Protocol | Base | Paths |
| -------- | ---- | ----- |
| OpenAI Chat Completions / Responses | `{host}/v1` | `/chat/completions`, `/responses`, `/responses/input_tokens`, `/models` |
| Anthropic Messages | `{host}/anthropic` | `/v1/messages`, `/v1/messages/count_tokens`, `/v1/models` |

MiniMax recommends the Anthropic protocol when a tool supports both (prompt-cache benefits). Anthropic dialect is **not wired yet** in Dyson.

## Model slugs

| Slug | Context | Notes |
| ---- | ------- | ----- |
| `MiniMax-M3` | 1,000,000 | Flagship; text + image + video input, tools, agentic reasoning. Primary coding/agent model in Token Plan guides. |
| `MiniMax-M2.7` | 204,800 | Text + tools; ~60 tps |
| `MiniMax-M2.7-highspeed` | 204,800 | Same quality; ~100 tps; higher cost |
| `MiniMax-M2.5` / `-highspeed` | 204,800 | Legacy |
| `MiniMax-M2.1` / `-highspeed` | 204,800 | Legacy; programming-focused |
| `MiniMax-M2` | 204,800 | Legacy |

Discover at runtime: `GET /v1/models` or `GET /anthropic/v1/models`.

Claude Code configs sometimes use `MiniMax-M3[1m]` (bracketed context suffix) on the Anthropic protocol only — not the bare Completions/Responses slug.

## Thinking / effort

**No real effort depth or budget.** Thinking is a binary adaptive/disabled switch. Values that look like effort levels only enable adaptive thinking for wire compatibility.

| Protocol | Parameter | Allowed values | Default when omitted |
| -------- | --------- | -------------- | -------------------- |
| Chat Completions | `thinking.type` | `adaptive`, `disabled` | `adaptive` |
| Responses | `reasoning.effort` | `minimal`, `low`, `medium`, `high`, `none` | `none` |
| Anthropic Messages | `thinking.type` | `adaptive`, `disabled` | `disabled` |

- Responses: `minimal` / `low` / `medium` / `high` all enable Adaptive Thinking and **do not** tune M3 reasoning depth.
- No `budget_tokens` on any endpoint.
- Chat Completions OpenAPI has **no** `reasoning_effort` field.
- **Always send thinking/reasoning explicitly** — defaults differ by protocol.
- M2.x: thinking cannot be turned off; `disabled` is accepted and silently ignored.
- Chat Completions: thinking is inlined in `content` unless `reasoning_split: true`, which splits to `reasoning_content` / `reasoning_details` (formatting only).
- Multi-turn / tool calls: echo the full assistant message (including thinking content, or Anthropic `signature`) to preserve the reasoning chain.

## Harness notes

Wire as `ProviderKind=OpenAICompatible` with Token Plan `BaseUrl` + `sk-cp-…` `ApiKey`. Use Completions or Responses via `OpenAiApiMode`.

| Upstream field | Dyson today |
| -------------- | ----------- |
| Top-level `reasoning_effort` | Sent when slug/session effort is non-empty — **not** MiniMax’s Completions contract |
| `thinking.type` (`adaptive` / `disabled`) | **not wired yet** |
| Responses nested `reasoning.effort` | **not wired yet** (Dyson sends top-level `reasoning_effort` only) |
| `reasoning_split` | **not wired yet** |
| Anthropic Messages + `thinking` | **not wired yet** (`ProviderKind=Anthropic` path does not cover MiniMax’s `/anthropic` base) |

Practical Completions mapping until wire shapes land: store UI modes as labels only; do not expect top-level `reasoning_effort` to control MiniMax thinking. Prefer documenting slug `ReasoningModes` as empty or as harness-local labels mapped later to `thinking.type`.

Non-standard response fields: `reasoning_content`, `reasoning_details` (when `reasoning_split` is on).

## Gotchas

- 5h + weekly quota windows; unused quota does not roll over.
- Subscription Key (`sk-cp-…`) vs PAYG key: separate credential types and billing.
- Protocol default trap: Completions thinks by default; Responses and Messages do not.
- `temperature` range differs: Completions `[0,2]` vs Responses `(0,1]`.
- Use `max_completion_tokens` (not `max_tokens`); M3 recommends 131,072 (ceiling 524,288).
- Silently ignored: `presence_penalty`, `frequency_penalty`, `logit_bias`. `n` must be 1; no `function_call` (use `tools`); no audio input.
- Optional: `service_tier` (`standard` / `priority`, priority 1.5×), `prompt_cache_key`.

## Sources

- [API Overview](https://platform.minimax.io/docs/api-reference/api-overview)
- [OpenAI SDK guide](https://platform.minimax.io/docs/api-reference/text-openai-api)
- [Chat Completions API](https://platform.minimax.io/docs/api-reference/text-chat-openai)
- [Messages API](https://platform.minimax.io/docs/api-reference/text-chat-anthropic)
- [Create Response](https://platform.minimax.io/docs/api-reference/responses-create)
- [Token Plan Overview](https://platform.minimax.io/docs/token-plan/intro)
- [Quick Start](https://platform.minimax.io/docs/token-plan/quickstart)
- [Other Tools](https://platform.minimax.io/docs/token-plan/other-tools)
- [Claude Code](https://platform.minimax.io/docs/token-plan/claude-code)
- [Token Plan pricing](https://platform.minimax.io/docs/guides/pricing-token-plan)
- [Pay as You Go pricing](https://platform.minimax.io/docs/guides/pricing-paygo)
