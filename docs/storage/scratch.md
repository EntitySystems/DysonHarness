# Scratch files

Work-directory-scoped **scratch files**: database-backed markdown documents in the same EF Core SQLite DB as sessions, work directories, and [plans](plans.md). They are shown in the Meta Agent page's Scratch tab. Identity, title, body, and user order live in the row, not on disk.

This is **not** scratch notes. Scratch notes are file-based (`.dyson/scratch/*.md`, `ListNotes` / `CreateNote` / `UpdateNote` / `DeleteNote`, root Meta Agent only). See [meta-agent.md §Scratch notes](../engine/meta-agent.md#scratch-notes). Do not mix the two.

Contracts: `IDysonMetaScratchFileRepository` in `Harness.Abstractions`. Implementation: `DysonMetaScratchFileRepository` in `Harness.LocalDb` (`DbSet<DysonMetaScratchFileEntity> MetaScratchFiles`). Registered next to the other LocalDb repositories in `DysonLocalDbServiceCollectionExtensions` (`AddScoped`). Runtime / UI mirror: `DysonMetaScratchFile` (non-EF). Schema: migration `AddMetaScratchFiles`.

## Schema

### `meta_scratch_files`

| Property | Notes |
| -------- | ----- |
| `Id` | `long` PK — the scratch file `id` callers use. SQLite `INTEGER PRIMARY KEY` (a rowid alias). Assigned on `SaveChangesAsync`, not by the client. |
| `WorkDirectoryId` | Guid FK → `work_directories.Id`, `OnDelete(Cascade)`. Required. Every lookup filters `Id` **and** `WorkDirectoryId`. |
| `Title` | Required, trimmed, at most 200 characters. Shown in the Scratch tab. |
| `Content` | Required body. May be empty. At most 131,072 characters (`string.Length`). |
| `SortIndex` | Nullable user order. Null means unpinned. |
| `CreatedUtc` / `UpdatedUtc` | UTC, through the existing global converters. Move does not bump `UpdatedUtc`. |

Index: `(WorkDirectoryId, SortIndex)`.

## Size cap

`IDysonMetaScratchFileRepository.MaxContentChars` is **131,072** (128K = 128×1024 UTF-16 characters, measured as `string.Length`). The repository enforces it on create and update. The executor checks again before the database round-trip so the agent gets the same wording. Both return a Result error; neither throws.

Over-cap error: `Scratch file content is {n} characters; the maximum is 131072 (128K). Nothing was saved.`

## Sort

`ListAsync` never loads bodies. `Content` is null and `ContentLength` is `Content.Length` projected in SQL.

Order (SQLite-translatable):

1. Rows with a non-null `SortIndex` first, ascending by `SortIndex`, then by `Id`.
2. Rows with a null `SortIndex` after that, by `UpdatedUtc` **descending**, then `Id` descending.

Descending matches the plans list (newest updated first), so a file an agent just wrote surfaces at the top of the unpinned section. New files are created with a null index and appear below any rows the user has ordered.

## Move

`MoveAsync(id, workDirectoryId, toPosition)` loads every row of the work directory in list order, re-inserts the moved row at `Math.Clamp(toPosition, 0, n-1)`, and writes `SortIndex = 0..n-1` on every row in one save. After the first user move the whole visible order is pinned. Later creates stay null and sort below the pinned rows. Delete leaves gaps; sorting is by value, so gaps are harmless. Move does not touch `UpdatedUtc`.

`// ponytail:` the rewrite is O(n) per move, fine for tens of files; upgrade to sparse indexes if counts grow.

UI positions: Move to top = 0, Move up = current−1, Move down = current+1, where current is the row's index in the list as shown.

## Delete

Hard delete. There is no archive and no restore. `DeleteAsync` removes the row. Deleting the work directory cascades scratch rows through the FK (no extra repository code). `usage` of the file-based scratch notes is unchanged.

## `IDysonMetaScratchFileRepository`

```csharp
const int MaxContentChars = 131_072;
Task<Result<IReadOnlyList<DysonMetaScratchFile>, string>> ListAsync(Guid workDirectoryId, …);
Task<Result<DysonMetaScratchFile, string>> GetAsync(long id, Guid workDirectoryId, …);
Task<Result<long, string>> CreateAsync(Guid workDirectoryId, string title, string content, …);
Task<VoidResult<string>> UpdateAsync(long id, Guid workDirectoryId, string? title, string? content, …);
Task<VoidResult<string>> DeleteAsync(long id, Guid workDirectoryId, …);
Task<VoidResult<string>> MoveAsync(long id, Guid workDirectoryId, int toPosition, …);
```

- **`ListAsync`** — metadata only (`Content` null, `ContentLength` set), in the sort above.
- **`GetAsync`** — full row including `Content`.
- **`CreateAsync`** — insert with null `SortIndex`; returns the database-assigned id. Empty content is allowed; null content is not.
- **`UpdateAsync`** — patch (null = unchanged); bumps `UpdatedUtc`. Both null returns `Nothing to update.`
- **`DeleteAsync`** — hard delete.
- **`MoveAsync`** — dense renumber; see [Move](#move).

Error strings:

| Condition | Error |
| --------- | ----- |
| `id <= 0` | `Scratch file id must be a positive integer.` |
| empty work directory id | `Work directory id is required.` |
| blank title | `Title is required.` |
| title longer than 200 characters after trim | `Title must be at most 200 characters.` |
| null content on create | `Content is required.` |
| content over the cap | `Scratch file content is {n} characters; the maximum is 131072 (128K). Nothing was saved.` |
| row missing | `Scratch file '{id}' not found.` |
| work directory missing or not owned | `Work directory '{wd}' not found.` |
| both patch fields null | `Nothing to update.` |
| other exceptions | `Failed to {verb} scratch file: {message}` |

SQLite busy/locked contention is retried by `DysonDbAccessor`; exhausted retries and other failures return error Results, never thrown exceptions.

## Engine tools

`CreateMetaScratchFile`, `ReadMetaScratchFile`, `EditMetaScratchFile`, and `DeleteMetaScratchFile` are on the root Meta Agent and the Meta Agent Drone (`DysonMetaAgentTools.CreateScratchTools()`). Work, Explore, Plan, and the classic Drone do not get them, and they are not on the default catalog. There is no list tool: `ReadMetaScratchFile` without `id` returns the sorted metadata list. `DeleteMetaScratchFile` requires a `reason` and hard-deletes. The repository is `DysonAgentSessionConfig.MetaScratchFiles` (null ⇒ `Scratch files are not available in this session (no database).`). After a successful create, edit, or delete the executor publishes `DysonMetaScratchFilesChangedEvent` on the work-directory bus key. The Scratch tab also publishes that event after a user move or delete.

Behaviour and who gets the tools: [meta-agent.md §Scratch files](../engine/meta-agent.md#scratch-files).

## Not scratch notes

Scratch **files** are these rows and the Scratch tab. Scratch **notes** are `.dyson/scratch/*.md` and `ListNotes` / `CreateNote` / `UpdateNote` / `DeleteNote`. The names are close; the stores are not. Notes stay as documented in [meta-agent.md §Scratch notes](../engine/meta-agent.md#scratch-notes).
