# Ollama

Local (and optional cloud) model runner with an OpenAI-compatible `/v1` surface. Catalogs drift; treat pulled local tags as runtime truth.

Research date: **2026-07-27**.

## Product

Ollama runs models on the local machine (`ollama serve`). Models must be pulled before use — there is no implicit pull-on-request. Cloud-offloaded variants use a `-cloud` tag suffix and still need `ollama pull`. Direct calls to `https://ollama.com/api/*` use a bearer token and drop the `-cloud` suffix.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| Base URL | `http://localhost:11434/v1/` |
| Auth | OpenAI client libraries require a key; local server ignores it. Docs use the literal `ollama`. |
| API mode | Completions (`/v1/chat/completions`). `/v1/responses` exists (v0.13.3+) but is non-stateful (no `previous_response_id` / `conversation`). |

Also useful: `GET /v1/models`, native `GET /api/tags`, `POST /api/pull`, `POST /api/show` (includes `capabilities`).

## Model slugs

Format: `[namespace/]model[:tag]`.

- Tag defaults to `latest` when omitted.
- Namespace defaults to `library` for official models.
- Tags are opaque (size, variant, quantization mixed in); do not parse them.

| Example | Notes |
| ------- | ----- |
| `llama3.1:8b` | Common local size tag |
| `gpt-oss:20b` | Thinking-capable example |
| `deepseek-r1:latest` | Thinking model |
| `mattw/pygmalion:latest` | Namespaced |
| `gpt-oss:120b-cloud` | Cloud-offloaded; must still `pull` |

List local models via `/v1/models` or `GET /api/tags`. Alias with `ollama cp <src> <dst>` when a client expects a fixed name (e.g. `gpt-3.5-turbo`).

## Thinking / effort

Supported on both native and OpenAI-compatible APIs.

| Surface | Parameter | Values |
| ------- | --------- | ------ |
| OpenAI `/v1/chat/completions` | `reasoning_effort` or nested `reasoning.effort` | `high`, `medium`, `low`, `max`, `none` |
| Native `/api/chat`, `/api/generate` | `think` | `true` / `false`, or `"low"` / `"medium"` / `"high"` / `"max"` |

- `none` maps to `think: false`; other effort strings map to the matching think level.
- Invalid effort values error with the allowed set.
- Thinking is on by default for models that support it. Model quirks apply (e.g. GPT-OSS honors `low`/`medium`/`high` only and cannot fully disable the trace).
- Response trace field: `message.reasoning` (stream: `delta.reasoning`) — non-standard vs OpenAI / `reasoning_content`. Round-trip: inbound assistant `reasoning` is fed back as thinking.

## Harness notes

Map as `ProviderKind=OpenAICompatible` with `BaseUrl=http://localhost:11434/v1`, any placeholder `ApiKey` (e.g. `ollama`), `OpenAiApiMode=Completions`.

| Dyson field | Ollama mapping |
| ----------- | -------------- |
| `Slug` | Full `[namespace/]model[:tag]` string already pulled locally |
| `DefaultReasoningEffort` / `ReasoningModes` | Prefer `high` / `medium` / `low` / `max` / `none` — Completions (default) send top-level `reasoning_effort` |
| Nested `reasoning.effort` | Wired if `OpenAiApiMode=Responses` (not the usual Ollama path; `/v1/responses` is non-stateful) |
| Native `think` | **not wired yet** |
| `message.reasoning` parse / echo | **not wired yet** — stream parsers expecting OpenAI-only shapes will miss the trace |

Omit unused sampling fields carefully (see Gotchas). Context size / `keep_alive` are not available on `/v1/` — use `OLLAMA_CONTEXT_LENGTH`, a Modelfile `num_ctx`, or native `/api/chat` (**native path not wired yet**).

## Gotchas

- Omitting `temperature` / `top_p` on `/v1/` injects `1.0` for both, silently overriding Modelfile defaults.
- No `num_ctx` / `keep_alive` passthrough on OpenAI endpoints.
- Unpulled model → 404; list before request or fail clearly.
- `tool_choice`, `logit_bias`, `user`, `n`, logprobs, and image URLs unsupported (base64 data URIs only).
- Cloud `-cloud` slugs retire on a published schedule; pinned cloud IDs can stop working. Local models are unaffected.
- `/v1/models` `owned_by` is the namespace; `created` is last-modified, not creation time.

## Sources

- [OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)
- [Thinking](https://docs.ollama.com/capabilities/thinking)
- [API introduction](https://docs.ollama.com/api/introduction) ([repo api.md](https://github.com/ollama/ollama/blob/main/docs/api.md))
- [FAQ](https://docs.ollama.com/faq) · [Context length](https://docs.ollama.com/context-length) · [CLI](https://docs.ollama.com/cli) · [Cloud](https://docs.ollama.com/cloud)
- Compat shim source: [openai/openai.go](https://github.com/ollama/ollama/blob/main/openai/openai.go)
