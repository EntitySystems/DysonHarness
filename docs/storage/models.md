# Model providers, slugs & app data

EF Core SQLite under platform app data stores **model providers** and their **model slugs** (and sessions — see [sessions.md](sessions.md)). Persistence is subject-scoped; see [cloud-hosting.md](cloud-hosting.md). Providers stay **ephemeral**: build a live `DysonAgentProvider` from a selected slug (credentials via the parent provider) when starting or resuming a session; do not persist provider instances.

Contracts: `IDysonModelRepository`, `IDysonSubjectSettingsRepository`, `IDysonConfiguredShellRepository` in `Harness.Abstractions`. SQLite implementation: `Harness.LocalDb`.

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

Shipped SQLite lives in **`Harness.LocalDb`** (not Engine). External hosts may implement the same Abstred repository interfaces against other backends.

- Packages: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design` (private) on LocalDb
- Connection: `DysonSqliteConfigurator` → `Data Source={path};Default Timeout=30`, plus `PRAGMA journal_mode=WAL` and `PRAGMA synchronous=NORMAL` on open
- `DysonDbContext` via `IDbContextFactory` at `DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current)`
- Thread-safe entrypoint: singleton `DysonDbAccessor` (process-wide gate per DB path, fresh context per `RunAsync`)
- Repository impls depend on the accessor (or a pass-down context with single-thread ownership) — never a shared scoped long-lived `DbContext`
- DI: `AddDysonLocalDb(databasePath)` (UI / hosts)
- `Database.Migrate()` **once** at startup; migrations under `Harness.LocalDb/Migrations/`
- Entity timestamps are `DateTime` (UTC). Do not use `DateTimeOffset` on EF entities or in EF `OrderBy` queries (SQLite limitation).
- `UpsertTurnAsync` retries EF concurrent-context / SQLITE_BUSY (5) / locked (6) before failing; other ops rely on the accessor gate + busy `SaveChanges` retry
- Existing rows migrate to `SubjectId = "local"` (`DysonSubjects.Local`)

### Subjects (`subjects`)

| Property | Notes |
| -------- | ----- |
| `Id` | string PK |
| `CreatedUtc` | `DateTime` UTC |
| `UserId` | string? — reserved for future user binding; unused now |

## App settings (`app_settings`)

Subject-scoped key/value (`DysonAppSettingEntity`). Callers use `IDysonSubjectSettingsRepository` (`GetSettingAsync` / `SetSettingAsync`; null/whitespace value deletes). Composite PK `(SubjectId, Key)`.

| Property | Notes |
| -------- | ----- |
| `SubjectId` | Owning subject (composite PK with `Key`) |
| `Key` | Setting key |
| `Value` | text |

Known keys (`DysonAppSettingKeys`):
- `web_search_summarizer_model_slug_id` — Guid string of the model slug for web-search/fetch summarization; empty / missing ⇒ session model.
- `tool_panel_width_percent` — chat tools column width as a percent of the turn content row (clamped 12–50, default 30); empty / missing ⇒ 30.
- `agent_mode_tool_policy` — JSON `DysonToolPolicyDocument`: `modes.{Mode}.disabledTools` string arrays (denylist); optional `models.{slugGuid}.modes.{Mode}.disabledTools` plumbing for future per-model overlays (resolver ignores `models` in v1). Missing document / mode ⇒ all tools enabled. Edited via Settings → Agent modes (`DysonToolPolicyStore`).
- `end_of_task_auto_review` — `"true"` / `"false"`; when true, a reviewer agent should auto-run after task completion (persist only for now — no reviewer spawn yet). Missing / other ⇒ off. Edited via Settings → Agent behavior.
- `self_review_intensity` — `"low"` / `"medium"` / `"high"`; how thoroughly the agent reviews its own work (persist only for now — engine does not read this yet). Missing / other ⇒ `"medium"`. Settings UI currently disables selecting `"high"`. Edited via Settings → Agent behavior.
- `cliproxy_api_key` / `cliproxy_management_key` / `cliproxy_port` — mirrored from `external/cliproxy/keys.json` when a managed provider connects (canonical secret store is the sidecar next to `config.yaml`).

`DysonToolPolicyStore` depends on `IDysonSubjectSettingsRepository` for the tool-policy document.

## Configured shells (`configured_shells`)

Subject-owned shell catalog for MCP `ShellExecute` / long-running shell tools (`DysonConfiguredShellEntity` / `IDysonConfiguredShellRepository`). Not stored in `app_settings`.

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `SubjectId` | Owning subject |
| `Name` | Unique case-insensitive per subject (SQLite `NOCASE`); MCP `shell` enum value |
| `ExecutablePath` | Absolute path or PATH-resolvable file name |
| `FixedArgsJson` | Optional JSON string array of argv prefix before the command (e.g. `["-c"]`); null/empty ⇒ basename heuristics |
| `IsEnabled` | When false, omitted from session `AvailableShells` / MCP catalog |
| `SortOrder` | Stable UI / enum order (ascending) |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |

Unique: `(SubjectId, Name)`. `EnsureDefaultsAsync` seeds Windows rows when the current subject has no rows: `Pwsh`→`pwsh`, `PowerShell`→`powershell.exe`, `Cmd`→`cmd.exe` (all enabled). Other platforms seed nothing until Bash/Zsh runners exist. Settings → Shells edits the table (optional Fixed args as space-separated tokens → `FixedArgsJson`); new/resume sessions load enabled specs into `DysonAgentSessionConfig.AvailableShells` (`Name`, `ExecutablePath`, optional `FixedArgs`).

## Model providers (`model_providers`)

Credentials and endpoint live on the provider only. Slugs are children; add/remove freely without duplicating `ApiKey` / `BaseUrl`. Each row is **subject-owned** (`SubjectId` = current subject) or **shared** (`SubjectId` = `DysonSubjects.Shared` / `"shared"`). See [cloud-hosting.md](cloud-hosting.md#shared-model-providers).

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `SubjectId` | Owning subject, or `"shared"` for deployment-wide providers |
| `DisplayName` | UI label (e.g. “OpenAI work”) |
| `ProviderKind` | Provider family string from `DysonProviderKinds` (`demo`, `OpenAICompatible`, `Anthropic`) |
| `BaseUrl` | Optional API root (OpenAI-compatible default `https://api.openai.com/v1`; keep `/vN` if already present, else `/v1` is appended) |
| `ApiKey` | Optional; **plaintext-local** (no OS keychain yet) |
| `OpenAiApiMode` | OpenAICompatible only: `Completions` (default) or `Responses` — see `DysonOpenAiApiModes` |
| `ManagedSource` | Optional; when set (e.g. `cliproxy-codex`, `cliproxy-grok`, `cliproxy-antigravity`, `cliproxy-kimi`, `cliproxy-claude`) the row is a managed third-party provider — view-only in UI; unique when non-null within scope. Null = user-owned manual provider |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |
| `Slugs` | Navigation to child `model_slugs` |

Cascade-delete: removing a provider deletes its slugs. Shared + per-subject rows may coexist (Guid PKs); list/display is a union.

### Managed providers (CLIProxy)

Settings → Models can **Import** ChatGPT Codex, Grok Build, Antigravity, Kimi, or Claude Code via a pinned local [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI) binary under `{AppContext.BaseDirectory}/external/cliproxy/{version}/` (lazy download; not LocalAppData). Managed rows stay `ProviderKind=OpenAICompatible`, `OpenAiApiMode=Responses`, `BaseUrl=http://127.0.0.1:{port}/v1`, with `ApiKey` = the local proxy Bearer key. OAuth goes through CLIProxy Management API (`codex-auth-url` / `xai-auth-url` / `antigravity-auth-url` / `kimi-auth-url` / `anthropic-auth-url` + `get-auth-status`); **Verify** syncs `/v1/models` into the slug set. Claude uses the proxy’s OpenAI/Responses surface (not Anthropic Messages).

`IDysonModelRepository.UpsertManagedProviderAsync` keeps an id-stable row per `ManagedSource` and **merges** slugs by name: existing rows keep `Id`, `IsEnabled`, and `DefaultReasoningEffort` while catalog fields (`DisplayAlias`, `ReasoningModes`, …) refresh; new API models insert enabled with catalog default effort; missing API models are removed. `UpdateProviderAsync` / slug add-update-remove reject when `ManagedSource` is set. `SetSlugEnabledAsync` toggles enablement for managed slugs only. `SetSlugDefaultReasoningEffortAsync` sets per-slug default effort for managed slugs only (blank → null/omit).

On `IDysonModelRepository`, create / update / managed upsert take an explicit `shared` flag (`false` = current subject; `true` = `DysonSubjects.Shared`, gated by `ManageSharedProviders`).

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

Subject-owned starred slugs for the Composer model picker (persisted per app-data DB). Favorites stay subject-owned even when they point at a shared provider’s slug.

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `SubjectId` | Owning subject |
| `ModelSlugId` | FK → `model_slugs` (cascade delete); unique per `(SubjectId, ModelSlugId)` |
| `CreatedUtc` | `DateTime` UTC — when favorited |

## `IDysonModelRepository`

Functional API over LocalDb (Result / VoidResult). Visibility: list/resolve providers and slugs where `SubjectId == current OR SubjectId == shared`. Shared writes require `IDysonAccessEvaluator.Can(ManageSharedProviders)`.

- **Providers:** list (include slugs), get, create (`shared` flag), update (incl. `ApiKey` / `BaseUrl` / `OpenAiApiMode`; rejected when `ManagedSource` set; `shared` flag), `UpsertManagedProviderAsync` (id-stable by source + merge slugs by name, preserving `Id`/`IsEnabled`/`DefaultReasoningEffort`; `shared` flag), delete
- **Slugs:** add under a provider (optional `defaultReasoningEffort` + `reasoningModes`; rejected when provider is managed), update (alias / slug / default effort / modes / is-default; rejected when managed), remove (rejected when managed), `SetSlugEnabledAsync` (managed only), `SetSlugDefaultReasoningEffortAsync` (managed only; blank → omit)
- **Selection:** get/set default slug (get prefers enabled `IsDefault`, else first enabled; set rejects disabled), get slug by id (with provider loaded; works for disabled — resume), `FindSlugByNameAsync` (enabled only; case-insensitive exact match on `Slug` then `DisplayAlias`; visible = current + shared)
- **Favorites:** `ListFavoriteSlugIdsAsync`, `AddFavoriteAsync`, `RemoveFavoriteAsync`, `IsFavoriteAsync` (current subject only)

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

At session create / load / child spawn (when a model repository is available), `DysonAgentSystemPrompts.FormatAvailableModelsBlock` appends a same-kind **enabled** slug list to the system prompt: display alias, API slug, `defaultEffort`, and registered `modes`. That snapshot is persisted as `SystemPromptSnapshot`. Tests/stubs without a model repository skip the block.

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
