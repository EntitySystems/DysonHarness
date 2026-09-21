# Plans

Work-directory-scoped plan rows in the same EF Core SQLite DB as sessions and work directories ([sessions.md](sessions.md), [work-directories.md](work-directories.md)). A meta-agent plan **is** the row: identity, status, and markdown body live here, not on disk. Meta Agent mode has no filesystem or shell access; addressing plans by integer `planId` is what makes that rule structural rather than a prompt convention.

Contracts: `IDysonPlanRepository` in `Harness.Abstractions`. Implementation: `DysonPlanRepository` in `Harness.LocalDb` (`DbSet<DysonPlanEntity> Plans`). Registered next to the other LocalDb repositories in `DysonLocalDbServiceCollectionExtensions`. Schema: migration `20260921165127_AddPlans`. Runtime / UI mirror: `DysonPlan` (non-EF).

Classic Plan-mode artifacts are still files under `.dyson/plans/*.md` with **no** row in this table. This table is the storage half of Meta Agent mode; a later backfill can copy those files in without a second schema change (see [Kind](#kind-dysonplankind)).

## Schema

### `plans`

| Property | Notes |
| -------- | ----- |
| `Id` | `long` PK — the `planId` callers use. SQLite `INTEGER PRIMARY KEY` (a rowid alias). EF marks it `ValueGeneratedOnAdd` by convention; the migration does **not** emit the `AUTOINCREMENT` keyword (monotonic-after-delete is not required). Assigned on `SaveChangesAsync`, not by the client. First non-Guid key in this schema. |
| `WorkDirectoryId` | Guid FK → `work_directories.Id`, `OnDelete(Cascade)`. Required. |
| `Kind` | `DysonPlanKind` stored as int |
| `Title` | Required; trimmed; what the meta page list shows |
| `Markdown` | Plan body. Required for `MetaPlan`; null for a future file-backed `ClassicPlan` |
| `PlanRelativePath` | Nullable, forward slashes (`\` normalized to `/` on create). Null for `MetaPlan`; the file pointer for a future `ClassicPlan` |
| `Status` | `DysonPlanStatus` stored as int |
| `Note` | Nullable last status note |
| `BuildAgentId` | Nullable Guid — session id of the drone building the plan, if any |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC (`UpdateAsync` bumps `UpdatedUtc`) |

`DysonPlanEntity` is the EF row. Every row written today is `Kind = MetaPlan`. The UI plans column (`MetaPlanList`) lists through `ListAsync` and opens a row via `GetAsync` into `FileViewerOverlay` (`metaplan:{planId}/{slug}.md`). **Build** is a session message; the agent’s `BeginBuildPlan` tool is what sets `Status=Building`. Engine writes: Meta Agent Drone `SubmitMetaPlan`; Meta Agent `SetPlanStatus` / `BeginBuildPlan` / `DeletePlan`. See [meta-agent.md](../engine/meta-agent.md).

Deleting a work directory cascades these rows. `IDysonWorkDirectoryRepository.DeleteAsync` still refuses a workdir that has sessions; cascade matters once the workdir itself goes away.

## Kind (`DysonPlanKind`)

```csharp
public enum DysonPlanKind { ClassicPlan = 0, MetaPlan = 1 }
```

**`ClassicPlan = 0` is reserved and unused.** Nothing in this feature writes a `ClassicPlan` row. Classic plans stay files written by Plan-mode `SubmitPlan`; they have no row. The member exists so a future move of `.dyson/plans/*.md` into this table is a backfill plus a `Kind` filter, not a schema change on a populated table. Missing `Kind == 0` rows are expected, not a bug — do not delete the enum member as dead code.

Every insert today is `MetaPlan`.

## Status (`DysonPlanStatus`)

```csharp
public enum DysonPlanStatus { Draft = 0, Building = 1, Completed = 2, Stale = 3 }
```

`CreateAsync` defaults to `Draft`. The repository accepts any defined value; it does not encode a state machine (who may set `Building` / `Completed` / `Stale` is an engine concern, not a storage one).

## Shape invariant

Enforced once in `DysonPlanRepository.ValidateKindShape` on every create and update, as a `Result` error (not an exception):

- **MetaPlan** ⇒ non-empty `Markdown` (whitespace-only counts as empty) and null `PlanRelativePath`
- **ClassicPlan** ⇒ the reverse: `Markdown` must be empty/null, `PlanRelativePath` required

Call sites do not re-check this. `CreateAsync` also rejects an empty title and an unknown kind/status.

## Filtered unique index

Unique on `(WorkDirectoryId, PlanRelativePath)` **filtered to `"PlanRelativePath" IS NOT NULL`**. Future classic rows cannot fork one file into two rows; meta rows (all null path) stay unconstrained, so many meta plans can coexist in one work directory. Meta revisions are identified by `planId`, so they need no uniqueness of their own.

## Traps

These are the two mistakes a Guid-keyed copy-paste will make. Both have already cost time.

### CreateAsync must read `Id` after save

`Id` does not exist until `SaveChangesAsync`. `CreateCoreAsync` adds the entity, saves, then returns `entity.Id` (and errors if it is still `<= 0`). Every other table in `DysonDbContext` uses a client-generated Guid, so those repositories assign `Id` *before* the save. Copying one of them verbatim yields a `CreateAsync` that returns `0` forever. Tests in `DysonPlanRepositoryTests` assert two successive inserts return non-zero, increasing ids.

### UpdateAsync cannot clear `Note` or `BuildAgentId`

`UpdateAsync` is patch semantics: a null argument means "leave unchanged". The implementation is `if (note is not null) entity.Note = note;` (same for `buildAgentId`, `title`, `markdown`, `status`). There is no sentinel that writes null back onto the row, so `Note` and `BuildAgentId` currently **cannot** be cleared once set.

This is a **known limitation**, not intended design. `title` cannot be patched to empty either (empty-after-trim is rejected); `PlanRelativePath` is not an update argument at all (create-only).

## `IDysonPlanRepository`

Result-pattern functional repository. Every method takes `workDirectoryId` and is scoped to the **current subject** (the work directory must exist and belong to `IDysonSubjectContext.SubjectId`; otherwise `"Work directory '{id}' not found."`). Sequential `planId` values are guessable and unique across the whole `plans` table, not per work directory — `workDirectoryId` is the security boundary, never the id alone. `GetAsync` / `UpdateAsync` / `DeleteAsync` look up `Id == planId && WorkDirectoryId == workDirectoryId` and fail with `"Plan '{planId}' not found."` when the row is in another workdir. `planId <= 0` is rejected before the query (`"Plan id must be a positive integer."`).

```csharp
Task<Result<IReadOnlyList<DysonPlan>, string>> ListAsync(Guid workDirectoryId, …);
Task<Result<DysonPlan, string>> GetAsync(long planId, Guid workDirectoryId, …);
Task<Result<long, string>> CreateAsync(Guid workDirectoryId, DysonPlanKind kind, string title, string? markdown, string? planRelativePath, DysonPlanStatus status = Draft, string? note = null, Guid? buildAgentId = null, …);
Task<VoidResult<string>> UpdateAsync(long planId, Guid workDirectoryId, string? title = null, string? markdown = null, DysonPlanStatus? status = null, string? note = null, Guid? buildAgentId = null, …);
Task<VoidResult<string>> DeleteAsync(long planId, Guid workDirectoryId, …);
```

- **`ListAsync`** — metadata only. The projection omits `Markdown` (always null on listed `DysonPlan`s). Newest `UpdatedUtc` first, then highest `Id`. The meta page list renders from this and does not need the body.
- **`GetAsync`** — full row including `Markdown`.
- **`CreateAsync`** — insert; returns the database-assigned `planId` **after** save. See [traps](#traps).
- **`UpdateAsync`** — patch; bumps `UpdatedUtc`. Re-runs the shape invariant on the resulting row. See [traps](#traps).
- **`DeleteAsync`** — removes the row (body included). Irreversible; no filesystem involvement.

SQLite busy/locked contention is retried by `DysonDbAccessor`; exhausted retries and other failures return error Results, never thrown exceptions.

## Engine tools (contract)

Every plan tool in `DysonWorkspaceToolExecutor.MetaAgent` is scoped by the session `_workDirectoryId`. Sequential `planId`s are guessable; a `planId` alone must never cross work directories. `planId <= 0` → `"planId must be a positive integer"`.

**`ListPlans` JSON is a closed property set:** `planId`, `title`, `status`, `buildAgentId`, `updatedUtc` — **no `planRelativePath`, no `markdown`, no `note`**, even though `ListAsync` still projects `PlanRelativePath` / `Note` onto `DysonPlan`. `DysonPlanStatusTests.ListPlans_serialized_json_omits_path_and_markdown` asserts the serialized names. Widening that payload is a regression (filesystem leak + prompt-cache churn).

**`BeginBuildPlan`** briefs the drone **by reference** (`DysonMetaBuildBrief`: planId + "call `ReadMetaPlan`"), never by inlining markdown. Unrelated to `DysonBeginBuildPlanFlow`. Refuses a second spawn while a **non-terminal** child still matches `BuildAgentId`; pass that `agentId` to extend. **`DeletePlan`** refuses while `Status == Building` (Status is the delete gate).

**`SubmitMetaPlan`** (drone-only) creates a `Draft` `MetaPlan` (or revises in place). It does **not** satisfy `SubmitSubagentReport`. No parent interrupt — the drone still reports.

**Status vs `BuildAgentId`:** `Status` is authoritative for "is this building" (page spinner, `DeletePlan`). `BuildAgentId` is retained as history and is not cleared on completion (`UpdateAsync` cannot null it).
