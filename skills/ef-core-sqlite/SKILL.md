---
name: ef-core-sqlite
description: >-
  EF Core + SQLite practices for DysonHarness (DysonDbContext, migrations,
  value converters, EnsureMigrated). Use when changing entities, DbContext
  configuration, SQLite schema, migrations, value converters, indexes, cascade
  deletes, or concurrency around a shared DbContext in this repository.
---

# EF Core + SQLite (DysonHarness)

Apply when working on persistence in `Harness.Engine` (`DysonDbContext`, stores, migrations). Prefer Microsoft EF Core guidance adapted to this app’s single-file SQLite model.

## Repo map

| Piece | Location |
| ----- | -------- |
| DbContext | `src/Harness/Harness.Engine/Storage/DysonDbContext.cs` |
| Design-time factory | `src/Harness/Harness.Engine/Storage/DysonDbContextFactory.cs` |
| Migrations | `src/Harness/Harness.Engine/Migrations/` |
| Value converter example | `src/Harness/Harness.Engine/Storage/StringListJsonValueConverter.cs` |
| Apply on startup | `EnsureMigrated()` / `DysonDbContext.Open()` → `Database.Migrate()` |
| Schema docs | `docs/storage/models.md`, `docs/storage/sessions.md` |

DB file: `DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current)` → `{app-data}/dyson.db`. Timestamps: `DateTime` UTC only — never `DateTimeOffset` on entities or EF `OrderBy` (SQLite limitation; see csharp skill / rules).

## When SQLite fits

- Local / single-user app data (this harness): one file, no server, simple deploy.
- Not a substitute for multi-writer server DBs when you need strong concurrent writers, schemas, sequences, or rich ALTER support.

### Limitations vs other providers (act on these)

- No schemas / sequences; no native DB-generated concurrency tokens.
- Prefer `DateTime` (UTC) over `DateTimeOffset`; avoid ordering/comparing `decimal` / `TimeSpan` / `ulong` in SQL — EF may client-eval.
- Some schema ops need table rebuilds; EF may rebuild or throw `NotSupportedException` — review generated migrations for SQLite.
- Idempotent migration scripts are limited; prefer `Database.Migrate()` / `dotnet ef database update`.

## DbContext & connection strings

- Runtime: `OnConfiguring` calls `UseSqlite($"Data Source={path}")` when options are not already configured.
- Design-time: `DysonDbContextFactory` uses a throwaway `Data Source=dyson-design.db` for `dotnet ef` only — do not point tools at real user DBs casually.
- Prefer configuring via `DbContextOptions` when constructing from DI; keep path resolution in one place (`DysonAppPaths`).

```csharp
// Prefer migrations path used by the app:
db.EnsureMigrated();           // Database.Migrate()
// or
var db = DysonDbContext.Open(); // new context + Migrate()
```

## EnsureCreated vs migrations

| API | Use |
| --- | --- |
| `Database.Migrate()` / `EnsureMigrated()` | **This repo’s default** — creates DB if missing and applies pending migrations |
| `EnsureCreated()` | Prototypes / throwaway tests only |

**Do not mix** `EnsureCreated` with migrations. `EnsureCreated` skips the migrations history table; a DB created that way cannot be updated with migrations cleanly. If switching from `EnsureCreated` to migrations, drop and recreate (or otherwise rebuild) the DB.

## Migrations workflow

After entity / `OnModelCreating` changes:

1. Add a migration (from repo, targeting Engine):

```bash
dotnet ef migrations add <Name> \
  --project src/Harness/Harness.Engine/Harness.Engine.csproj \
  --startup-project src/Harness/Harness.UI/Harness.UI.csproj
```

2. Review the generated `Up`/`Down` for SQLite rebuilds and data loss.
3. Apply: app startup (`EnsureMigrated`) or `dotnet ef database update` with an explicit connection if needed.
4. Commit migration `.cs` + `DysonDbContextModelSnapshot.cs` together with model changes.
5. Update storage docs when schema/behavior changes (`docs/storage/…`).

Never hand-edit the live `dyson.db` schema without a migration — that causes schema drift.

## Value converters

Prefer EF `ValueConverter` / `HasConversion` over manual serialize/deserialize in store methods.

**In this repo:**

- `List<string>` ↔ JSON TEXT: `StringListJsonValueConverter` + its `ValueComparer` on `HasConversion(..., Comparer)` so change tracking sees list mutations.
- Enums → int: `.HasConversion<int>()`.
- Opaque JSON blobs (e.g. `ToolStateJson`, `PayloadJson`, `CommentsJson`) may stay as `string` columns when the store owns parse/format.

**Rules:**

- Configure converters in `OnModelCreating`, not ad hoc at call sites.
- For mutable collections/owned types, supply a `ValueComparer` (or EF won’t detect in-place edits).
- Normalize on write; define safe read behavior for null/invalid provider values (see `StringListJsonValueConverter`).
- Changing converter output shape usually needs a migration (column type/data rewrite).

## Indexes, uniqueness, cascade

Configure in `OnModelCreating` (match existing style):

- **Unique**: composite business keys, e.g. `(ProviderId, Slug)`, `(SessionId, Sequence)`, `(SessionId, TaskCode)`.
- **Index**: filter/sort columns (`LastActivityUtc`, FKs used in lookups).
- **Cascade**: owned children of a session/provider (turns, logs, todos, slugs) → `DeleteBehavior.Cascade`.
- **SetNull**: optional FKs that should survive parent delete (e.g. session → work directory / slug).
- **Restrict**: relationships that must not silently wipe graphs (e.g. parent session).

Pick delete behavior deliberately; SQLite will enforce FKs when enabled.

## Concurrency & threading

- A `DbContext` instance is **not thread-safe**. Do not run concurrent operations on one context.
- If one context is shared across UI/async work, **serialize** access (lock / queue / single-threaded ownership). Prefer short-lived contexts or clear ownership in stores.
- SQLite allows one writer at a time; keep transactions short.
- Do not call `SaveChanges` from multiple threads on the same instance; avoid overlapping queries + writes on that instance.

## Pitfalls checklist

- [ ] Schema change → migration + snapshot + docs; never rely on `EnsureCreated` in app code
- [ ] No parallel use of the same `DysonDbContext`
- [ ] No `DateTimeOffset` on EF entities / ordered queries
- [ ] Collection JSON properties use converter + comparer (or immutable replace of the list)
- [ ] Secrets: API keys live in DB for the app’s use — do not log them, do not commit `dyson.db` / `.env` credentials
- [ ] Design-time DB (`dyson-design.db`) is not user data; don’t treat it as source of truth
- [ ] Abandoned migration lock (EF9+ `__EFMigrationsLock`): if migrate hangs after a kill, clear that table (see [reference.md](reference.md))

## Additional resources

- Deeper Microsoft links and extended notes: [reference.md](reference.md)
- Storage overview: [docs/storage/models.md](../../docs/storage/models.md)
- C# / DateTime rule: [skills/csharp/SKILL.md](../csharp/SKILL.md)
