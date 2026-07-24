# Engine

Library: [`src/Harness/Harness.Engine`](../../src/Harness/Harness.Engine) (`net10.0`, namespace `DysonHarness`).

The engine is an abstract agent harness: `DysonEngine` exposes a root `DysonAgentSession`; sessions talk to an ephemeral `DysonAgentProvider` and run staged MCP-shaped tool calls. There is no concrete host in the engine itself — UI and demo hosts live elsewhere.

For bindable public types, see [api-surface.md](api-surface.md). Persistence is covered under [docs/storage](../storage/models.md).

## Session loop

A concrete session implements:

| Method | Role |
| ------ | ---- |
| `LoadFunctionalContextAsync` | Load workspace / functional context before work |
| `PromptAsync` | User (or harness) prompt; optional file paths |
| `WaitForNotifyAsync` | Async notify events (prefer draining interrupts) |

Typical flow: construct session with mode + config + provider → load context → prompt → model replies (H1 title + body) and/or queues tool calls → `DysonToolCallScheduler.RunStagedAsync` runs stages → optional expand / completion turns → `OptimizeContextIfNeeded` before the next provider request.

### OpenAI-compatible provider

When `ProviderKind == OpenAICompatible`, the host builds `OpenAiCompatibleAgentProvider` + `OpenAiCompatibleAgentSession` (engine). Demo kind stays on `DemoDysonAgentSession`.

- **API mode** (`OpenAiApiMode` on the provider entity): `Completions` (default) → `POST …/chat/completions`; `Responses` → `POST …/responses`.
- **Streaming SSE** (`stream: true`) for assistant text; Completions reads `choices[0].delta.content` (+ incremental `tool_calls`, `stream_options.include_usage`); Responses handles `response.output_text.delta`, function-call assembly (`output_item.added` / `function_call_arguments.delta|done` / `output_item.done`), and `error` / `response.failed`. Session consumes chunks per tool-loop round; `AssistantText` + H1 title parse only on the final no-tool round (preview stays raw until then). Cancel/error clears `StreamingPreview`.
- **Native function tools** with required harness `stage` on every schema.
- **Tool loop** inside one `PromptAsync` (cap ~20 rounds): model tool_calls → staged executor → feed results → call again.
- **Executors (v1):** `DysonWorkspaceToolExecutor` — real `RenameSession`, workdir-scoped file tools (`ReadFile`, `CreateFile`, `WriteFile`, `Grep`, `ListDirectory`, `CreateDirectory`), and `ShellExecute` (session-available shells via `DysonShell`); other catalog tools return “not implemented yet”.
- **RenameSession review:** every 8 turns (1-based indices **1, 9, 17, …** — when `TurnHistory.Count % 8 == 0` before adding the turn), the transcript builder appends an ephemeral yes/no `RenameSessionReviewMandate` on the **current incomplete** user message only. Turn 1 is `InitializeSession` via `DysonSessionInitialization.CreateTurn`; later review turns stay `Normal`. Completed/history turns always send clean `Instruction` — the mandate is never re-emitted. Soft every-turn rename nudges are not in system prompts; MCP description says rename only on harness review mandate or explicit user request.
- **Cache-friendly requests** (`OpenAiCacheFriendlyTranscriptBuilder`):
  1. Stable prefix first: system/instructions (mode prompt + MCP catalog) → `tools[]` (stable sort) → prior transcript → new user/tool deltas last.
  2. Never mutate an already-sent/optimized prefix (`OptimizeContextIfNeeded` before building).
  3. `prompt_cache_key` = `dyson:{PersistenceId}` on every call (send-first).
  4. GPT-5.6+ only: optional `prompt_cache_options.mode=explicit` + breakpoint on the system prefix when the slug looks like `gpt-5.6+`.
  5. Completions always sends full local `messages[]`. Responses rebuilds full `input` after compaction / new user turns (`store: false`); within a tool loop may chain `previous_response_id` + `function_call_output` (`store: true` for that hop).
  6. User content for history turns is always `Instruction` only; rename-review mandate is appended only for the in-flight review turn.

Root sessions have runtime `Id = 0`. Subagents get ids ≥ 1 via `RegisterSubagent`.

## Agent modes

Built-in names in `DysonAgentModes`:

| Mode | Intent |
| ---- | ------ |
| `Ask` | Q&A without heavy mutation |
| `Plan` | Planning / design |
| `Work` | Primary work loop |
| `Explore` | Codebase exploration |
| `Drone` | Delegated subagent work |
| `Security Review` | Security-focused review |
| `Bug Review` | Bug-focused review |
| `Custom` | Category label; lookup uses `Config.CustomAgents` keys |

System prompts come from `DysonAgentSystemPrompts.ForMode`.

## MCP access

`DysonMcpAccessMode` on `DysonAgentSessionConfig`:

- **FullAccess** — tools run with full access; no allowlist.
- **AutoReview** — calls route through in-process `DysonMcpAutoReviewProxy`; no allowlist.

`DysonMcpPipeline` holds the per-session tool catalog (`FormatToolsForPrompt`) and optional auto-review proxy. OpenAI-compatible sessions also expose the same tools as native function schemas (with required `stage`). Live remote MCP servers remain out of scope; workspace file tools and `ShellExecute` run locally via `DysonWorkspaceToolExecutor`.

Default tools include subagent control (`StartSubagent`, `WaitForSubagent`, …), task completion (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), workspace file tools, **`ShellExecute`** (when the platform has available shells), and related harness tools. Every call carries harness fields: optional `callId`, required `stage` (int).

### ShellExecute

- Session config `AvailableShellTypes` defaults from `DysonShell.AvailableForCurrentPlatform()` (Windows: `Pwsh`, `PowerShell`, `Cmd`; other platforms: none yet).
- MCP schema `shell` enum + description list those types; the model must pass `shell` plus `command` (optional `timeoutMs`, `workingDirectory` under the work root).
- Executor rejects shells outside the session list, then `DysonShell.Create` → `DysonWindowsShell` (Windows arg map: `pwsh`/`powershell.exe` `-NoProfile -NonInteractive -Command`, `cmd.exe` `/d /c`).
- Abstraction: `DysonShellType`, abstract `DysonShell` (`ShellType` get + `ExecuteAsync`), `DysonShellRunResult`, `DysonShell.Create` / `AvailableForCurrentPlatform`.

## Staged tool calls

`DysonToolCall.Stage` orders execution:

1. Same-stage calls run **concurrently**.
2. Ascending stage order is a **barrier** between groups.
3. Status: `Queued` → `Working` → `Completed` | `Failed`.
4. UI binds `DysonAgentTurn.ToolCallStatusChanged` and `TrackedToolCalls`.

`DysonToolCallScheduler.RunStagedAsync` drives this; results append to `ResponseLog`.

## Interrupts

Parent sessions observe subagents via `DysonAgentInterrupt` (`SubagentCompleted` / `SubagentStopped` / `SubagentFailed`).

- `EnqueueInterrupt` / `TryDequeueInterrupt` / `WaitForInterruptAsync`
- Concrete `WaitForNotifyAsync` should drain the interrupt queue so Work does not busy-poll

## Task completion flow

After the model calls `CompleteTask`:

1. **Confirm** — `TaskCompletionConfirm` turn (`ConfirmTaskComplete` or `ContinueWork`)
2. **Continue** — `Continuation` turn if work remains
3. **Report** — `ReportSummary` turn after confirm (final handoff)

Factories: `DysonTaskCompletionFlow` and session helpers `CreateCompletionConfirmTurn` / `CreateContinuationTurn` / `CreateReportSummaryTurn`.

## Expand thought process

`DysonExpandThoughtProcess` / `CreateExpandThoughtProcessTurn` inserts an `ExpandThoughtProcess` turn so the agent reformulates before heavy work continues.

## Context optimizer

`DysonContextOptimizer` (code-generated compaction, no LLM):

- Triggers on turn count or unoptimized token size (`IDysonTokenCounter`, default Tiktoken).
- Compacts **older** turns only (`KeepRecentTurns`); sets `ToolHistoryOptimized` + `CompactToolHistory` for prompt-cache stability.
- Call `OptimizeContextIfNeeded` before building the next provider request.

## Result pattern

Public expected-failure paths return `Result<TValue, TError>`, `VoidResult<TError>`, or `ValueResult<TValue>` — see [rules/rules_csharp.md](../../rules/rules_csharp.md). Do not use exceptions for ordinary control flow.
