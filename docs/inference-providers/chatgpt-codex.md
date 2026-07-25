# ChatGPT Codex

OpenAI Codex via ChatGPT subscription (Plus/Pro/Business/Enterprise) or platform API key. Model catalogs and effort enums rotate quickly; prefer live `/models` over hardcoded lists.

Research date: **2026-07-25**.

## Product

Codex is OpenAI’s coding agent surface (CLI, IDE, cloud, SDK). Two billing/auth paths:

| Path | Billing | Typical use |
| ---- | ------- | ----------- |
| ChatGPT sign-in (OAuth) | Plan credits / subscription | Codex CLI, IDE, Codex cloud (cloud requires ChatGPT sign-in) |
| Platform API key | Usage-based API pricing | Local CLI/IDE, plain `api.openai.com` HTTP |

They are not interchangeable for governance (ChatGPT workspace RBAC vs API org) or for every feature (e.g. Codex cloud needs ChatGPT auth).

## Auth & base URL

### ChatGPT OAuth (subscription)

PKCE against `https://auth.openai.com` (`/oauth/authorize`, `/oauth/token`). Codex CLI client id `app_EMoamEEZ73f0CkXaXp7hrann` (override: `CODEX_APP_SERVER_LOGIN_CLIENT_ID`); loopback `http://localhost:1455/auth/callback`; scopes include `openid profile email offline_access` plus connector scopes. Device-code flow: `codex login --device-code`. Account id comes from the `chatgpt_account_id` claim on the id token.

| Item | Value |
| ---- | ----- |
| Base URL | `https://chatgpt.com/backend-api/codex` |
| Auth | `Authorization: Bearer <access_token>` **and** `ChatGPT-Account-ID: <chatgpt_account_id>` |
| API mode | Responses only (`POST …/responses`, SSE). Completions removed from Codex `wire_api`. |

Also: `POST …/responses/compact`, `GET …/models?client_version=<v>`. This ChatGPT backend base is an **undocumented third-party** surface (first-party Codex clients only).

### Platform API key

| Item | Value |
| ---- | ----- |
| Base URL | `https://api.openai.com/v1` |
| Auth | `Authorization: Bearer <api_key>` (optional `OpenAI-Organization` / `OpenAI-Project`) |
| API mode | Responses (`POST /v1/responses`) — documented path for custom HTTP clients |

Sanctioned subscription-backed harness paths: [Codex SDK](https://developers.openai.com/codex/sdk), `codex exec --json`, or Codex as MCP / `codex app-server`. Scraping OAuth into a custom client against `chatgpt.com/backend-api/codex` is undocumented and TOS-risky — **not wired yet** in Dyson.

## Model slugs

Official list: [developers.openai.com/codex/models](https://developers.openai.com/codex/models). Discover at runtime via `GET {base}/models`.

**Recommended (ChatGPT sign-in):**

| Slug | Role | Codex cloud | API access |
| ---- | ---- | ----------- | ---------- |
| `gpt-5.6-sol` | Flagship; complex coding, computer use, research | no | yes |
| `gpt-5.6-terra` | Balanced everyday workhorse | no | yes |
| `gpt-5.6-luna` | Fast/cheap, high-volume tasks | no | yes |
| `gpt-5.5` | Previous-generation frontier | yes | yes |
| `gpt-5.3-codex-spark` | Real-time iteration; text-only research preview; **ChatGPT Pro only** | no | no |

Also selectable: `gpt-5.4`, `gpt-5.4-mini`. Deprecated for ChatGPT sign-in: `gpt-5.2`, `gpt-5.3-codex` (some remain on the platform API under key auth).

- Bare ids only for ChatGPT accounts — provider-prefixed names (e.g. `openai-codex/gpt-5.1-codex`) fail.
- Docs/config often use family alias `gpt-5.6` → Sol.
- Bedrock mirrors: `openai.gpt-5.6-sol`, etc.

## Thinking / effort

Wire (Responses API): nested `reasoning` object — [reasoning guide](https://developers.openai.com/api/docs/guides/reasoning).

| Parameter | Values | Notes |
| --------- | ------ | ----- |
| `reasoning.effort` | `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` | Model-dependent; GPT-5.5 / 5.6 default `medium` |
| `reasoning.mode` | `standard` (default), `pro` | GPT-5.6+; independent of effort; pro does more work per turn at standard token rates |
| `reasoning.summary` | e.g. `auto` / UI-facing summaries | Optional |
| `reasoning.context` | persisted reasoning across turns | Optional |

Example:

```json
{
  "model": "gpt-5.6",
  "reasoning": { "mode": "pro", "effort": "medium" }
}
```

Codex client enum also has `Ultra` (rewritten to `max` on the wire; fans work to subagents) and `Custom(String)` for passthrough. Config keys: `model_reasoning_effort`, `plan_mode_reasoning_effort`, etc. — no `--effort` CLI flag; use `codex --config model_reasoning_effort='"high"'` or `/model`.

## Harness notes

Documented HTTP path today: `ProviderKind=OpenAICompatible`, `BaseUrl=https://api.openai.com/v1`, API key, `OpenAiApiMode=Responses`.

| Dyson field | Codex mapping |
| ----------- | ------------- |
| `Slug` | Bare model id (`gpt-5.6-sol`, …) |
| `DefaultReasoningEffort` / `ReasoningModes` | Prefer `none` / `minimal` / `low` / `medium` / `high` / `xhigh` / `max` — Dyson sends top-level `reasoning_effort` today |
| Nested `reasoning.effort` / `reasoning.mode` | Upstream contract; **not wired yet** (Dyson only sends top-level `reasoning_effort`) |
| ChatGPT OAuth + `ChatGPT-Account-ID` | **not wired yet** |
| Base `https://chatgpt.com/backend-api/codex` | **not wired yet** (and not a supported third-party integration) |
| Codex SDK / `codex exec` host | **not wired yet** |
| `include: ["reasoning.encrypted_content"]` / `store: false` | Codex client defaults for multi-turn; **not wired yet** as Codex-specific request shaping |

## Gotchas

- ChatGPT backend `/codex` is undocumented for third parties; prefer API key + `api.openai.com` or the Codex SDK/CLI.
- Completions are gone from Codex `wire_api` — Responses only.
- Bare model ids for ChatGPT accounts; prefixed slugs error.
- Slug lists rot fast; poll `/models` (presets include `default_reasoning_effort`, `supported_reasoning_efforts`).
- `xhigh` / `max` / `reasoning.mode=pro` are model- and generation-dependent.
- Subscription vs API key differ in billing, retention, and feature reach (cloud).

## Sources

- [Codex models](https://developers.openai.com/codex/models)
- [Codex auth](https://developers.openai.com/codex/auth)
- [Reasoning guide](https://developers.openai.com/api/docs/guides/reasoning)
- [Codex SDK](https://developers.openai.com/codex/sdk)
- [Config reference](https://learn.chatgpt.com/docs/config-file/config-reference)
- Codex source (wire truth): [openai/codex](https://github.com/openai/codex) (`model-provider-info`, `codex-api`, `login`)
