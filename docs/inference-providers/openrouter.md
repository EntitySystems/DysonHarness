# OpenRouter

Direct OpenAI-compatible access to OpenRouter with a user-supplied API key and live model discovery. Dyson ships this as a **direct managed provider**, not through CLIProxy.

## Product

Settings → Models can import one OpenRouter provider row (`ManagedSource=openrouter`). The row keeps the OpenRouter endpoint and API key, while its child slug rows contain only models the user explicitly enables from the live catalog.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| Base URL | `https://openrouter.ai/api/v1` |
| Auth | `Authorization: Bearer {ApiKey}` using the API key stored on the provider row |
| Managed source | `openrouter` |
| Provider kind | `OpenAICompatible` |
| API mode | Completions (`OpenAiApiMode=Completions`; `POST /chat/completions`) |

OpenRouter does not use CLIProxy, the loopback `8317` endpoint, or CLIProxy OAuth. The API key remains plaintext in local SQLite like other provider keys; there is no OS keychain integration in this change. Optional `HTTP-Referer` and `X-Title` attribution headers are not sent in this change.

## Model discovery and enablement

Dyson fetches the live text-model catalog with:

```http
GET https://openrouter.ai/api/v1/models?output_modalities=text
Authorization: Bearer {ApiKey}
```

Catalog entries use their OpenRouter `id` as the slug, preserving the `author/model` form. The display name uses the catalog `name` when present and otherwise falls back to the id.

The catalog is **enable-only persistence**, not a full catalog sync:

1. Import creates the managed provider with an empty slug set (`syncSlugs: false`); all catalog models are off by default. A later Import or API-key update keeps existing slug rows. The Settings Import button is still disabled once an `openrouter` row exists.
2. **Browse models** opens `OpenRouterModelsModal`, which searches the live catalog by display name or slug.
3. Checking a model upserts that one slug and enables it.
4. Unchecking a model disables its existing row but keeps the row, including user-specific effort and context settings.

Dyson does not persist the full OpenRouter catalog. Catalog entries that the user never enables do not become `model_slugs` rows.

## Thinking / effort

OpenRouter uses the Completions endpoint but expects nested reasoning configuration:

```json
{
  "reasoning": {
    "effort": "high"
  }
}
```

| Parameter | Values |
| --------- | ------ |
| `reasoning.effort` | `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max` |

Dyson maps the selected freeform `DefaultReasoningEffort` / session override to nested `reasoning.effort`. Blank or null omits `reasoning`. Other Completions providers continue to use top-level `reasoning_effort`.

Catalog `reasoning` metadata supplies each model’s effort choices and default. The parser tolerates missing metadata: a published supported-effort list is used directly; a gateway-supported reasoning declaration can use the full value set above; models without reasoning support expose no effort modes. Mandatory reasoning models do not add `none`.

## Harness mapping

| Dyson field | OpenRouter mapping |
| ----------- | ------------------ |
| `ManagedSource` | `openrouter` |
| `BaseUrl` | `https://openrouter.ai/api/v1` |
| `ApiKey` | User-supplied OpenRouter key, stored on the provider row |
| `OpenAiApiMode` | `Completions` |
| `Slug` | Live catalog `id`, persisted only when enabled |
| `DefaultReasoningEffort` / `ReasoningModes` | Live reasoning metadata plus user selection |

Updating the API key keeps existing slug rows intact. Browse-model enablement refreshes catalog metadata for the chosen slug; disabling retains the row.

## Gotchas

- OpenRouter is direct managed API-key access. Do not configure it as `cliproxy-*` or rewrite it to a local proxy key/base URL.
- Responses mode is not part of this change; inference uses Chat Completions.
- Catalog JSON can drift. Discovery and reasoning metadata parsing must remain tolerant of missing or changed optional fields.
- Nested `reasoning.effort` is the documented OpenRouter shape and should be verified with a real API key after implementation.
- Image-only and embedding-only catalog entries are excluded by the text output-modality query.

## Sources

- [OpenRouter API overview](https://openrouter.ai/docs/api/reference/overview)
- [List models](https://openrouter.ai/docs/api/api-reference/models/get-models)
- [Reasoning tokens](https://openrouter.ai/docs/guides/best-practices/reasoning-tokens)
- Storage behavior: [models.md](../storage/models.md)#managed-providers
