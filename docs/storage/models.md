# Model providers, slugs & app data

EF Core SQLite under platform app data stores **model providers** and their **model slugs** (and sessions — see [sessions.md](sessions.md)). Providers stay **ephemeral**: build a live `DysonAgentProvider` from a selected slug (credentials via the parent provider) when starting or resuming a session; do not persist provider instances.

For provider-specific slug catalogs, auth, and thinking/effort contracts, see [inference-providers/](../inference-providers/). This page remains the harness data model for providers and slugs.

## App mode

```csharp
public enum DysonAppMode { Dev = 0, Test = 1, Prod = 2 }
```

Prebuild scripts (`scripts/resolve-app-mode.sh` / `.ps1`) and MSBuild `GenerateAppMode` write `DysonBuildInfo.g.cs` with `Current` and `BranchName`. No git / failure → `Dev`.

| Git branch | `DysonAppMode` | App-data folder |
| ---------- | -------------- | --------------- |
| `main`, `master` | `Prod` | `DysonProd` |
| `develop`, `test`, `testing` | `Test` | `DysonTest` |
| anything else / no git | `Dev` | `DysonDev` |

## Platform paths (`DysonAppPaths`)

| OS | Base |
| ---- | ---- |
| Windows | `%LocalAppData%` (`LocalApplicationData`) |
| macOS | `~/Library/Application Support` |
| Linux | `$XDG_DATA_HOME` or `~/.local/share` |

- `GetRoot(mode)` → `{base}/{DysonDev|DysonTest|DysonProd}`
- `GetDatabasePath(mode)` → `{root}/dyson.db`
- Ensure the directory exists on first open

Single SQLite file holds providers, slugs, sessions, and app settings for that mode.

## Database

- Packages: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design` (private)
- `DysonDbContext` → `UseSqlite` at `DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current)`
- `Database.Migrate()` on open; migrations under `Harness.Engine/Migrations/`
- Entity timestamps are `DateTime` (UTC). Do not use `DateTimeOffset` on EF entities or in EF `OrderBy` queries (SQLite limitation).

## App settings (`app_settings`)

Thin key/value store (`DysonAppSettingEntity` / `DysonAppSettingsStore`).

| Property | Notes |
| -------- | ----- |
| `Key` | string PK |
| `Value` | text |

Known keys (`DysonAppSettingKeys`):
- `web_search_summarizer_model_slug_id` — Guid string of the model slug for web-search/fetch summarization; empty / missing ⇒ session model.
- `tool_panel_width_percent` — chat tools column width as a percent of the turn content row (clamped 12–50, default 30); empty / missing ⇒ 30.
- `agent_mode_tool_policy` — JSON `DysonToolPolicyDocument`: `modes.{Mode}.disabledTools` string arrays (denylist); optional `models.{slugGuid}.modes.{Mode}.disabledTools` plumbing for future per-model overlays (resolver ignores `models` in v1). Missing document / mode ⇒ all tools enabled. Edited via Settings → Agent modes (`DysonToolPolicyStore`).
- `cliproxy_api_key` / `cliproxy_management_key` / `cliproxy_port` — mirrored from `external/cliproxy/keys.json` when a managed provider connects (canonical secret store is the sidecar next to `config.yaml`).

## Configured shells (`configured_shells`)

User-managed shell catalog for MCP `ShellExecute` / long-running shell tools (`DysonConfiguredShellEntity` / `DysonConfiguredShellStore`). Not stored in `app_settings`.

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `Name` | Unique case-insensitive (SQLite `NOCASE`); MCP `shell` enum value |
| `ExecutablePath` | Absolute path or PATH-resolvable file name |
| `FixedArgsJson` | Optional JSON string array of argv prefix before the command (e.g. `["-c"]`); null/empty ⇒ basename heuristics |
| `IsEnabled` | When false, omitted from session `AvailableShells` / MCP catalog |
| `SortOrder` | Stable UI / enum order (ascending) |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |

`EnsureDefaultsAsync` seeds Windows rows when the table is empty: `Pwsh`→`pwsh`, `PowerShell`→`powershell.exe`, `Cmd`→`cmd.exe` (all enabled). Other platforms seed nothing until Bash/Zsh runners exist. Settings → Shells edits the table (optional Fixed args as space-separated tokens → `FixedArgsJson`); new/resume sessions load enabled specs into `DysonAgentSessionConfig.AvailableShells` (`Name`, `ExecutablePath`, optional `FixedArgs`).

## Model providers (`model_providers`)

Credentials and endpoint live on the provider only. Slugs are children; add/remove freely without duplicating `ApiKey` / `BaseUrl`.

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `DisplayName` | UI label (e.g. “OpenAI work”) |
| `ProviderKind` | Provider family string from `DysonProviderKinds` (`demo`, `OpenAICompatible`, `Anthropic`) |
| `BaseUrl` | Optional API root (OpenAI-compatible default `https://api.openai.com/v1`; keep `/vN` if already present, else `/v1` is appended) |
| `ApiKey` | Optional; **plaintext-local** (no OS keychain yet) |
| `OpenAiApiMode` | OpenAICompatible only: `Completions` (default) or `Responses` — see `DysonOpenAiApiModes` |
| `ManagedSource` | Optional; when set (e.g. `cliproxy-codex`, `cliproxy-grok`) the row is a managed third-party provider — view-only in UI; unique when non-null. Null = user-owned manual provider |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |
| `Slugs` | Navigation to child `model_slugs` |

Cascade-delete: removing a provider deletes its slugs.

### Managed providers (CLIProxy)

Settings → Models can **Import** ChatGPT Codex or Grok Build via a pinned local [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) binary under `{AppContext.BaseDirectory}/external/cliproxy/{version}/` (lazy download; not LocalAppData). Managed rows stay `ProviderKind=OpenAICompatible`, `OpenAiApiMode=Responses`, `BaseUrl=http://127.0.0.1:{port}/v1`, with `ApiKey` = the local proxy Bearer key. OAuth goes through CLIProxy Management API (`codex-auth-url` / `xai-auth-url` + `get-auth-status`); **Verify** syncs `/v1/models` into the slug set.

`DysonModelStore.UpsertManagedProviderAsync` keeps an id-stable row per `ManagedSource` and **merges** slugs by name: existing rows keep `Id` and `IsEnabled` while catalog fields refresh; new API models insert enabled; missing API models are removed. `UpdateProviderAsync` / slug add-update-remove reject when `ManagedSource` is set. `SetSlugEnabledAsync` toggles enablement for managed slugs only.

## Model slugs (`model_slugs`)

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `ProviderId` | FK → `model_providers` |
| `Slug` | API model id (e.g. `gpt-4o`) |
| `DisplayAlias` | UI label (e.g. “GPT-4o Fast”) |
| `IsDefault` | Global default selection for new sessions (one default across all providers) |
| `IsEnabled` | When false, omitted from new selection catalogs (picker, Composer `/model`, system-prompt catalog, `FindSlugByNameAsync`); Settings → Models still lists it. Managed providers only expose Enable/Disable; manual/custom slugs stay always selectable (API rejects toggle). Default `true` (migration + new inserts). |
| `DefaultReasoningEffort` | Optional freeform default effort (e.g. `high` / `low`); null/empty = omit. Wire shape depends on API mode: Completions → top-level `reasoning_effort`; Responses → nested `reasoning.effort` |
| `ReasoningModes` | Freeform `List<string>` of effort values for the Composer dropdown; stored as JSON TEXT via `StringListJsonValueConverter` (normalize on write; empty list on bad JSON read); default `[]` |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |

Unique index on `(ProviderId, Slug)`.

## Model favorites (`model_favorites`)

User-starred slugs for the Composer model picker (persisted per app-data DB).

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `ModelSlugId` | FK → `model_slugs` (cascade delete); unique |
| `CreatedUtc` | `DateTime` UTC — when favorited |

## `DysonModelStore`

Thin CRUD over `DysonDbContext` using the Result pattern (`Result` / `VoidResult`):

- **Providers:** list (include slugs), get, create, update (incl. `ApiKey` / `BaseUrl` / `OpenAiApiMode`; rejected when `ManagedSource` set), `UpsertManagedProviderAsync` (id-stable by source + merge slugs by name, preserving `Id`/`IsEnabled`), delete
- **Slugs:** add under a provider (optional `defaultReasoningEffort` + `reasoningModes`; rejected when provider is managed), update (alias / slug / default effort / modes / is-default; rejected when managed), remove (rejected when managed), `SetSlugEnabledAsync` (managed only)
- **Selection:** get/set default slug (get prefers enabled `IsDefault`, else first enabled; set rejects disabled), get slug by id (with provider loaded; works for disabled — resume), `FindSlugByNameAsync` (enabled only; case-insensitive exact match on `Slug` then `DisplayAlias`)
- **Favorites:** `ListFavoriteSlugIdsAsync`, `AddFavoriteAsync`, `RemoveFavoriteAsync`, `IsFavoriteAsync`

## Reasoning effort

Per-slug **default** (`DefaultReasoningEffort`) plus a **session override** (`sessions.ReasoningEffort` — see [sessions.md](sessions.md)):

1. New session / model slug change → session effort initialized from the slug’s default.
2. Models editor prefills new-slug / new-provider initial default effort to `high`; clearing the field saves **null** (omit).
3. Slug **`ReasoningModes`** registers freeform values for the Composer Effort dropdown (not a hard-coded enum; no requirement that slug default ∈ modes).
4. Composer can override for the current session only (does not rewrite the slug default).
5. Live `OpenAiCompatibleAgentProvider.ReasoningEffort` is built as session value when set (including empty = omit); if session value is null (legacy rows), fall back to slug default.
6. When non-empty, Completions request bodies include top-level `"reasoning_effort": "<value>"` and Responses include nested `"reasoning": { "effort": "<value>" }`; blank/null omits the field.
7. `StartSubagent.reasoningEffort` (optional) sets the child’s effort; omit/null uses the chosen slug’s default (or keeps the parent’s current effort when inheriting the parent model).

## System-prompt catalog

At session create / load / child spawn (when a `DysonModelStore` is available), `DysonAgentSystemPrompts.FormatAvailableModelsBlock` appends a same-kind **enabled** slug list to the system prompt: display alias, API slug, `defaultEffort`, and registered `modes`. That snapshot is persisted as `SystemPromptSnapshot`. Tests/stubs without a model store skip the block.

## OpenAI-compatible API mode

Per-provider setting (`OpenAiApiMode`), not per slug. UI shows the Completions | Responses toggle only when `ProviderKind == OpenAICompatible`.

| Mode | Relative path (under normalized BaseUrl) |
| ---- | ---------------------------------------- |
| `Completions` (default) | `POST …/chat/completions` |
| `Responses` | `POST …/responses` |

Normalize BaseUrl to an absolute API root with no trailing slash (default `https://api.openai.com/v1`). If BaseUrl already ends with `/vN` (e.g. `/v1`, `/v4/`), keep that version; otherwise append `/v1` for OpenAI default compatibility. Clients then append `/chat/completions` or `/responses` only — never a second version segment. Auth: `Authorization: Bearer {ApiKey}` when set.

## Ephemeral providers

1. UI or host loads a model slug (or default), including its parent provider.
2. Constructs a short-lived concrete `DysonAgentProvider` from provider credentials + slug fields (`DemoDysonAgentProvider` or `OpenAiCompatibleAgentProvider`).
3. Passes it into the session for that run/resume; session persists `ModelSlugId`.
4. Discards the live provider when the session ends; provider/slug rows remain in SQLite.

Do not store secrets beyond local SQLite until a keychain story exists — document and treat `ApiKey` as machine-local plaintext.
