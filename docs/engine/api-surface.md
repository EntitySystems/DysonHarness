# Engine API surface

Public types in `Harness.Engine` that hosts and UI typically bind to. Namespace: `DysonHarness`.

Conceptual overview: [README.md](README.md).

## Core host

| Type | Notes |
| ---- | ----- |
| `DysonEngine` | Abstract; exposes `RootSession` |
| `DysonAgentSession` | Abstract session: mode, prompt, MCP pipeline, subagents, interrupts, log, turns, optimizer hooks |
| `DysonAgentProvider` | Abstract ephemeral model provider (no durable state) |
| `OpenAiCompatibleAgentProvider` | OpenAI-compatible ephemeral provider (`BaseUrl`, `ApiKey`, `Slug`, `OpenAiApiMode`, …) |
| `OpenAiCompatibleAgentSession` | Completions/Responses tool-loop session |
| `OpenAiCompletionsClient` / `OpenAiResponsesClient` | Streaming SSE adapters (`StreamCreateAsync` → `OpenAiStreamChunk`) |
| `OpenAiCacheFriendlyTranscriptBuilder` | Stable-prefix transcript + `prompt_cache_key` |
| `DysonWorkspaceToolExecutor` | Workdir-scoped file tools + `RenameSession` + `GetDateTime` + `ShellExecute` + web search/fetch tools (tool-owned summarize) + **subagent tools** (`StartSubagent` / `WaitForSubagent` / `InspectSubagentLog` / `StopSubagent` / `SubmitSubagentReport`) + **session todo tools** (`ListTodos` / `CreateTodo` / `UpdateTodo` / `DeleteTodo`); stubs for the rest |
| `DysonShell` / `DysonWindowsShell` | Shell runners (`ShellType` get); Windows: Pwsh / PowerShell / Cmd |
| `DysonShellType` / `DysonShellRunResult` | Shell enum + process result |
| `DysonOpenAiApiModes` | `Completions` / `Responses` constants |
| `DysonAgentSessionConfig` | `CustomAgents`, `McpAccessMode`, `AvailableShellTypes`, optional `BraveApiKey`, optional `SummarizerProvider` |
| `DysonAgentSessionEvent` | Abstract notify payload for `WaitForNotifyAsync` |

### Session members (high level)

- Identity: `Id` (runtime int; root `0`)
- Persistence (when wired): `PersistenceId` (`Guid`), `DisplayTitle`, `Turns`, `TurnAdded`, `AddTurn`, `RestoreFromPersisted`
- Todos: `Todos` (`IReadOnlyList<DysonSessionTodo>`), `TodosChanged`, `RestoreTodos`, `ListTodosAsync` / `CreateTodoAsync` / `UpdateTodoAsync` / `DeleteTodoAsync` / `ReplaceTodosAsync` (persist when `PersistenceId` set)
- Rename: `RenameAsync(title)` → validates (trim, max 120) → sets `DisplayTitle` → raises `SessionRenamed` (`DysonSessionRenamedEventArgs`: `PersistenceId`, `Title`); host/tool executor persists `sessions.Title`
- Config / mode: `Config`, `Mode`, `SystemPrompt`, `McpPipeline`, `Provider`
- Subagents: `Parent`, `SubSessions`, `RegisterSubagent`, `CreateChildAsync` (optional `initialTodos` seed), `WaitForSubagentAsync`, `InspectSubagentLog` (sync), `StopSubagentAsync`, `SubmitSubagentReportAsync` (`skipTasksCheck` gates incomplete session todos), `ValidateSubagentSpawn`
- Interrupts: `EnqueueInterrupt`, `TryDequeueInterrupt`, `WaitForInterruptAsync`; `NotifySubagentCompleted` / `Stopped` / `Failed` (include optional child `PersistenceId`)
- Log: `AppendLog`, `SnapshotLog`, `LogAppended`
- Turns / context: `CreateExpandThoughtProcessTurn`, completion-turn helpers, `OptimizeContextIfNeeded`
- Loop: `LoadFunctionalContextAsync`, `PromptAsync`, `WaitForNotifyAsync`

## Modes & prompts

| Type | Notes |
| ---- | ----- |
| `DysonAgentModes` | Built-in mode name constants (`Plan` top-level only) |
| `DysonProviderKinds` | Known provider-kind strings (`demo`, `OpenAICompatible`, `Anthropic`) |
| `DysonOpenAiApiModes` | OpenAICompatible API surface (`Completions` default, `Responses`) |
| `DysonAgentSystemPrompts` | `ForMode` → system prompt text; Work/Explore/Drone orchestrator directives; `DroneFirstTurnContextMandate` |
| `DysonStartSubagentResult` | StartSubagent / `CreateChildAsync` return: `SubagentId`, `PersistenceId`, `AgentMode`, `Title` |
| `DysonSubagentSpawnGateSelfCheck` | Assert-only soft spawn gate checks (Work→Explore/Drone ok; Plan banned; Explore cannot spawn; Drone→Explore only) |
| `DysonSessionTodo` | Runtime/UI/MCP mirror of a session todo (`TaskCode`, `DisplayName`, `Status`, `Comments`, `Sequence`, timestamps) |
| `DysonSessionTodoStatus` | `Pending` / `Ongoing` / `Complete` (ints 0/1/2) |
| `DysonSessionTodoSelfCheck` | Assert-only TaskCode uniqueness + status enum round-trip |

## Turns & tools

| Type | Notes |
| ---- | ----- |
| `DysonAgentTurn` | Turn kind, instruction, agent title, `AssistantText`, `StartedUtc` / `CompletedUtc` (UI chrome + persistence; not in model transcript), live `StreamingPreview`/`IsStreaming`/`AssistantTextChanged`, tool calls, tracked status, response log, compact history |
| `DysonAgentTurnKind` | `Normal`, `ExpandThoughtProcess`, `TaskCompletionConfirm`, `Continuation`, `ReportSummary`, `InitializeSession` |
| `DysonSessionInitialization` | First-turn factory (`CreateTurn` → `InitializeSession`); `RenameSessionReviewMandate` + `IsRenameReviewTurn` (every 8 turns: 1, 9, 17, …; mandate appended only for incomplete current turn) |
| `DysonToolCall` | `CallId`, `ToolName`, `Stage`, `ArgumentsJson` |
| `DysonToolCallStatus` | `Queued`, `Working`, `Completed`, `Failed` |
| `DysonTrackedToolCall` | Live status + result for UI rows |
| `DysonToolCallResult` | Completed/failed payload (`IsError`, `Content`, …) |
| `DysonToolCallStatusChangedEventArgs` | Previous/new status + tracked row |
| `DysonToolCallScheduler` | `RunStagedAsync` — concurrent same-stage, barrier across stages; multi-round Queued-only runs |

`DysonAgentTurn.TryParseAgentTitle` requires agent replies to start with a Markdown H1. `PrepareAdditionalTrackedCalls` supports multi-round tool loops on one turn.

## MCP

| Type | Notes |
| ---- | ----- |
| `DysonMcpAccessMode` | `FullAccess`, `AutoReview` |
| `DysonMcpPipeline` | Tool catalog + optional auto-review proxy |
| `DysonMcpTool` | Name, description, input schema JSON |
| `DysonMcpAutoReviewProxy` | In-process review gate when mode is AutoReview |

Default catalog includes session tools (`StartSubagent`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`), **session todos** (`ListTodos`, `CreateTodo`, `UpdateTodo`, `DeleteTodo`), completion tools, workspace file tools, **`RenameSession`** (`{ "title": string }` required) for UI/list titles, **`GetDateTime`** (optional `timezone`: `"utc"` default | `"local"`; returns ISO + `dd/MM/yyyy HH:mm` display), **`ShellExecute`** (`shell` enum from session `AvailableShellTypes`, `command`, optional `timeoutMs` / `workingDirectory`) when shells are available, and **web search/fetch** tools: `FreeSearch`, `FreeSearchAdvanced`, `SearchWithSynthesis`, `FreeExtract`, `WebFetch`, `FetchGithubReadme` (see [README.md](README.md)#web-search--fetch-in-process). Call `RenameSession` only when the harness every-8 rename-review mandate asks, or when the user explicitly requests a rename. `DysonMcpPipeline.CreateDefault(accessMode, availableShellTypes)` builds the dynamic ShellExecute schema.

**Session todo tools:** operate on the current session’s list only (root and subagent each own a list). Status strings: `pending` / `ongoing` / `complete`. `CreateTodo` requires `displayName` + `taskCode` (unique per session); optional `status`, `comments`. `UpdateTodo` requires `taskCode`; optional patch `displayName` / `status`; `comments` replaces the full list; `appendComment` appends one. No comment-delete tool. `DeleteTodo` / `ListTodos` by current session.

**Subagent tools (see [README.md](README.md)#orchestrator-subagents):** `StartSubagent` is non-blocking (`agentMode` + `task`, optional `context`, optional `todos` seed array with `displayName` / `taskCode` / optional `status` / `comments`; Plan banned; Explore parents cannot spawn; Drone→Explore only). `WaitForSubagent` blocks for prerequisites only. `SubmitSubagentReport` (`summary`, optional `status`, optional `skipTasksCheck`) is the child handoff that drives parent interrupts / host auto-turn. By default it **errors** (session stays non-terminal) when any session todo is still `Pending` or `Ongoing`; pass `skipTasksCheck: true` to override — success JSON then includes `incompleteTodos` (`taskCode`, `displayName`, `status`) and `skipTasksCheck: true`. Parent notification still uses the agent `summary` unchanged. Empty todo list always passes.

## Search (in-process)

| Type | Notes |
| ---- | ----- |
| `SearchOrchestrator` | `FreeSearchAsync` / `FreeSearchAdvancedAsync` / `SearchWithSynthesisAsync` |
| `SearchEngines` | DuckDuckGo HTML (default first), Bing RSS, Wikipedia OpenSearch, optional Brave API; returns `Result` with HTTP/parse errors |
| `SearchFetch` | `WebFetchAsync` (caller supplies `maxBytes`; clamp 1KB–2MB, default **64KB** if null), `FreeExtractAsync`, `FetchGithubReadmeAsync` |
| `SearchHttp` | Shared `HttpClient` (`Api-User-Agent` = DysonHarness) + `ValidateUrl` SSRF guard |
| `SearchAggregation` | Dedup, filter (keeps titled http(s) hits with short snippets), confidence 1–3 scoring, waterfall basket |
| `SearchSelfCheck` | `RunSsrfChecks()` — localhost/private IP block + DDG HTML / Bing RSS parse smoke + summarizer policy (incl. focus prompt) |
| `DysonWebSearchSummarizer` / `DysonWebSearchSummarizerPrompt` | Tool-owned LLM summarize for web tools (`SummarizeAsync` + optional `summarizePrompt`; ≤10K tokens) |
| `SearchHit` / `SearchResponse` / `SearchOptions` / `WebFetchResult` | Search DTOs |

## Interrupts & completion

| Type | Notes |
| ---- | ----- |
| `DysonAgentInterrupt` | Kind, subagent id, optional `PersistenceId`, optional summary |
| `DysonAgentInterruptKind` | `SubagentCompleted`, `SubagentStopped`, `SubagentFailed` |
| `DysonSubagentInterruptEvent` | Session-event shape for subagent interrupts |
| `DysonExpandThoughtProcess` | Expand-thought turn factory |
| `DysonSessionInitialization` | First-prompt turn factory; periodic rename review mandate (ephemeral, not in subsequent history) |
| `DysonTaskCompletionFlow` | Confirm / continuation / report-summary factories |

## Context & tokens

| Type | Notes |
| ---- | ----- |
| `DysonContextOptimizer` | Thresholds + compact older tool history |
| `IDysonTokenCounter` | Token estimate for optimizer |
| `DysonTiktokenTokenCounter` | Default counter |

## Result types

| Type | Notes |
| ---- | ----- |
| `Result<TValue, TError>` | Value or error |
| `VoidResult<TError>` | Side-effect success or error |
| `ValueResult<TValue>` | Success value vs error flag |
| `DebugCodes` | Optional debug code on error results |

## Persistence-facing types

Documented under [docs/storage](../storage/models.md), [sessions.md](../storage/sessions.md), and [work-directories.md](../storage/work-directories.md):

- `DysonAppMode`, `DysonAppPaths`, `DysonBuildInfo`
- `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonWorkDirectoryStore`, `DysonAppSettingsStore`
- `DysonModelProviderEntity`, `DysonModelSlugEntity` (providers own `ApiKey` / `BaseUrl` / `ProviderKind`; slugs own `Slug` + `DisplayAlias`)
- `DysonAppSettingEntity` / `DysonAppSettingKeys` (key/value prefs, e.g. web search summarizer slug)
- `DysonWorkDirectoryEntity`, `DysonNativeFolderPicker`, `DysonGitInfo`
- Session/turn/log entities and `DysonPersistedSession` (sessions reference `ModelSlugId` + optional `WorkDirectoryId`; aggregate includes todos)
- `DysonSessionTodoEntity` / `DysonSessionTodo` / `DysonSessionTodoStatus` / todo request DTOs on `DysonSessionStore`
