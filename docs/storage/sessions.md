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
| `MaxTargetContextTokens` | Session max target context; null = inherit slug `DefaultMaxTargetContextTokens` / harness 100K; `0` = Off (no DropContext inject) |
| `WorkDirectoryId` | Guid? FK to `work_directories` (`SetNull` on delete; required for new sessions) |
| `McpAccessMode` | enum |
| `Status` | `Active`=0 / `Completed`=1 / `Stopped`=2 / `Failed`=3 / `Interrupted`=4 (append-only; child terminal after process-restart recovery; roots stay `Active`) |
| `Title` | Optional UI title (agent `RenameSession` / first prompt); mirrored live as `DysonAgentSession.DisplayTitle` |
| `SystemPromptSnapshot` | Prompt at create time; updated on mid-session `ApplyAgentMode` |
| `CreatedUtc`, `UpdatedUtc`, `LastActivityUtc` | `DateTime` UTC |

Live session: `DysonAgentSession.PersistenceId` ↔ `sessions.Id`. Work directories: [work-directories.md](work-directories.md). Child rows (`turns`, `session_logs`, `session_todos`) stay parent-scoped (no redundant `SubjectId`). Repository visibility: current subject only; cross-subject get-by-id → error.

### `turns`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK — also `DysonAgentTurn.Id` |
| `SessionId` | Guid FK |
| `Sequence` | Order within session |
| `Kind` | `DysonAgentTurnKind` (`PlanResult` = 6, `BeginBuildPlan` = 7, `SubagentReportProcessing` = 8, `ShellExited` = 9, `RethinkToolUsage` = 10, `DisplayInfo` = 11 — UI-only chrome, omitted from provider transcripts; `ModeSwitch` = 12 — mode boundary, completed immediately, included in provider transcripts as a short harness user message; modes in `Instruction` as `From→To`; `DropContext` = 13; `TaskEndReflect` = 14; `BugReview` = 15; `FullSummarize` = 16 — agent-authored session summary; after completion earlier turns are excluded from later transcripts) |
| `AgentTitle` | Parsed H1 / plan title |
| `PlanRelativePath` | Workspace-relative plan path for `PlanResult` / `BeginBuildPlan` (e.g. `.dyson/plans/…`); null otherwise |
| `Instruction` | Harness-injected instruction |
| `AssistantText` | Agent body after title |
| `ReasoningText` | Denormalized join of Thought segments only (UI / reload / search; not replayed into transcripts) |
| `ReasoningLogJson` | Ordered Thought + InterimText JSON for thinking history (UI + DB only; omitted from transcripts). Empty/null with legacy `ReasoningText` → restore synthesizes one Thought |
| `SkillsUsedJson` | Skills attached this turn (slash `/skill-` or `LoadSkill`); JSON array of `DysonSkillUsedEntry`. Injected into provider transcripts as separate `[Skill: …]` user messages |
| `UserImagesJson` | User-attached composer images this turn; JSON array of `DysonBinaryAttachment` fields (no `FileId`). Re-emitted as multimodal Completions/Responses parts on restore |
| `ToolStateJson` | Full snapshot of tool calls + results (restore fidelity) |
| `ToolHistoryOptimized` | bool |
| `CompactToolHistory` | string? |
| `CreatedUtc`, `CompletedUtc`? | `DateTime` UTC |
| `IsExcludedFromContext` | bool — when true, omitted from provider transcripts; UI still shows Dropped + Restore |
| `ContextSummary` | string? — when set, provider transcripts emit a compact summary stub instead of the full turn body; UI shows Summarized badge |
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
| `CreatedUtc`, `UpdatedUtc` | `DateTime` UTC |

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
Task<VoidResult<string>> AppendLogAsync(DysonSessionLogEntry entry, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(Guid? workDirectoryId = null, bool rootsOnly = true, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListChildSessionsAsync(Guid parentSessionId, CancellationToken ct = default);
Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionUnfinishedWorkSummary>, string>> ListActiveSessionsWithUnfinishedTurnsAsync(CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListActiveDescendantSessionsAsync(CancellationToken ct = default);
Task<VoidResult<string>> DeleteSessionAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<DysonSessionTodo, string>> CreateTodoAsync(DysonSessionTodoCreateRequest request, CancellationToken ct = default);
Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(DysonSessionTodoUpdateRequest request, CancellationToken ct = default);
Task<VoidResult<string>> DeleteTodoAsync(Guid sessionId, string taskCode, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(Guid sessionId, IReadOnlyList<DysonSessionTodoReplaceItem> items, CancellationToken ct = default);
```

`ListSessionsAsync` optionally filters by `WorkDirectoryId` (within the current subject). `ListChildSessionsAsync` returns direct children of a parent ordered by `RuntimeId`. `DysonSessionCreateRequest` / summaries include `WorkDirectoryId`. `DysonSessionMetaUpdate` can patch status/title/model/effort and, on mid-session mode switch, `AgentMode` + `SystemPromptSnapshot`.

`GetFullSessionAsync` returns session row + all turns (ordered) + all log entries (ordered by `Sequence`) + todos (ordered by `Sequence`).

`ListActiveSessionsWithUnfinishedTurnsAsync` is the subject-filtered recovery scan: current-subject sessions with `Status == Active` that still have at least one turn whose `CompletedUtc` is null. Each row includes unfinished-turn summaries (`TurnId`, `Sequence`, `Kind`, `CreatedUtc`, `InterruptionReason`). Cross-subject rows are never returned. This is not a distributed lease.

`ListActiveDescendantSessionsAsync` is the complementary subject-filtered recovery scan: current-subject sessions with `Status == Active` and a non-null `ParentSessionId` (any depth). Roots are never returned. After a process restart these descendants cannot resume their in-process runtime even when every turn is already complete; recovery marks them `Interrupted`. Cross-subject rows are never returned. Circuit disconnect in a still-running process does not use these scans.

`DeleteSessionAsync` removes the session and descendant subagent sessions (`ParentSessionId` is Restrict, so children are deleted deepest-first). Turns, session logs, and todos cascade. Bulk inactive delete is host-side (`DysonUiHost.DeleteInactiveSessionsAsync` + `DysonSessionInactiveDelete` + existing `DeleteSessionAsync`), not a new repository API. The host overlay protects the current session and busy sessions; idle `Active` leftovers are deletable.

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
OpenAI-compatible path: `OpenAiCompatibleAgentSession.LoadAsync(sessionRepository, sessionId, provider, http, workDirectoryAbsolutePath)` (still host-owned; the runtime factory returns an explicit Result error for OpenAI create/load).

Same-process circuit reconnect is not this cold path: `DysonUiRuntimeAttachment` reattaches the retained `DysonSessionRuntime` and does not rerun recovery or rebuild the live graph from SQLite.

## Process-restart recovery

On the first `DysonSessionRuntimeRegistry.GetOrCreateAsync` for a subject after process start, `DysonSessionRuntime.EnsureRecoveredAsync` runs `DysonSessionRecoveryService` once. Circuit disconnect / host dispose does **not** run this sweep — the same-process runtime is retained.

1. Scan `ListActiveSessionsWithUnfinishedTurnsAsync`.
2. For each turn with null `CompletedUtc`: rehydrate tool state, `FinalizeIncompleteTools` (Queued/Working → failed with `DysonSessionRecoveryService.IncompleteToolReason`), set `InterruptionReason = application-restart`, stamp `CompletedUtc`, upsert the turn, and append a `TurnInterrupted` log when one is not already present. Existing `AssistantText` / `Instruction` are kept. No model or tool call is replayed, no new turn is added, and no `AgentReply` is written.
3. Scan `ListActiveDescendantSessionsAsync` and mark each still-`Active` child/grandchild `Interrupted` (`SessionStatusChanged` with reason `application-restart` + `UpdateSessionMetaAsync`). Already-terminal descendants are left alone. Roots stay `Active`.
4. Do not synthesize a parent `SubmitSubagentReport` or live `Interrupt`.

The sweep is idempotent. Counts land on `DysonSessionRecoveryReport` (`UnfinishedSessions`, `TurnsRepaired`, `DescendantsInterrupted`).

## Subagents

Parent FK (`ParentSessionId`) links the graph. `CreateChildAsync` persists the child with `ParentSessionId = parent.PersistenceId`, allocates runtime id ≥ 1, optionally seeds the child todo list (`ReplaceTodosAsync` / in-memory hydrate from optional `initialTodos`), and starts a background prompt. Child status updates via `UpdateSessionMetaAsync` on `SubmitSubagentReport` / stop / fail.

Subagents are **session-owned**: the live graph (`SubSessions` / `SubagentsById`) is rebuilt from DB children on parent load (not only from the spawning turn’s tool cards). `ListChildSessionsAsync` returns direct children ordered by `RuntimeId`. Grandchildren hydrate when that child session is opened.

`ListSessionsAsync(..., rootsOnly: true)` (default) hides children from the sidebar; drill-in is UI navigation only (`NavigateToSessionAsync` / `NavigateToParentAsync`). Root resume loads root turns fully; live host keeps parent+children in a session registry so focus switches do not dispose running children.

Orchestrator policy (engine soft gates + prompts): Plan banned as subagent; Explore never spawns; Drone may spawn Explore only; Work/Drone always Wait on Explore they start, never Wait on Drones, Plan still Wait-if-blocker; completion via `SubmitSubagentReport` → parent interrupt → host FIFO `SubagentReportProcessing` auto-turn (buffered in Plan until BeginBuildPlan or mode leave). See [engine README](../engine/README.md)#orchestrator-subagents.

## Live write hooks

While a session runs, concrete session / UI host should:

1. `CreateSession` at start
2. On `AddTurn` → `UpsertTurn` + `TurnStarted` log
3. On each `ToolCallStatusChanged` → matching tool log + update turn `ToolStateJson`
4. On agent reply / title parse → `AgentReply` + turn update
5. On `AppendLog` → `LogLine`
6. On optimize → `ContextOptimized` + turn flags
