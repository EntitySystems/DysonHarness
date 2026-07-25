# Kimi Code

Subscription coding API at `api.kimi.com`. Distinct from the pay-per-token Kimi API Platform (`api.moonshot.ai`) — keys, hostnames, and model IDs are **not interchangeable**.

Research date: **2026-07-25**.

## Product

**Kimi Code** is a membership-billed coding endpoint: weekly quota plus a rolling 5-hour rate window, shared across devices and API keys (max 5 keys). Quota does not roll over. Exhausting the broader Kimi membership ceiling freezes Kimi Code even if coding quota remains. An Extra Usage wallet covers overage.

OpenAI Chat Completions and Anthropic Messages are both supported on the coding host. The Responses API is not native (third-party tools often use a translation layer).

### Platform (distinct product)

| Item | Platform (PAYG) |
| ---- | --------------- |
| Base | `https://api.moonshot.ai/v1` (global), `https://api.moonshot.cn/v1` (China) |
| Key | Platform console key (`MOONSHOT_API_KEY`) |
| Slugs | `kimi-k3`, `kimi-k2.7-code`, `kimi-k2.7-code-highspeed`, `kimi-k2.6`, … |

Same underlying models, different IDs. Do not mix Platform slugs with the coding base URL (or vice versa) — typical failure is **401**.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| OpenAI base | `https://api.kimi.com/coding/v1` |
| Anthropic base | `https://api.kimi.com/coding/` |
| Key | `sk-kimi-…` from [Kimi Code Console](https://www.kimi.com/code/console) |
| Header | `Authorization: Bearer <key>` |
| Dialect | Chat Completions (primary for Dyson). Anthropic Messages available upstream. Responses: not native. |

## Model slugs

Coding endpoint IDs ([Model Configuration](https://www.kimi.com/code/docs/en/kimi-code/models.html)):

| Slug | Model | Context | Thinking | Tier |
| ---- | ----- | ------- | -------- | ---- |
| `k3` | Kimi K3 | up to 1M | `reasoning_effort` `low` / `high` / `max` | Moderato+; 1M needs Allegretto+ |
| `k3-256k` | Kimi K3 (256K) | 256K | same | Moderato+ |
| `kimi-for-coding` | Kimi K2.7 Code | 256K | always on | All members |
| `kimi-for-coding-highspeed` | K2.7 Code HighSpeed (~5–6× faster, 3× quota) | 256K | always on | Allegretto+ |

Claude Code env quirk only: `ANTHROPIC_MODEL="k3[1m]"` for the 1M window; everywhere else use plain `k3`.

## Thinking / effort

### K3 (`k3`, `k3-256k`)

Top-level `reasoning_effort`: `low` | `high` | `max`.

Kimi Code gateway normalization (agent tools often send alternate strings):

| Input | Result |
| ----- | ------ |
| null / undefined | `high` |
| `ultra` / `max` / `xhigh` | `max` |
| `high` / `medium` | `high` |
| `low` / `minimum` / `light` | `low` |
| `none` | `thinking.type` disabled |
| anything else | HTTP 400 |

Default documented as `high` on Kimi Code (Platform docs say `max` for Platform K3). Send explicitly.

Disabling thinking on the coding endpoint can silently route K3 / K2.7 to K2.6 rather than erroring.

### K2.7 Code (`kimi-for-coding`, `kimi-for-coding-highspeed`)

- No `reasoning_effort`.
- Thinking permanently on (`thinking.type: "disabled"` errors).
- Preserved thinking treated as `keep: "all"`.
- Responses include `reasoning_content` (sibling of `content`; precedes content when streaming). **Must echo `reasoning_content` on historical assistant messages** in multi-turn.

## Harness notes

Map as `ProviderKind=OpenAICompatible` with `BaseUrl=https://api.kimi.com/coding/v1`, `ApiKey=sk-kimi-…`, `OpenAiApiMode=Completions`.

| Dyson field | Kimi Code mapping |
| ----------- | ----------------- |
| `Slug` | Coding IDs only (`k3`, `k3-256k`, `kimi-for-coding`, `kimi-for-coding-highspeed`) |
| `DefaultReasoningEffort` / `ReasoningModes` | For K3: `low`, `high`, `max` (Dyson sends top-level `reasoning_effort`). For K2.7 Code: leave empty / omit — effort is N/A |
| `temperature` / `top_p` / `n` | Do not send — fixed upstream; sending errors |
| `reasoning_content` round-trip | Required for K2.7 Code multi-turn; **not wired yet** |
| Anthropic dialect | Upstream supported; **not wired yet** |
| Responses API | Not native; **not wired yet** |
| Platform product | Separate provider config if needed; **not the same as this page** |

## Gotchas

- Coding key + Platform base (or Platform key + coding base) → 401.
- Entitlement failures often return **401**, not 403 (e.g. tier lacks `k3`). Quota exhaustion: 403 (billing cycle) or 429 (period/monthly).
- Omit `temperature`, `top_p`, `n`, and penalties — fixed at 1.0 / 0.95 / 1 / 0; explicit values error.
- `tool_choice`: K3 supports `auto` / `none` / `required`; K2.7 Code rejects `required`.
- Recommend `max_tokens >= 16000` (reasoning + content share the budget) and streaming for agent loops.
- Tampering with User-Agent / client identifier is prohibited and may suspend membership benefits.
- HighSpeed costs ~3× quota for ~5–6× throughput.

## Sources

- [Kimi Code docs overview](https://www.kimi.com/code/docs/en/)
- [Model configuration](https://www.kimi.com/code/docs/en/kimi-code/models.html)
- [Membership](https://www.kimi.com/code/docs/en/kimi-code/membership.html)
- [Error reference](https://www.kimi.com/code/docs/en/kimi-code/error-reference.html)
- [Codex (third-party)](https://www.kimi.com/code/docs/en/third-party-tools/codex.html)
- Platform (distinct): [Product plans](https://platform.kimi.ai/docs/guide/product-plans) · [Models](https://platform.kimi.ai/docs/models) · [Reasoning effort](https://platform.kimi.ai/docs/guide/use-reasoning-effort)
