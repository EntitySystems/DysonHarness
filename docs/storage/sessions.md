# Sessions, turns & session log

Durable session state lives in the same EF Core SQLite DB as model providers/slugs ([models.md](models.md)). **Turns** are the resume source of truth; the **session log** is an append-only audit / UI timeline.

## Schema

### `sessions`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK — **persistence id** (distinct from runtime int `DysonAgentSession.Id`) |
| `RuntimeId` | Root `0` / subagent ≥ `1` |
| `ParentSessionId` | Guid? FK to parent persisted session |
| `AgentMode` | Ask / Plan / Work / … |
| `ModelSlugId` | Guid? FK to `model_slugs` (credentials via parent provider) |
| `WorkDirectoryId` | Guid? FK to `work_directories` (`SetNull` on delete; required for new sessions) |
| `McpAccessMode` | enum |
| `Status` | `Active` / `Completed` / `Stopped` / `Failed` |
| `Title` | Optional UI title (agent `RenameSession` / first prompt); mirrored live as `DysonAgentSession.DisplayTitle` |
| `SystemPromptSnapshot` | Prompt at create time |
| `CreatedUtc`, `UpdatedUtc`, `LastActivityUtc` | `DateTime` UTC |

Live session: `DysonAgentSession.PersistenceId` ↔ `sessions.Id`. Work directories: [work-directories.md](work-directories.md).

### `turns`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK — also `DysonAgentTurn.Id` |
| `SessionId` | Guid FK |
| `Sequence` | Order within session |
| `Kind` | `DysonAgentTurnKind` |
| `AgentTitle` | Parsed H1 |
| `Instruction` | Harness-injected instruction |
| `AssistantText` | Agent body after title |
| `ToolStateJson` | Full snapshot of tool calls + results (restore fidelity) |
| `ToolHistoryOptimized` | bool |
| `CompactToolHistory` | string? |
| `CreatedUtc`, `CompletedUtc`? | `DateTime` UTC |

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

Runtime mirror: `DysonSessionTodo` (same fields; `Comments` as `IReadOnlyList<string>`). Self-check: `DysonSessionTodoSelfCheck.Run()`.

### `session_logs` (discriminated JSON)

Append-only. Filter by `Kind`; payload fields live in `PayloadJson`.

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
| `SessionStatusChanged` | `{ "status", … }` |
| `SessionRenamed` | `{ "title" }` (from `RenameSession` / `RenameAsync`) |
| `UserPrompt` | `{ "prompt", "filePaths"? }` |
| `TurnStarted` / `TurnCompleted` | `{ "turnId", "kind", "agentTitle"? }` |
| `AgentReply` | `{ "turnId", "title", "body" }` |
| `ToolCallQueued` / `ToolCallWorking` / `ToolCallCompleted` / `ToolCallFailed` | `{ "turnId", "callId", "toolName", "stage", "argumentsJson"?, "resultContent"?, "isError"? }` |
| `Interrupt` | `{ "interruptKind", "subagentId", "summary"? }` |
| `ContextOptimized` | `{ "turnsCompacted", "tokenEstimate"? }` |
| `LogLine` | `{ "line" }` (from `AppendLog`) |
| `CompletionFlow` | `{ "phase": "CompleteTask"\|"Confirm"\|"Continue"\|"ReportSummary", … }` |

Use small sealed records per kind plus a type-discriminator helper (`DysonSessionLogPayload`). Store `Kind` for SQL filtering; JSON carries the fields.

## `DysonSessionStore` API

Result-pattern concrete store:

```csharp
Task<Result<Guid, string>> CreateSessionAsync(DysonSessionCreateRequest request, CancellationToken ct = default);
Task<VoidResult<string>> UpdateSessionMetaAsync(...);
Task<VoidResult<string>> UpsertTurnAsync(DysonTurnEntity turn, CancellationToken ct = default);
Task<VoidResult<string>> AppendLogAsync(DysonSessionLogEntry entry, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(Guid? workDirectoryId = null, bool rootsOnly = true, CancellationToken ct = default);
Task<Result<DysonPersistedSession, string>> GetFullSessionAsync(Guid sessionId, CancellationToken ct = default);
Task<VoidResult<string>> DeleteSessionAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(Guid sessionId, CancellationToken ct = default);
Task<Result<DysonSessionTodo, string>> CreateTodoAsync(DysonSessionTodoCreateRequest request, CancellationToken ct = default);
Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(DysonSessionTodoUpdateRequest request, CancellationToken ct = default);
Task<VoidResult<string>> DeleteTodoAsync(Guid sessionId, string taskCode, CancellationToken ct = default);
Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(Guid sessionId, IReadOnlyList<DysonSessionTodoReplaceItem> items, CancellationToken ct = default);
```

`ListSessionsAsync` optionally filters by `WorkDirectoryId`. `DysonSessionCreateRequest` / summaries include `WorkDirectoryId`.

`GetFullSessionAsync` returns session row + all turns (ordered) + all log entries (ordered by `Sequence`) + todos (ordered by `Sequence`).

`DeleteSessionAsync` removes the session and descendant subagent sessions (`ParentSessionId` is Restrict, so children are deleted deepest-first). Turns, session logs, and todos cascade.

Todo CRUD rejects duplicate `TaskCode` (create) / missing code (update/delete). `UpdateTodoAsync` patches optional `DisplayName` / `Status`; `Comments` replaces the full list; `AppendComment` appends one string after any replace. `ReplaceTodosAsync` clears then inserts the seed set (used by `StartSubagent` child seed); duplicate codes in the set fail.

### `DysonPersistedSession`

Aggregate DTO: session entity + `IReadOnlyList` turns + `IReadOnlyList` log entries + `IReadOnlyList` todos.

## Resume

1. `GetFullSessionAsync(sessionId)`
2. Construct concrete session with ephemeral provider (from selected model slug + parent provider)
3. `RestoreFromPersisted(state)` — sets `PersistenceId`, rebuilds `TurnHistory` from turn rows (`ToolStateJson` → tool calls / tracked / response log), restores todos via `RestoreTodos`, restores mode/config snapshots as applicable
4. Append `SessionResumed` log
5. Session is ready for further `PromptAsync`

Demo path: `DemoDysonAgentSession.LoadAsync(store, sessionId, provider)`.
OpenAI-compatible path: `OpenAiCompatibleAgentSession.LoadAsync(store, sessionId, provider, http, workDirectoryAbsolutePath)`.

### Subagents

Parent FK (`ParentSessionId`) links the graph. `CreateChildAsync` persists the child with `ParentSessionId = parent.PersistenceId`, allocates runtime id ≥ 1, optionally seeds the child todo list (`ReplaceTodosAsync` / in-memory hydrate from optional `initialTodos`), and starts a background prompt. Child status updates via `UpdateSessionMetaAsync` on `SubmitSubagentReport` / stop / fail.

`ListSessionsAsync(..., rootsOnly: true)` (default) hides children from the sidebar; drill-in is UI navigation only (`NavigateToSessionAsync` / `NavigateToParentAsync`). Root resume loads root turns fully; live host keeps parent+children in a session registry so focus switches do not dispose running children.

Orchestrator policy (engine soft gates + prompts): Plan banned as subagent; Explore never spawns; Drone may spawn Explore only; Wait only for prerequisites; completion via `SubmitSubagentReport` → parent interrupt → host FIFO auto-turn. See [engine README](../engine/README.md)#orchestrator-subagents.

## Live write hooks

While a session runs, concrete session / UI host should:

1. `CreateSession` at start
2. On `AddTurn` → `UpsertTurn` + `TurnStarted` log
3. On each `ToolCallStatusChanged` → matching tool log + update turn `ToolStateJson`
4. On agent reply / title parse → `AgentReply` + turn update
5. On `AppendLog` → `LogLine`
6. On optimize → `ContextOptimized` + turn flags
