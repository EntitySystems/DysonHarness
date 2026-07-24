# EF Core + SQLite — reference

Deeper links and notes. Read from [SKILL.md](SKILL.md) when you need official detail beyond the checklist.

## Microsoft Learn

- [SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
- [SQLite provider overview](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/)
- [EnsureCreated / Create and Drop APIs](https://learn.microsoft.com/en-us/ef/core/managing-schemas/ensure-created) — mutually exclusive with migrations
- [Applying migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) — `Database.Migrate`, startup patterns, migration locking
- [Migrations overview](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
- [Value conversions](https://learn.microsoft.com/en-us/ef/core/modeling/value-conversions) — converters, mapping hints, collections / comparers
- [DbContext lifetime / thread safety](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/) — one context, one thread at a time
- [Concurrency tokens](https://learn.microsoft.com/en-us/ef/core/saving/concurrency) — SQLite lacks native DB-generated tokens
- [dotnet ef CLI](https://learn.microsoft.com/en-us/ef/core/cli/dotnet)

## SQLite ALTER / rebuilds

Unsupported or rebuild-based operations are listed in the limitations doc. When EF generates a rebuild migration, verify:

1. Data copy columns match old → new.
2. Indexes/FKs recreated.
3. `Down` is realistic or explicitly unsafe for production user DBs.

Manual rebuild pattern (SQLite): new table → copy → drop old → rename; use `migrationBuilder.Sql(...)` when scaffolding is insufficient. See [SQLite ALTER TABLE](https://sqlite.org/lang_altertable.html#otheralter).

## Migration lock (EF9+)

SQLite uses a `__EFMigrationsLock` table instead of server app-locks. If a process dies mid-migrate, clear the lock:

```sql
DROP TABLE IF EXISTS "__EFMigrationsLock";
```

Then retry `Migrate()` / `dotnet ef database update`.

## DysonHarness connection reminders

| Context | Connection |
| ------- | ---------- |
| App runtime | `Data Source={DysonAppPaths.GetDatabasePath(...)}` |
| Design-time factory | `Data Source=dyson-design.db` |
| Explicit CLI update | `dotnet ef database update --connection "Data Source=..."` |

Prefer app `EnsureMigrated()` for normal runs so Dev/Test/Prod paths stay consistent with `DysonBuildInfo.Current`.

## Value converter tips

- Built-in enum/string/bool conversions: prefer `.HasConversion<TProvider>()` when enough.
- Custom JSON: keep serialize/normalize in the converter; stores call `Normalize` only when mutating before assign if needed for domain rules.
- Owned collections and JSON columns: without a `ValueComparer`, replacing the list reference is safer than mutating in place.
- Do not store secrets in converter debug logs or exception messages.

## Related repo docs

- [docs/storage/models.md](../../docs/storage/models.md) — providers, slugs, paths, packages
- [docs/storage/sessions.md](../../docs/storage/sessions.md) — sessions, turns, todos, logs
- [docs/storage/work-directories.md](../../docs/storage/work-directories.md)
