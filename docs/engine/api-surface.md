# Engine API surface

Public types in `Harness.Engine` that hosts and UI typically bind to. Namespace: `DysonHarness`.

Conceptual overview: [README.md](README.md).

## Core host

| Type | Notes |
| ---- | ----- |
| `DysonEngine` | Abstract; exposes `RootSession` |
| `DysonAgentSession` | Abstract session: mode, prompt, MCP pipeline, subagents, interrupts, log, turns, optimizer hooks |
| `DysonAgentProvider` | Abstract ephemeral model provider (no durable state) |
| `DysonAgentSessionConfig` | `CustomAgents`, `McpAccessMode` |
| `DysonAgentSessionEvent` | Abstract notify payload for `WaitForNotifyAsync` |

### Session members (high level)

- Identity: `Id` (runtime int; root `0`)
- Persistence (when wired): `PersistenceId` (`Guid`), `DisplayTitle`, `Turns`, `TurnAdded`, `AddTurn`, `RestoreFromPersisted`
- Rename: `RenameAsync(title)` → validates (trim, max 120) → sets `DisplayTitle` → raises `SessionRenamed` (`DysonSessionRenamedEventArgs`: `PersistenceId`, `Title`); host/tool executor persists `sessions.Title`
- Config / mode: `Config`, `Mode`, `SystemPrompt`, `McpPipeline`, `Provider`
- Subagents: `SubSessions`, `RegisterSubagent`
- Interrupts: `EnqueueInterrupt`, `TryDequeueInterrupt`, `WaitForInterruptAsync`
- Log: `AppendLog`, `SnapshotLog`, `LogAppended`
- Turns / context: `CreateExpandThoughtProcessTurn`, completion-turn helpers, `OptimizeContextIfNeeded`
- Loop: `LoadFunctionalContextAsync`, `PromptAsync`, `WaitForNotifyAsync`

## Modes & prompts

| Type | Notes |
| ---- | ----- |
| `DysonAgentModes` | Built-in mode name constants |
| `DysonProviderKinds` | Known provider-kind strings (`demo`, `OpenAICompatible`, `Anthropic`) |
| `DysonAgentSystemPrompts` | `ForMode` → system prompt text |

## Turns & tools

| Type | Notes |
| ---- | ----- |
| `DysonAgentTurn` | Turn kind, instruction, agent title, tool calls, tracked status, response log, compact history |
| `DysonAgentTurnKind` | `Normal`, `ExpandThoughtProcess`, `TaskCompletionConfirm`, `Continuation`, `ReportSummary` |
| `DysonToolCall` | `CallId`, `ToolName`, `Stage`, `ArgumentsJson` |
| `DysonToolCallStatus` | `Queued`, `Working`, `Completed`, `Failed` |
| `DysonTrackedToolCall` | Live status + result for UI rows |
| `DysonToolCallResult` | Completed/failed payload (`IsError`, `Content`, …) |
| `DysonToolCallStatusChangedEventArgs` | Previous/new status + tracked row |
| `DysonToolCallScheduler` | `RunStagedAsync` — concurrent same-stage, barrier across stages |

`DysonAgentTurn.TryParseAgentTitle` requires agent replies to start with a Markdown H1.

## MCP

| Type | Notes |
| ---- | ----- |
| `DysonMcpAccessMode` | `FullAccess`, `AutoReview` |
| `DysonMcpPipeline` | Tool catalog + optional auto-review proxy |
| `DysonMcpTool` | Name, description, input schema JSON |
| `DysonMcpAutoReviewProxy` | In-process review gate when mode is AutoReview |

Default catalog includes session tools (`StartSubagent`, `WaitForSubagent`, …), completion tools, workspace file tools, and **`RenameSession`** (`{ "title": string }` required) for UI/list titles.

## Interrupts & completion

| Type | Notes |
| ---- | ----- |
| `DysonAgentInterrupt` | Kind, subagent id, optional summary |
| `DysonAgentInterruptKind` | `SubagentCompleted`, `SubagentStopped`, `SubagentFailed` |
| `DysonSubagentInterruptEvent` | Session-event shape for subagent interrupts |
| `DysonExpandThoughtProcess` | Expand-thought turn factory |
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
- `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonWorkDirectoryStore`
- `DysonModelProviderEntity`, `DysonModelSlugEntity` (providers own `ApiKey` / `BaseUrl` / `ProviderKind`; slugs own `Slug` + `DisplayAlias`)
- `DysonWorkDirectoryEntity`, `DysonNativeFolderPicker`, `DysonGitInfo`
- Session/turn/log entities and `DysonPersistedSession` (sessions reference `ModelSlugId` + optional `WorkDirectoryId`)
