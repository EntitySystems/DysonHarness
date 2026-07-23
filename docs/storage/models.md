# Model providers, slugs & app data

EF Core SQLite under platform app data stores **model providers** and their **model slugs** (and sessions — see [sessions.md](sessions.md)). Providers stay **ephemeral**: build a live `DysonAgentProvider` from a selected slug (credentials via the parent provider) when starting or resuming a session; do not persist provider instances.

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

Single SQLite file holds providers, slugs, and sessions for that mode.

## Database

- Packages: `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design` (private)
- `DysonDbContext` → `UseSqlite` at `DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current)`
- `Database.Migrate()` on open; migrations under `Harness.Engine/Migrations/`
- Entity timestamps are `DateTime` (UTC). Do not use `DateTimeOffset` on EF entities or in EF `OrderBy` queries (SQLite limitation).

## Model providers (`model_providers`)

Credentials and endpoint live on the provider only. Slugs are children; add/remove freely without duplicating `ApiKey` / `BaseUrl`.

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `DisplayName` | UI label (e.g. “OpenAI work”) |
| `ProviderKind` | Provider family string from `DysonProviderKinds` (`demo`, `OpenAICompatible`, `Anthropic`) |
| `BaseUrl` | Optional endpoint override |
| `ApiKey` | Optional; **plaintext-local** (no OS keychain yet) |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |
| `Slugs` | Navigation to child `model_slugs` |

Cascade-delete: removing a provider deletes its slugs.

## Model slugs (`model_slugs`)

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `ProviderId` | FK → `model_providers` |
| `Slug` | API model id (e.g. `gpt-4o`) |
| `DisplayAlias` | UI label (e.g. “GPT-4o Fast”) |
| `IsDefault` | Global default selection for new sessions (one default across all providers) |
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

- **Providers:** list (include slugs), get, create, update (incl. `ApiKey` / `BaseUrl`), delete
- **Slugs:** add under a provider, update, remove
- **Selection:** get/set default slug, get slug by id (with provider loaded)
- **Favorites:** `ListFavoriteSlugIdsAsync`, `AddFavoriteAsync`, `RemoveFavoriteAsync`, `IsFavoriteAsync`

## Ephemeral providers

1. UI or host loads a model slug (or default), including its parent provider.
2. Constructs a short-lived concrete `DysonAgentProvider` from provider credentials + slug fields.
3. Passes it into the session for that run/resume; session persists `ModelSlugId`.
4. Discards the live provider when the session ends; provider/slug rows remain in SQLite.

Do not store secrets beyond local SQLite until a keychain story exists — document and treat `ApiKey` as machine-local plaintext.
