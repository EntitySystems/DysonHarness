---
name: ef-core-sqlite
description: >-
  EF Core + SQLite practices for DysonHarness (DysonDbContext, DysonDbAccessor,
  IDbContextFactory, migrations, value converters, WAL/timeout, SubjectId). Use when
  changing entities, DbContext configuration, repository concurrency, SQLite schema,
  migrations, value converters, indexes, cascade deletes, or any service that
  touches dyson.db in this repository.
---

# EF Core + SQLite (DysonHarness)

Apply when working on persistence in **`Harness.LocalDb`** (`DysonDbContext`, repository impls, migrations). Contracts and EF-free POCOs live in `Harness.Abstractions/Storage`. Prefer Microsoft EF Core guidance adapted to this app’s single-file SQLite model. Subject scoping / shared providers: [docs/storage/cloud-hosting.md](../../docs/storage/cloud-hosting.md).

## Repo map

| Piece | Location |
| ----- | -------- |
| Contracts + POCOs | `src/Harness/Harness.Abstractions/Storage/` |
| DbContext | `src/Harness/Harness.LocalDb/Storage/DysonDbContext.cs` |
| Connection + WAL/timeout | `DysonSqliteConfigurator` |
| Thread-safe EF entrypoint | `DysonDbAccessor` (singleton) |
| Design-time factory | `src/Harness/Harness.LocalDb/Storage/DysonDbContextFactory.cs` |
| Runtime registration | `AddDysonLocalDb(databasePath)` (UI `Program.cs`) |
| Migrations | `src/Harness/Harness.LocalDb/Migrations/` |
| Value converter example | `src/Harness/Harness.LocalDb/Storage/StringListJsonValueConverter.cs` |
| Apply on startup | **once** via factory `EnsureMigrated()` in `Program.cs` |
| Schema docs | `docs/storage/models.md`, `docs/storage/sessions.md`, `docs/storage/cloud-hosting.md` |

DB file: `DysonAppPaths.GetDatabasePath(DysonBuildInfo.Current)` → `{app-data}/dyson.db`. Timestamps: `DateTime` UTC only — never `DateTimeOffset` on entities or EF `OrderBy` (SQLite limitation; see csharp skill / rules).

Subject columns: required `SubjectId` on subject-owned tables and on `model_providers` (owner or `DysonSubjects.Shared`). Existing rows migrate to `"local"`. Child rows (slugs, turns, logs, todos) stay parent-scoped.

## When SQLite fits

- Local / single-user app data (this harness): one file, no server, simple deploy.
- Multi-subject LocalDb on one file is fine for desktop + small cloud hosts; not a substitute for multi-writer server DBs when you need strong concurrent writers, schemas, sequences, or rich ALTER support.
- Other DB backends implement the same `IDyson*Repository` interfaces outside this repo.

### Limitations vs other providers (act on these)

- No schemas / sequences; no native DB-generated concurrency tokens.
- Prefer `DateTime` (UTC) over `DateTimeOffset`; avoid ordering/comparing `decimal` / `TimeSpan` / `ulong` in SQL — EF may client-eval.
- Some schema ops need table rebuilds; EF may rebuild or throw `NotSupportedException` — review generated migrations for SQLite.
- Idempotent migration scripts are limited; prefer `Database.Migrate()` / `dotnet ef database update`.

## Connection (timeout + WAL)

Always configure through `DysonSqliteConfigurator` (not bare `Data Source=`):

- Connection string: `Data Source={path};Default Timeout=30` (busy timeout seconds)
- On open: `PRAGMA journal_mode=WAL;` + `PRAGMA synchronous=NORMAL;`
- Used by DI factory registration, `OnConfiguring`, design-time factory, and tests that open file-backed DBs
- Compaction is `DysonSqliteVacuumHostedService` (`AddDysonLocalDb`); do not ad-hoc `VACUUM` from repositories

```csharp
DysonSqliteConfigurator.Configure(options, databasePath);
```

## Mandatory service rule (concurrency)

**Any DB access in a service must use either:**

1. A **new** `DysonDbContext` from `IDbContextFactory` / `DysonDbAccessor.RunAsync` for that operation (preferred default), **or**
2. A `DysonDbContext` **passed down** from the caller that already owns it — **one logical owner / one thread of execution at a time** on that instance.

Hard bans:

- Never inject a long-lived shared scoped `DysonDbContext` into multithreaded services for ad-hoc use.
- Never share one context across parallel tools / `Task.WhenAll` / `Task.Run` / multiple repositories.
- Never stash a context on a singleton beyond the owning `RunAsync` lifetime.

### Accessor pattern

```csharp
// Public repository API
public Task<Result<Guid, string>> CreateAsync(...) =>
    _accessor.RunAsync((db, ct) => CreateCoreAsync(db, ..., ct), cancellationToken);

// Pass-down helper (same unit of work; no parallel ops on db)
private static async Task TouchAsync(DysonDbContext db, Guid id, CancellationToken ct) { ... }
```

Semantics of `RunAsync`: acquire process-wide gate (keyed by DB path) → `CreateDbContext` → run work → dispose → release gate. `DysonDbAccessor.SaveChangesAsync` retries SQLITE_BUSY (5) / SQLITE_LOCKED (6).

### SQLite one-writer

SQLite allows one writer at a time. The process-wide gate serializes writers in-process; `Default Timeout=30` waits on external lock holders. **Error 5** = database busy (another connection holds a write lock). Prefer short transactions; do not hold a context across UI awaits outside `RunAsync`.

### UpsertTurn contention retry

`IDysonSessionRepository.UpsertTurnAsync` (LocalDb impl) alone retries (~5 attempts, short backoff, fresh `RunAsync` each time) on:

- EF `InvalidOperationException` (“second operation … on this context”)
- `SqliteException` busy (5) / locked (6)

Other errors return `Failed to upsert turn: …` immediately.

## EnsureCreated vs migrations

| API | Use |
| --- | --- |
| `Database.Migrate()` / `EnsureMigrated()` | **This repo’s default** — creates DB if missing and applies pending migrations |
| `EnsureCreated()` | Prototypes / throwaway in-memory tests only |

**Migrate once** at app startup (`Program.cs` factory context). Do not call `EnsureMigrated` on every scope.

**Do not mix** `EnsureCreated` with migrations on the same durable DB file.

## Migrations workflow

After entity / `OnModelCreating` changes:

1. Add a migration (from repo, targeting LocalDb):

```bash
dotnet ef migrations add <Name> \
  --project src/Harness/Harness.LocalDb/Harness.LocalDb.csproj \
  --startup-project src/Harness/Harness.UI/Harness.UI.csproj
```

2. Review the generated `Up`/`Down` for SQLite rebuilds and data loss.
3. Apply: app startup (`EnsureMigrated`) or `dotnet ef database update` with an explicit connection if needed.
4. Commit migration `.cs` + `DysonDbContextModelSnapshot.cs` together with model changes.
5. Update storage docs when schema/behavior changes (`docs/storage/…`).

Never hand-edit the live `dyson.db` schema without a migration — that causes schema drift.

## Value converters

Prefer EF `ValueConverter` / `HasConversion` over manual serialize/deserialize in repository methods.

**In this repo:**

- `List<string>` ↔ JSON TEXT: `StringListJsonValueConverter` + its `ValueComparer` on `HasConversion(..., Comparer)` so change tracking sees list mutations.
- Enums → int: `.HasConversion<int>()`.
- Opaque JSON blobs (e.g. `ToolStateJson`, `PayloadJson`, `CommentsJson`) may stay as `string` columns when the repository owns parse/format.

**Rules:**

- Configure converters in `OnModelCreating`, not ad hoc at call sites.
- For mutable collections/owned types, supply a `ValueComparer` (or EF won’t detect in-place edits).
- Normalize on write; define safe read behavior for null/invalid provider values (see `StringListJsonValueConverter`).
- Changing converter output shape usually needs a migration (column type/data rewrite).

## Indexes, uniqueness, cascade

Configure in `OnModelCreating` (match existing style):

- **Unique**: composite business keys, e.g. `(SubjectId, AbsolutePath)`, `(SubjectId, Key)` PK on app settings, `(SubjectId, Name)` on shells, `(SubjectId, ModelSlugId)` on favorites, `(ProviderId, Slug)`, `(SessionId, Sequence)`, `(SessionId, TaskCode)`.
- **Index**: filter/sort columns (`SubjectId`, `LastActivityUtc`, FKs used in lookups).
- **Cascade**: owned children of a session/provider (turns, logs, todos, slugs) → `DeleteBehavior.Cascade`.
- **SetNull**: optional FKs that should survive parent delete (e.g. session → work directory / slug).
- **Restrict**: relationships that must not silently wipe graphs (e.g. parent session).

Pick delete behavior deliberately; SQLite will enforce FKs when enabled. Always filter repository queries by `IDysonSubjectContext.SubjectId` (and shared visibility for model providers).

## Pitfalls checklist

- [ ] Schema change → migration + snapshot + docs; never rely on `EnsureCreated` in app code
- [ ] Service DB access → factory/accessor **or** pass-down context with single-thread ownership
- [ ] No shared scoped `DysonDbContext` across parallel tools / repositories
- [ ] No `DateTimeOffset` on EF entities / ordered queries
- [ ] Collection JSON properties use converter + comparer (or immutable replace of the list)
- [ ] Connection uses `DysonSqliteConfigurator` (timeout + WAL), not bare `Data Source=`
- [ ] Do not ad-hoc `VACUUM` from repositories — compaction is `DysonSqliteVacuumHostedService` (`AddDysonLocalDb`)
- [ ] Migrate **once** at startup — not per DI scope
- [ ] Subject filters on every list/get/write; never ensure `"shared"` as a real subject row
- [ ] Shared provider writes go through `IDysonAccessEvaluator.Can(ManageSharedProviders)`
- [ ] Secrets: API keys live in DB for the app’s use — do not log them, do not commit `dyson.db` / `.env` credentials
- [ ] Design-time DB (`dyson-design.db`) is not user data; don’t treat it as source of truth
- [ ] Abandoned migration lock (EF9+ `__EFMigrationsLock`): if migrate hangs after a kill, clear that table (see [reference.md](reference.md))

## Additional resources

- Deeper Microsoft links and extended notes: [reference.md](reference.md)
- Storage overview: [docs/storage/models.md](../../docs/storage/models.md)
- Cloud hosting / subjects: [docs/storage/cloud-hosting.md](../../docs/storage/cloud-hosting.md)
- C# / DateTime rule: [skills/csharp/SKILL.md](../csharp/SKILL.md)
