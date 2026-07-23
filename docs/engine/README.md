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

`DysonMcpPipeline` holds the per-session tool catalog (`FormatToolsForPrompt`) and optional auto-review proxy. Live remote MCP execution is out of scope for the library core; catalog + staging are in place.

Default tools include subagent control (`StartSubagent`, `WaitForSubagent`, …), task completion (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), and related harness tools. Every call carries harness fields: optional `callId`, required `stage` (int).

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
