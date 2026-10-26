# Sessions, turns & session log

Durable session state lives in the same EF Core SQLite DB as model providers/slugs ([models.md](models.md)). Sessions are **subject-owned** (`SubjectId`); see [cloud-hosting.md](cloud-hosting.md). **Turns** are the resume source of truth; the **session log** is an append-only audit / UI timeline.

Contracts: `IDysonSessionRepository` in `Harness.Abstractions`. Implementation: `Harness.LocalDb`.

## Schema

### `sessions`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK — **persistence id** (distinct from runtime int `DysonAgentSession.Id`) |
| `SubjectId` | Owning subject (`DysonSubjects.Local` or cloud subject id) |
| `RuntimeId` | Root `0` / subagent ≥ `1` |
| `ParentSessionId` | Guid? FK to parent persisted session |
| `AgentMode` | Ask / Plan / Work / … |
| `ModelSlugId` | Guid? FK to `model_slugs` (credentials via parent provider; may reference a shared provider’s slug) |
| `ReasoningEffort` | Session-scoped `reasoning_effort` override; null = fall back to slug `DefaultReasoningEffort` on resolve; empty = omit from request |
| `MaxTargetContextTokens` | Session max target context; null = inherit slug `DefaultMaxTargetContextTokens` / harness 200K; `0` = Off (no context compaction). **Meta Agent ignores this column** and is pinned at 180K (`DysonMaxTargetContextTokens.MetaAgentFixed`) |
| `CompactionPendingTurnId` | Guid? — durable live-compaction marker (migration `AddSessionCompactionPendingTurnId`). Set to the tripping turn's id when the live count passes the limit, or when a turn ends over the limit and cannot compact right away; cleared when the compaction summary succeeds (or a `FullSummarize` turn completes). Loaded on activate and read at prompt start (`GetCompactionPendingTurnIdAsync`); non-null means the session summarizes before it sends anything. Null on existing rows |
| `WorkDirectoryId` | Guid? FK to `work_directories` (`SetNull` on delete; required for new sessions) |
| `WorktreeEnabled` | bool, default `false` — composer **Worktree** checkbox (copied from workdir `forkWorktree` on create). Not a second `work_directories` row. |
| `WorktreeAbsolutePath` | string? — absolute git worktree checkout; null until created. Clearing after merge/remove is allowed. |
| `WorktreeBranch` | string? — e.g. `dyson/{8-hex of session id}`; null until created |
| `McpAccessMode` | enum |
| `Status` | `Active`=0 / `Completed`=1 / `Stopped`=2 / `Failed`=3 / `Interrupted`=4 (child terminal after process-restart recovery; roots stay `Active` on that sweep. `Completed`/`Failed` **roots and children** (and `Interrupted` children, e.g. a drone resumed after a restart) may return to `Active` on a new in-flight prompt (`BeginInFlightPrompt` / `PromptAsync` / queued user turn / `PromptHarnessTurnAsync`; children also via parent `TriggerSubagentEvent`). In-memory reopen is `TryReopenForNewParentTask` at `BeginInFlightPrompt`; durable `Active` persist is `PersistReopenAsync` on the prompt path (roots and children) and on parent `TriggerSubagentEvent` (also `MessageMetaAgentDrone` / `BeginBuildPlan` reuse); the roster then shows `Active` with no `finishedAt`. Do not reopen `Stopped`) |
| `Title` | Optional UI title (agent `RenameSession` / first prompt); mirrored live as `DysonAgentSession.DisplayTitle` |
| `SystemPromptSnapshot` | Prompt at create time; updated on mid-session `ApplyAgentMode` |
| `CreatedUtc`, `UpdatedUtc`, `LastActivityUtc` | `DateTime` UTC |

Live session: `DysonAgentSession.PersistenceId` ↔ `sessions.Id`. Work directories: [work-directories.md](work-directories.md). Session git worktrees persist **on the session row** (`WorktreeEnabled` / `WorktreeAbsolutePath` / `WorktreeBranch`); they are never extra `work_directories` rows and do not mutate `work_directories.AbsolutePath`. Child rows (`turns`, `session_logs`, `session_todos`) stay parent-scoped (no redundant `SubjectId`). Repository visibility: current subject only; cross-subject get-by-id → error.

### `turns`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK — also `DysonAgentTurn.Id` |
| `SessionId` | Guid FK |
| `Sequence` | Order within session |
| `Kind` | `DysonAgentTurnKind` (`PlanResult` = 6, `BeginBuildPlan` = 7, `SubagentReportProcessing` = 8, `ShellExited` = 9, `RethinkToolUsage` = 10, `DisplayInfo` = 11 — UI-only chrome, omitted from provider transcripts; `ModeSwitch` = 12 — mode boundary, completed immediately, included in provider transcripts as a short harness user message; modes in `Instruction` as `From→To`; `DropContext` = 13 — obsolete (the automatic Drop Context inject was removed), kept so old rows load; `TaskEndReflect` = 14; `BugReview` = 15; `FullSummarize` = 16 — agent-authored session summary; after completion earlier turns are excluded from later transcripts; `WorktreeCreating` = 17 — UI-only chrome while forking a session git worktree; starts incomplete (`CompletedUtc` null) so the UI can spin; omitted from provider transcripts like `DisplayInfo`, not like `ModeSwitch`; `MetaMaintenance` = 18 — obsolete (the Meta Agent maintenance tick was removed), kept so old rows load; `ChildReportReminder` = 19 — child idle-without-report nudge; `ParentEvent` = 20 — harness parent-event continuation, included in provider transcripts, not a meta-chat bubble) |
| `AgentTitle` | Parsed H1 / plan title |
| `PlanRelativePath` | Workspace-relative plan path for `PlanResult` / `BeginBuildPlan` (e.g. `.dyson/plans/…`); null otherwise |
| `Instruction` | Harness-injected instruction |
| `HiddenInstruction` | string? — `HiddenInstruction` is model-only local paths for files that were not uploaded. An image with `RemoteUrl` has no workspace file; the prompt echoes that URL at build time as `Attached urls`. Neither paths nor URLs are shown in chat. `UserImagesJson` still stores the image attachment (`Base64Data` + `RemoteUrl`) |
| `AssistantText` | Agent body after title |
| `ReasoningText` | Denormalized join of Thought segments only (UI / reload / search; not replayed into transcripts) |
| `ReasoningLogJson` | Ordered Thought + InterimText + **UserComment** JSON for thinking history. Thought/Interim stay UI + DB only (omitted from transcripts). **UserComment** (`kind` 2) is persisted the same way with optional `deliveredAfterToolCalls` (the turn's tool-call count when the comment was delivered to the model). Every later request replays it as a `[User comment during turn]` user message at that anchor; rows without the field (legacy, or never delivered) replay after the turn's tools, before its assistant text. No migration: the field lives inside this JSON string. The in-memory drain queue (`HasPendingUserComments`) is **not** persisted — comments survive resume via this log, not the queue. Empty/null with legacy `ReasoningText` → restore synthesizes one Thought |
| `SkillsUsedJson` | Context files attached this turn (slash `/skill-` or `LoadSkill` skills, or StartSubagent `contextFiles`); JSON array of `DysonContextFileEntry` including `kind`. Injected into provider transcripts as separate `[Skill: …]` or `[File: relative/path]` user messages |
| `UserImagesJson` | User-attached composer images this turn; JSON array of `DysonBinaryAttachment` fields (no `FileId`; includes `base64Data` for thumbs plus optional `remoteUrl` / `objectKey` / `remoteUrlExpiresUtc`). Re-emitted as multimodal Completions/Responses parts on restore |
| `ToolStateJson` | Full snapshot of tool calls + results (restore fidelity), plus optional per-round data (camelCase keys; no new column, no migration): `toolRoundCallCounts`; `toolRoundReasoningItems` (raw Responses reasoning items per tool round, index-aligned with the counts, `null` for a round without any); `finalReasoningItems` (the final no-tool round); `reasoningItemsSource` (the `ReasoningSourceTag` that produced them: `ManagedSource`, else the base URL's `scheme://host:port`, else the provider id). The three reasoning keys are omitted when no round has reasoning items, and rows written before they existed restore with none. Replay for finished turns is gated by `preserve_reasoning_traces` and a matching source ([engine README](../engine/README.md)#openai-compatible-provider). Stored encrypted reasoning blobs make these rows larger. Visualization bodies stay here on the render turn |
| `VisualizationId` | `Guid?`. Set only on a meta `DisplayInfo` post. Null otherwise. The viz body stays in `ToolStateJson` |
| `UserQuestionJson` | string?. Meta `DisplayInfo` question card (question and answer in one JSON blob). Null on every other turn. `answeredUtc` inside the blob is UTC `DateTime`, not its own column |
| `ToolHistoryOptimized` | bool |
| `CompactToolHistory` | string? |
| `CreatedUtc`, `CompletedUtc`? | `DateTime` UTC |
| `IsExcludedFromContext` | bool — when true, omitted from provider transcripts; UI still shows Dropped + Restore |
| `ContextSummary` | string? — when set, provider transcripts emit a compact summary stub instead of the full turn body; UI shows Summarized badge. UI `/summarize` writes it; agent `DropTurnContext` does not (that resume is appended to the calling turn's `AssistantText` with the dropped ids). `RestoreTurnContext` clears it when it actually restores. FullSummarize and UI drop do not write it |
| `InterruptionReason` | string? — nullable process-restart / recovery marker (stable code, e.g. `application-restart`). Presentation-only; not assistant text and not replayed into provider transcripts |

### `session_todos`

Each session (root or subagent) owns its own list. Cascade-deleted with the session. Unique `(SessionId, TaskCode)`.

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `SessionId` | Guid FK → `sessions` (cascade) |
| `TaskCode` | string; unique per session (trimmed) |
| `DisplayName` | string |
| `Status` | int enum `DysonSessionTodoStatus`: `0=Pending`, `1=Ongoing`, `2=Complete` |
| `CommentsJson` | JSON `string[]` (default `[]`); append-only via update `appendComment`, or full replace via `comments` |
| `Sequence` | int create/replace order within the session |
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC. `UpdatedUtc` bumps on every update (status, display name, comments). Every entity `DateTime` is read back with `DateTimeKind.Utc` (`UtcDateTimeValueConverter`); stored TEXT is unchanged |

A session holds at most 50 todos, completed included (`DysonSessionTodoLimits.MaxPerSession`). `CreateTodo` returns a Result error at the cap; `ReplaceTodos` fails the whole replace when the incoming count is over 50. Enforced for every session kind.

Runtime mirror: `DysonSessionTodo` (same fields; `Comments` as `IReadOnlyList<string>`). Covered by `DysonSessionTodoTests` in `Harness.Tests`.

### `session_logs` (discriminated JSON)

Append-only. Filter by `Kind`; payload fields live in `PayloadJson`. `session_logs` remains the structured DB/UI timeline; `dyson.log` is the host/framework exception file next to `dyson.db` (see [models.md](models.md#platform-paths-dysonapppaths)).

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `SessionId` | Guid FK (indexed) |
| `TurnId` | Guid? when event belongs to a turn |
| `Sequence` | Monotonic per session |
| `TimestampUtc` | `DateTime` UTC |
| `Kind` | Discriminator (`DysonSessionLogKind`) |
| `PayloadJson` | Kind-specific JSON |

## Log kinds & payload shapes

| Kind | Payload (illustrative) |
| ---- | ---------------------- |
| `SessionCreated` | session meta snapshot |
| `SessionResumed` | `{ "sessionId" }` |
| `SessionStatusChanged` | `{ "status", "reason"? }` (`reason` is optional; process-restart descendant interrupt uses `application-restart`) |
| `SessionRenamed` | `{ "title" }` (from `RenameSession` / `RenameAsync`) |
| `UserPrompt` | `{ "prompt", "filePaths"? }` |
| `TurnStarted` / `TurnCompleted` | `{ "turnId", "kind", "agentTitle"? }` |
| `AgentReply` | `{ "turnId", "title", "body" }` |
| `ToolCallQueued` / `ToolCallWorking` / `ToolCallCompleted` / `ToolCallFailed` | `{ "turnId", "callId", "toolName", "stage", "argumentsJson"?, "resultContent"?, "isError"? }` |
| `Interrupt` | `{ "interruptKind", "subagentId", "summary"? }` |
| `TurnInterrupted` | `{ "turnId", "reason" }` (durable recovery marker; distinct from live `Interrupt`) |
| `ContextOptimized` | `{ "turnsCompacted", "tokenEstimate"? }` |
| `LogLine` | `{ "line" }` (from `AppendLog`) |
| `CompletionFlow` | `{ "phase": "CompleteTask"\|"Confirm"\|"Continue"\|"ReportSummary", … }` |

Use small sealed records per kind plus a type-discriminator helper (`DysonSessionLogPayload`). Store `Kind` for SQL filtering; JSON carries the fields.

## `IDysonSessionRepository` API

Result-pattern functional repository (subject-scoped):

```csharp
Task<Result<Guid, string>> CreateSessionAsync(DysonSessionCreateRequest request, CancellationToken ct = default);
Task<VoidResult<string>> UpdateSessionMetaAsync(...);
Task<VoidResult<string>> UpsertTurnAsync(DysonTurnEntity turn, CancellationToken ct = default);
Task<VoidResult<string>> DeleteTurnsAsync(Guid sessionId, IReadOnlyList<Guid> turnIds, CancellationToken ct = default);
Task<VoidResult<string>> AppendLogAsync(DysonSessionLogEntry entry, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(Guid? workDirectoryId = null, bool rootsOnly = true, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsAsync(Guid parentSessionId, CancellationToken ct = default);
Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<Guid?, string>> GetCompactionPendingTurnIdAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>> ListActiveSessionsWithUnfinishedTurnsAsync(CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListActiveDescendantSessionsAsync(CancellationToken ct = default);
Task<VoidResult<string>> DeleteSessionAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<DysonSessionTodo, string>> CreateTodoAsync(DysonSessionTodoCreateRequest request, CancellationToken ct = default);
Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(DysonSessionTodoUpdateRequest request, CancellationToken ct = default);
Task<VoidResult<string>> DeleteTodoAsync(Guid sessionId, string taskCode, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(Guid sessionId, IReadOnlyList<DysonSessionTodoReplaceItem> items, CancellationToken ct = default);
```

`ListSessionsAsync` optionally filters by `WorkDirectoryId` (within the current subject). `ListChildSessionsAsync` returns direct children of a parent ordered by `RuntimeId`. `DysonSessionCreateRequest` / summaries include `WorkDirectoryId`. Create also copies `WorktreeEnabled` (and optional path/branch, usually null until the first Work fork). `DysonSessionSummary` exposes `WorktreeEnabled` and `HasWorktree` (true when `WorktreeAbsolutePath` is non-empty) so the session list can disable delete without a second query. `DysonSessionMetaUpdate` can patch status/title/model/effort and, on mid-session mode switch, `AgentMode` + `SystemPromptSnapshot`. Worktree patches: `UpdateWorktreeEnabled` writes `WorktreeEnabled`; `UpdateWorktreeLocation` writes both `WorktreeAbsolutePath` and `WorktreeBranch` (nulls clear the location after merge/remove). `UpdateCompactionPending` writes `CompactionPendingTurnId` (null clears). `GetCompactionPendingTurnIdAsync` reads only that column (missing or cross-subject session → error). Repository `ListSessionsAsync` returns every mode, including Meta Agent roots. The sidebar uses `DysonUiHost.ListSessionsAsync`, which omits roots whose `AgentMode` is Meta Agent.

`DeleteTurnsAsync` hard-deletes the given turn rows for that session. Empty `turnIds` is a success no-op; missing ids are ignored; unknown/cross-subject session returns `Session '{id}' not found.` Irreversible. The harness has no caller since the Meta Agent maintenance tick was removed. Context compaction, FullSummarize, and `DropTurnContext` flip `IsExcludedFromContext` instead (agent `DropTurnContext` appends its resume to the calling turn's `AssistantText`, not `ContextSummary`). It does not compact surviving `Sequence` values.

`UpsertTurnAsync` looks up by turn id. On insert, the row is appended at `MAX(Sequence) + 1` whenever the requested sequence is at or below the current max (holes included); an empty session keeps the requested sequence. On update, content fields are copied and `Sequence` is never changed. Gaps are expected; `GetFullSessionAsync` still orders by sequence.

`GetFullSessionAsync` returns session row + all turns (ordered) + all log entries (ordered by `Sequence`) + todos (ordered by `Sequence`).

### Context compaction rows

No mode hard-deletes turn rows any more (the Meta Agent 40-turn window is gone). When [context compaction](../engine/README.md#context-compaction) runs, it persists through the normal turn path:

- The turn that ended: for a mid-work pause, Success with its tool state and reasoning items kept and `AssistantText` = the `Paused for context compaction` note; for a turn that finished over the limit, its normal reply.
- One `FullSummarize` turn whose `AssistantText` is the summary, stored as written (no character cap; the prompt asks for under about 35,000 tokens). `ApplyAfterCompletion` sets `IsExcludedFromContext` on every earlier turn, and those rows are upserted.
- One fresh `Continuation` turn, queued after the summary, only when queued harness follow-ups were dropped or the turn was a mid-work pause. The continuation already sitting in the queue is dropped and replaced; it is not "whatever was already queued". Parent-inject turns, user prompts, and subagent-report / shell-exited / parent-event turns stay queued. A failed summary clears nothing.
- `sessions.CompactionPendingTurnId` set while the trip is open and cleared after the summary succeeds. A pending trip found on activate or at prompt start puts the `FullSummarize` turn **before** the new prompt's turn.
- A failed summary leaves a `FullSummarize` row with `AgentTitle` `Context compaction failed`, the error in `AssistantText`, and `IsExcludedFromContext` true; the marker stays set.

Who writes the rows: for a root session the host/runtime persist path applies the summary and upserts the summary and dropped turns after the prompt returns, the same as `/summarize-full`. A child session (subagent) has no host apply step, and a pre-prompt summary is not the last turn, so in those cases `DysonCompactionFlow.RunAsync` applies it inline and raises `TurnUpdated` for each newly excluded turn and for the summary turn; hosts upsert turns on that event.

Older sessions may still contain `MetaMaintenance` turns and `EVICTED TURN …` log lines from the removed tick. They load and render as before.

Reminder counters, `LastReportSummary`, and `ReadMetaAgentDroneLog` lines are in-memory (each `AppendLog` line is also persisted as a `LogLine` session_logs row, uncapped: `USER COMMENT (injected mid-turn, turn {id8}): …` on inject, `PARENT MESSAGE: …` on the child for `TriggerSubagentEvent`). A restart synthesizes Interrupted descendants immediately (parent Interrupt log) rather than reading a persisted reminder count. A restored `Stopped` child is treated as already reported: `RestoreFromPersisted` takes `LastReportSummary` from its last `SessionStatusChanged(Stopped)` log reason, so the restore-time child-report watch does not re-report it as `application restart`.

`ListActiveSessionsWithUnfinishedTurnsAsync` is the subject-filtered recovery scan: current-subject sessions with `Status == Active` that still have at least one turn whose `CompletedUtc` is null. Each row includes unfinished-turn summaries (`TurnId`, `Sequence`, `Kind`, `CreatedUtc`, `InterruptionReason`). Cross-subject rows are never returned. This is not a distributed lease.

`ListActiveDescendantSessionsAsync` is the complementary subject-filtered recovery scan: current-subject sessions with `Status == Active` and a non-null `ParentSessionId` (any depth). Roots are never returned. After a process restart these descendants cannot resume their in-process runtime even when every turn is already complete; recovery marks them `Interrupted`. Cross-subject rows are never returned. Circuit disconnect in a still-running process does not use these scans.

`DeleteSessionAsync` removes the session and descendant subagent sessions (`ParentSessionId` is Restrict, so children are deleted deepest-first). Turns, session logs, and todos cascade. A root whose `AgentMode` is Meta Agent **errors** with `Meta Agent sessions cannot be deleted.` and is not removed (no descendant walk). Meta Agent Drone rows are not protected. When `WorktreeAbsolutePath` is set, delete **errors** with the exact message `Merge or delete this session's worktree before deleting the session.` Enabled-without-path (`WorktreeEnabled` true, path null) still deletes. There is no auto-`git worktree remove` on session delete. Bulk inactive delete is host-side (`DysonUiHost.DeleteInactiveSessionsAsync` + `DysonSessionInactiveDelete` + existing `DeleteSessionAsync`), not a new repository API. The host overlay protects the current session and busy sessions; idle `Active` leftovers are deletable. `DysonSessionInactiveDelete.SelectDeletableRootIds` **skips** roots with `HasWorktree` and Meta Agent roots, so header “delete inactive” still removes other inactive roots and cannot wipe a live checkout. The host and runtime refuse a Meta Agent delete before cancel, clear-focus, or unregister; the repository guard still applies if a caller skips that pre-check.

Todo CRUD rejects duplicate `TaskCode` (create) / missing code (update/delete). `UpdateTodoAsync` patches optional `DisplayName` / `Status`; `Comments` replaces the full list; `AppendComment` appends one string after any replace. `ReplaceTodosAsync` clears then inserts the seed set (used by `StartSubagent` child seed); duplicate codes in the set fail.

### `DysonPersistedSession`

Aggregate DTO: session entity + `IReadOnlyList` turns + `IReadOnlyList` log entries + `IReadOnlyList` todos.

## Resume

1. `GetFullSessionAsync(sessionId)`
2. Construct concrete session with ephemeral provider (from selected model slug + parent provider)
3. `RestoreFromPersisted(state)` — sets `PersistenceId`, rebuilds `TurnHistory` from turn rows (`ToolStateJson` → tool calls / tracked / response log), restores todos via `RestoreTodos`, restores mode/config snapshots as applicable
4. Append `SessionResumed` log (skipped when the host hydrates a child quietly during parent resume)
5. Host (`LoadAndFocusSessionAsync`) lists direct children via `ListChildSessionsAsync(parentId)`, loads each missing child (`appendResumeLog: false`), and calls `RestoreRegisteredSubagent` so `SubagentsById` / `SubSessions` are session-owned again (Wait / Inspect / Stop / ListSubagents work across turns and after cold resume)
6. Session is ready for further `PromptAsync`

Demo path: `DemoDysonAgentSession.LoadAsync(sessionRepository, sessionId, provider)` (also used by the retained-scope runtime factory).
OpenAI-compatible path: `OpenAiCompatibleAgentSession.LoadAsync(sessionRepository, sessionId, provider, http, workDirectoryAbsolutePath)` (host direct path, and the retained-scope runtime factory for every built-in agent mode; Completions vs Responses follows `OpenAiApiMode`).

**Provider kind** has no column: it is implied by `ModelSlugId` → `model_slugs.ProviderId` → `model_providers.ProviderKind` (`EffectiveKind`). Converting a root Demo session to OpenAI-compatible (`DysonSessionProviderConversion.ConvertSessionProviderAsync`) rewrites only that FK, the reasoning effort (target slug default), and `MaxTargetContextTokens` when it equaled the old slug default; it appends one `LogLine` ("Converted provider from … to …"). Turns, summaries, exclusions, title, mode, worktree, and parent id are not rewritten. Children are refused (they keep their slug). No migration.

**Worktree on resume/load:** if `WorktreeAbsolutePath` is set and that directory still exists, the host/factory binds the live session to that path (tools, shells, openrules). If the column is set but the folder is gone, load **clears** `WorktreeAbsolutePath` / `WorktreeBranch` (`UpdateWorktreeLocation` with nulls) and falls back to the registered work-directory `AbsolutePath`. `WorktreeEnabled` is kept. Process-restart recovery already finalizes an incomplete `WorktreeCreating` turn (`CompletedUtc` + `InterruptionReason`); the next Work send retries ensure if the path is still null.

Same-process circuit reconnect is not this cold path: `DysonUiRuntimeAttachment` reattaches the retained `DysonSessionRuntime` and does not rerun recovery or rebuild the live graph from SQLite.

## Process-restart recovery

On the first `DysonSessionRuntimeRegistry.GetOrCreateAsync` for a subject after process start, `DysonSessionRuntime.EnsureRecoveredAsync` runs `DysonSessionRecoveryService` once. Circuit disconnect / host dispose does **not** run this sweep — the same-process runtime is retained.

1. Scan `ListActiveSessionsWithUnfinishedTurnsAsync`.
2. For each turn with null `CompletedUtc`: rehydrate tool state, `FinalizeIncompleteTools` (Queued/Working → failed with `DysonSessionRecoveryService.IncompleteToolReason`), set `InterruptionReason = application-restart`, stamp `CompletedUtc`, upsert the turn, and append a `TurnInterrupted` log when one is not already present. Existing `AssistantText` / `Instruction` are kept. No model or tool call is replayed, no new turn is added, and no `AgentReply` is written.
3. Scan `ListActiveDescendantSessionsAsync` and mark each still-`Active` child/grandchild `Interrupted` (`SessionStatusChanged` with reason `application-restart` + `UpdateSessionMetaAsync`). Already-terminal descendants are left alone. Roots stay `Active`.
4. For each descendant just interrupted, append one parent `Interrupt` log (`SubagentFailed`, summary `application restart`, child’s `RuntimeId` / persistence id). Same shape as a live failed `SubmitSubagentReport` so the parent is not stranded. Idempotent: skip when that child already has a `SubagentFailed` interrupt on the parent. This is a **persisted log**, not a live `SubmitSyntheticReportAsync` (no session graph exists yet). Does not replay model or tool calls.

The sweep is idempotent. Counts land on `DysonSessionRecoveryReport` (`UnfinishedSessions`, `TurnsRepaired`, `DescendantsInterrupted`).

## Subagents

Parent FK (`ParentSessionId`) links the graph. `CreateChildAsync` persists the child with `ParentSessionId = parent.PersistenceId`, allocates runtime id ≥ 1, optionally seeds the child todo list (`ReplaceTodosAsync` / in-memory hydrate from optional `initialTodos`), and starts a background prompt. Classic children inherit the parent’s `WorktreeEnabled` **and** path/branch (same checkout) and never call `DysonSessionWorktree.Ensure`. A Meta Agent Drone with `useWorktree` true forks its own checkout (`Ensure`) and merges on a completed report. `useWorktree` false stays on the registered checkout and does not set worktree columns, unless `existingWorktreePath` rebinds the child work directory to an already-listed checkout (still no worktree columns, still no `Ensure`). `LastReportSummary` is in-memory on the live session (not a column); `ReadMetaAgentDroneLog` / `InspectSubagentLog` read `_logLines`, also in-memory and empty after restart even though the child session row rehydrates. Child status updates via `UpdateSessionMetaAsync` on `SubmitSubagentReport` / stop / fail. A user halt leaves the child `Stopped` (not `Failed`); the parent still gets a `SubagentFailed` interrupt meaning "done, will not report" — [meta-agent.md](../engine/meta-agent.md)#stop-semantics. A `Completed` or `Failed` child can return to `Active` when a new child turn starts (`BeginInFlightPrompt` — host `ShellExited` / other `PromptHarnessTurnAsync` as well as parent `TriggerSubagentEvent`). `BeginInFlightPrompt` reopen is in-memory (`TryReopenForNewParentTask`). Durable `Active` persist remains `TriggerSubagentEvent` (persist via `UpdateSessionMetaAsync` + `SessionStatusChanged` before kickoff). Do not reopen `Stopped`/`Interrupted`.

Subagents are **session-owned**: the live graph (`SubSessions` / `SubagentsById`) is rebuilt from DB children on parent load (not only from the spawning turn’s tool cards). `ListChildSessionsAsync` returns direct children ordered by `RuntimeId`. Grandchildren hydrate when that child session is opened.

`ListSessionsAsync(..., rootsOnly: true)` (default) hides children from the sidebar; the repository query still returns Meta Agent roots. `DysonUiHost.ListSessionsAsync` omits those roots (find-or-create uses an unfiltered lookup, not the sidebar list). Drill-in is UI navigation only (`NavigateToSessionAsync` / `NavigateToParentAsync`). Root resume loads root turns fully; live host keeps parent+children in a session registry so focus switches do not dispose running children.

Orchestrator policy (engine soft gates + prompts): Plan banned as subagent; Explore never spawns; Drone may spawn Explore only; Meta Agent → Meta Agent Drone or Explore; Meta Agent Drone → Explore or classic Drone (no nested Meta Agent Drone; those Drones are coordinated workers in its worktree and get `MetaAgentDroneWorkerFirstTurnMandate`); at most `MaxRunningWorktreeDrones` (3) live `useWorktree: true` drones per root Meta Agent; Work/Drone always Wait on Explore they start, never Wait on Drones, Plan still Wait-if-blocker; completion via `SubmitSubagentReport` → parent interrupt → host FIFO `SubagentReportProcessing` auto-turn unless that child’s completion was consumed by a successful `WaitForSubagent` this cycle (timeout/cancel does not consume; siblings still notify). Plan still buffers non-consumed completions until BeginBuildPlan or mode leave; Wait-consumed reports do not resurrect there. See [engine README](../engine/README.md)#orchestrator-subagents.

## Live write hooks

While a session runs, concrete session / UI host should:

1. `CreateSession` at start
2. On `AddTurn` → `UpsertTurn` + `TurnStarted` log
3. On each `ToolCallStatusChanged` → matching tool log + update turn `ToolStateJson`
4. On agent reply / title parse → `AgentReply` + turn update
5. On `AppendLog` → `LogLine`
6. On optimize → `ContextOptimized` + turn flags
