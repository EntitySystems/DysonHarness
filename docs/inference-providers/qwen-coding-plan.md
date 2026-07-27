# Qwen Coding Plan

Research date: **2026-07-27**.

## Product

Alibaba Cloud Model Studio (**Bailian**) **Coding Plan** is a fixed-monthly-fee subscription for interactive use inside AI coding tools. Metering is by **request count**, not tokens. Alternative to pay-as-you-go.

| Property | Value |
| -------- | ----- |
| Tier | **Pro** (Lite closed to new orders 2026-03-20; renewals/upgrades stopped 2026-04-13) |
| Price | ¥200/mo (China) · $50/mo (international) |
| Quota | 6,000 requests / 5 hours · 45,000 / week · 90,000 / month |
| Reset | 5h window rolls continuously; weekly Monday 00:00 UTC+8; monthly on renewal date |
| Availability | Slot-limited; restocked daily 09:30 UTC+8 |

Sibling product **Token Plan (Team Edition)** uses a different endpoint and token-credit billing — not this page.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| Key | Coding Plan key prefix `sk-sp-…` (env often `BAILIAN_CODING_PLAN_API_KEY`) |
| Auth header | `Authorization: Bearer <key>` |
| Completions | OpenAI-compatible Chat Completions |
| Responses | OpenAI-compatible Responses (nested `reasoning.effort`) |

| Protocol | Region | Base URL |
| -------- | ------ | -------- |
| OpenAI-compatible | International / global | `https://coding-intl.dashscope.aliyuncs.com/v1` |
| OpenAI-compatible | China (Beijing) | `https://coding.dashscope.aliyuncs.com/v1` |
| Anthropic-compatible | — | `https://coding.dashscope.aliyuncs.com/apps/anthropic` |

Path is bare `/v1`, **not** `/compatible-mode/v1`. Mixing a Coding Plan subscription with the standard PAYG base URL / key silently bills pay-as-you-go on top of the subscription.

Pay-as-you-go (key `sk-…`) uses regional `…/compatible-mode/v1` hosts — separate product. Anthropic dialect is **not wired yet** in Dyson.

## Model slugs

Exact-string **allowlist** — character-by-character match, no version inference. Example failures: `qwen3-coder-max` (not listed), `GLM-5.1` (only `glm-5`).

| Slug | Notes |
| ---- | ----- |
| `qwen3.7-plus` | General; vision |
| `qwen3.6-plus` | General; vision |
| `qwen3.5-plus` | General; vision |
| `qwen3-max-2026-01-23` | General |
| `qwen3-coder-next` | Coder; non-thinking |
| `qwen3-coder-plus` | Coder; non-thinking; large context (PAYG catalog ~1M) |
| `glm-5` | Third-party |
| `glm-4.7` | Third-party |
| `kimi-k2.5` | Third-party; vision |
| `MiniMax-M2.5` | Third-party; capitalization matters |

Coder PAYG extras (`qwen3-coder-flash`, `qwen2.5-coder` family) are **not** on the Coding Plan allowlist. Alibaba’s Qwen-Coder page recommends newer general `qwen3.x-plus` models over dedicated coder slugs for many workloads.

## Thinking / effort

Alibaba migration direction: nested Responses `reasoning.effort` supersedes Completions `enable_thinking` (latter to be deprecated).

### Chat Completions

| Parameter | Applies to | Semantics |
| --------- | ---------- | --------- |
| `enable_thinking` | Qwen3.x hybrid, DeepSeek, Kimi-K2.x, GLM | Boolean toggle; Qwen3.5+ defaults `true`. Reasoning in `reasoning_content`. |
| `thinking_budget` | Qwen3.x / Qwen3-VL, Kimi | Cap on reasoning tokens; then answer immediately. Default = model max CoT length. |
| `reasoning_effort` | **DeepSeek-V4 and GLM** per Alibaba intl chat docs; QwenCloud also documents **Qwen3.8-Max-Preview** | Enum varies by family (see below). |
| `preserve_thinking` | qwen3.7-max/plus, qwen3.6-*, kimi-k2.6/k2.7-code | Feed prior-turn `reasoning_content` back; default `false`. |

`qwen3-coder-*` models are **non-thinking**: no reasoning content; thinking params not applicable.

**`reasoning_effort` scope is ambiguous** for Qwen on Completions: Alibaba chat reference limits it to DeepSeek/GLM; QwenCloud extends it to Qwen3.8-Max-Preview. Portable Completions path for Qwen: `enable_thinking` + `thinking_budget`. Forward path: Responses `reasoning.effort`.

Placement quirk: Python OpenAI SDK needs these in `extra_body`; Node/raw HTTP send them top-level.

### Responses

```json
{ "reasoning": { "effort": "high" } }
```

| Values | Default | Notes |
| ------ | ------- | ----- |
| `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` | `xhigh` | `xhigh` / `max` only in China (Beijing) and Singapore per docs. Takes precedence over `enable_thinking`. |

Reasoning returns as an output item of type `reasoning`; tokens in `usage.output_tokens_details.reasoning_tokens`.

## Harness notes

Wire as `ProviderKind=OpenAICompatible` with Coding Plan intl (or China) `BaseUrl` + `sk-sp-…` `ApiKey`. Completions or Responses via `OpenAiApiMode`.

| Upstream field | Dyson today |
| -------------- | ----------- |
| Top-level `reasoning_effort` | Completions: sent when slug/session effort is non-empty — useful for GLM/DeepSeek; **not** the primary Qwen Completions contract |
| `enable_thinking` / `thinking_budget` | **not wired yet** |
| Responses nested `reasoning.effort` | Wired when `OpenAiApiMode=Responses` (`OpenAiResponsesClient` → `reasoning: { effort }`) |
| `preserve_thinking` | **not wired yet** |
| Anthropic Messages dialect | **not wired yet** |

Slug `DefaultReasoningEffort` / `ReasoningModes` remain freeform strings for UI; map to real upstream fields when those shapes land. Coder allowlist slugs should omit effort modes (non-thinking).

Non-standard response fields: `reasoning_content` (Completions); Responses `reasoning` output items.

## Gotchas

- **Interactive-tools-only TOS:** scripts, eval backends, and other non-interactive automation are prohibited; violation can suspend the subscription or revoke the key. Use PAYG `compatible-mode/v1` for eval loops.
- Wrong base URL / key mix → silent PAYG billing on top of the subscription.
- Allowlist is exact match only.
- Account sharing prohibited; inputs/outputs may be used for model improvement.
- Qwen OAuth free tier discontinued 2026-04-15.
- China vs intl help pages occasionally diverge (pricing currency, which families get `thinking_budget`).

## Sources

- [Coding Plan (intl)](https://www.alibabacloud.com/help/en/model-studio/coding-plan)
- [Coding Plan (China)](https://help.aliyun.com/en/model-studio/coding-plan)
- [Qwen Code setup](https://www.alibabacloud.com/help/en/model-studio/qwen-code)
- [Qwen-Coder capabilities](https://www.alibabacloud.com/help/en/model-studio/qwen-coder)
- [Deep thinking](https://www.alibabacloud.com/help/en/model-studio/deep-thinking)
- [OpenAI-compatible Chat](https://www.alibabacloud.com/help/en/model-studio/qwen-api-via-openai-chat-completions)
- [Responses API](https://help.aliyun.com/en/model-studio/qwen-api-via-openai-responses)
- [QwenCloud OpenAI chat](https://docs.qwencloud.com/api-reference/chat/openai-chat) (supplementary)
