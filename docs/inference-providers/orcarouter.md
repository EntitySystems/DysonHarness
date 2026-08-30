# OrcaRouter

Direct OpenAI-compatible access to OrcaRouter with a user-supplied API key and live model discovery. Dyson ships this as a **direct managed provider**, not through CLIProxy. It is a sibling of [OpenRouter](openrouter.md): one managed row, Bearer key, Completions, browse-to-enable catalog. There is no CLIProxy path and no PKCE / “Sign in with OrcaRouter”.

## Product

Settings → Models can import one OrcaRouter provider row (`ManagedSource=orcarouter`). The row keeps the OrcaRouter endpoint and API key, while its child slug rows contain only models the user explicitly enables from the live catalog.

## Auth & base URL

| Item | Value |
| ---- | ----- |
| Base URL | `https://api.orcarouter.ai/v1` |
| Auth | `Authorization: Bearer {ApiKey}` using the API key stored on the provider row |
| Managed source | `orcarouter` |
| Provider kind | `OpenAICompatible` |
| API mode | Completions (`OpenAiApiMode=Completions`; `POST /chat/completions`) |

OrcaRouter does not use CLIProxy, the loopback `8317` endpoint, or CLIProxy OAuth. The API key remains plaintext in local SQLite like other provider keys; there is no OS keychain integration in this change. Keys typically look like `sk-orca-…`.

## Model discovery and enablement

Dyson fetches the live catalog with:

```http
GET https://api.orcarouter.ai/v1/models
Authorization: Bearer {ApiKey}
```

Catalog entries use their `id` as both slug and display name (the list has no `name`). Dyson keeps text/chat models: if `architecture.output_modalities` is present, the row must include `text`; otherwise the row is kept when `supported_endpoint_types` includes `openai`. Video prefixes `kling/` and `byteplus/` are dropped.

The catalog is **enable-only persistence**, not a full catalog sync:

1. Import creates the managed provider with an empty slug set (`syncSlugs: false`); all catalog models are off by default. A later Import or API-key update keeps existing slug rows. The Settings Import button is still disabled once an `orcarouter` row exists.
2. **Browse models** opens the shared direct-managed modal (`OpenRouterModelsModal`), which searches the live catalog by display name or slug.
3. Checking a model upserts that one slug and enables it.
4. Unchecking a model **deletes** its slug row (`RemoveSlugAsync`), as does **Remove** on the provider card. Disable-without-delete remains CLIProxy-only.

Dyson does not persist the full OrcaRouter catalog. Catalog entries that the user never enables do not become `model_slugs` rows.

## Thinking / effort

OrcaRouter Completions uses top-level reasoning configuration (not OpenRouter’s nested `reasoning.effort`):

```json
{
  "reasoning_effort": "high"
}
```

The live catalog does not publish reasoning metadata, so imported slugs have empty `EffortLevels`. Composer already falls back to the generic effort set when modes are empty. Blank or null omits `reasoning_effort`.

## Harness mapping

| Dyson field | OrcaRouter mapping |
| ----------- | ------------------ |
| `ManagedSource` | `orcarouter` |
| `BaseUrl` | `https://api.orcarouter.ai/v1` |
| `ApiKey` | User-supplied OrcaRouter key, stored on the provider row |
| `OpenAiApiMode` | `Completions` |
| `Slug` | Live catalog `id`, persisted only when enabled |
| `DisplayAlias` | Same as `id` |
| `DefaultReasoningEffort` / `ReasoningModes` | Empty from catalog; user may set effort on the card |

Updating the API key keeps existing slug rows intact. Browse-model enablement refreshes catalog metadata for the chosen slug; uncheck and card Remove delete the row.

## Gotchas

- OrcaRouter is direct managed API-key access. Do not configure it as `cliproxy-*` or rewrite it to a local proxy key/base URL.
- Do not treat OrcaRouter as OpenRouter in Completions reasoning: send top-level `reasoning_effort`, not nested `reasoning.effort`.
- Responses mode is not part of this change; inference uses Chat Completions.
- Catalog JSON can drift. Discovery parsing must remain tolerant of missing `architecture` / `owned_by`.
- Image-only rows (no `text` in `output_modalities`) and video prefixes are excluded. Image generation stays ineligible for any `ManagedSource`.

## Sources

- [OrcaRouter API overview](https://docs.orcarouter.ai/integrations/overview)
- [List available models](https://docs.orcarouter.ai/api-reference/models/list-available-models)
- Storage behavior: [models.md](../storage/models.md)#managed-providers
