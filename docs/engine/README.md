# Engine

Library: [`src/Harness/Harness.Engine`](../../src/Harness/Harness.Engine) (`net10.0`, namespace `DysonHarness`).

Source is organized by concern under folders (namespace stays `DysonHarness`): `Result/`, `App/`, `Session/`, `Turns/`, `Mcp/`, `Shell/`, `Providers/OpenAi/`, `Storage/`, `Context/`, `Search/`, plus `Migrations/` unchanged. Generated `DysonBuildInfo` stays in the build intermediate output.

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
- **Tool loop** inside one `PromptAsync` (cap ~20 rounds): model tool_calls → staged executor (web tools summarize **inside** the tool) → feed results → call again.
- **Executors (v1):** `DysonWorkspaceToolExecutor` — real `RenameSession`, **`GetDateTime`** (host clock; `timezone`: `"utc"` default or `"local"`), workdir-scoped file tools (`ReadFile`, `CreateFile`, `WriteFile`, `Grep`, `ListDirectory`, `CreateDirectory`), `ShellExecute` (session-available shells via `DysonShell`), in-process web search/fetch tools (`FreeSearch`, `FreeSearchAdvanced`, `SearchWithSynthesis`, `FreeExtract`, `WebFetch`, `FetchGithubReadme`), and **subagent tools** (`StartSubagent`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`); other catalog tools return “not implemented yet”.
- **RenameSession review:** every 8 turns (1-based indices **1, 9, 17, …** — when `TurnHistory.Count % 8 == 0` before adding the turn), the transcript builder appends an ephemeral yes/no `RenameSessionReviewMandate` on the **current incomplete** user message only. Turn 1 is `InitializeSession` via `DysonSessionInitialization.CreateTurn`; later review turns stay `Normal`. Completed/history turns always send clean `Instruction` — the mandate is never re-emitted. Soft every-turn rename nudges are not in system prompts; MCP description says rename only on harness review mandate or explicit user request.
- **Cache-friendly requests** (`OpenAiCacheFriendlyTranscriptBuilder`):
  1. Stable prefix first: system/instructions (mode prompt + MCP catalog) → `tools[]` (stable sort) → prior transcript → new user/tool deltas last.
  2. Never mutate an already-sent/optimized prefix (`OptimizeContextIfNeeded` before building).
  3. `prompt_cache_key` = `dyson:{PersistenceId}` on every call (send-first).
  4. GPT-5.6+ only: optional `prompt_cache_options.mode=explicit` + breakpoint on the system prefix when the slug looks like `gpt-5.6+`.
  5. Completions always sends full local `messages[]`. Responses rebuilds full `input` after compaction / new user turns (`store: false`); within a tool loop may chain `previous_response_id` + `function_call_output` (`store: true` for that hop).
  6. User content for history turns is always `Instruction` only; rename-review mandate is appended only for the in-flight review turn.

Root sessions have runtime `Id = 0`. Subagents get ids ≥ 1 via `RegisterSubagent` (sets child `Parent`).

## Agent modes

Built-in names in `DysonAgentModes`:

| Mode | Intent |
| ---- | ------ |
| `Ask` | Q&A without heavy mutation |
| `Plan` | Planning / design (**top-level only** — banned as a subagent mode) |
| `Work` | Primary work loop / orchestrator |
| `Explore` | Codebase exploration (never spawns subagents) |
| `Drone` | Delegated implementation; may spawn **Explore** only |
| `Security Review` | Security-focused review |
| `Bug Review` | Bug-focused review |
| `Custom` | Category label; lookup uses `Config.CustomAgents` keys |

System prompts come from `DysonAgentSystemPrompts.ForMode`. Work / Explore / Drone directives cover orchestrator routing, Wait-only-for-prerequisites, and `SubmitSubagentReport`. Drone children also get `DroneFirstTurnContextMandate` on the first turn (estimate brief quality; Explore if thin).

## Orchestrator subagents

Primary flow: `StartSubagent` is **non-blocking**; the child runs in the background; the child calls **`SubmitSubagentReport`**; the parent gets a `SubagentCompleted` / `SubagentFailed` interrupt; the host **auto-queues a parent turn** with the report (FIFO if the parent is busy — does not cancel in-flight parent work).

| Tool | Behavior |
| ---- | -------- |
| `StartSubagent` | `CreateChildAsync` — persist child (`ParentSessionId`), register runtime id, background `PromptAsync`. Soft gates via `ValidateSubagentSpawn` |
| `WaitForSubagent` | Block until child terminal or `timeoutMs`. Use **only** when the child’s result is a **prerequisite/blocker** for the next step |
| `InspectSubagentLog` | `SnapshotLog` for a subagent id |
| `StopSubagent` | Cancel child CTS; mark `Stopped`; notify parent |
| `SubmitSubagentReport` | Child-only handoff (`summary`, optional `status` completed\|failed); persists meta and notifies parent |

**Spawn policy (prompt + soft enforce):**

- **Work** may start any built-in mode the task needs **except Plan**.
- **Plan is banned** as `agentMode` — Plan exists only as a top-level session mode.
- **Explore** never spawns.
- **Drone** may spawn **Explore** only (Drone→Drone rejected by default).
- Prefer **Work-owned Explore → then Drone** over Drone-owned Explore when Work can supply context.
- **Work context-before-drones:** estimate whether the brief is rich enough; if not, Explore first, then deploy Drones with a rich brief so they often skip their own Explore.

Self-check: `DysonSubagentSpawnGateSelfCheck.Run()`. Return shape: `DysonStartSubagentResult` (`subagentId`, `persistenceId`, `agentMode`, `title`).

## MCP access

`DysonMcpAccessMode` on `DysonAgentSessionConfig`:

- **FullAccess** — tools run with full access; no allowlist.
- **AutoReview** — calls route through in-process `DysonMcpAutoReviewProxy`; no allowlist.

`DysonMcpPipeline` holds the per-session tool catalog (`FormatToolsForPrompt`) and optional auto-review proxy. OpenAI-compatible sessions also expose the same tools as native function schemas (with required `stage`). Live remote MCP servers remain out of scope; workspace file tools, `ShellExecute`, and web search/fetch tools run locally via `DysonWorkspaceToolExecutor`.

Default tools include subagent control (`StartSubagent`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`), task completion (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), workspace file tools, **`GetDateTime`**, **`ShellExecute`** (when the platform has available shells), **web search/fetch** tools (below), and related harness tools. Every call carries harness fields: optional `callId`, required `stage` (int).

### GetDateTime

- Catalog tool (no work root). Optional `timezone`: `"utc"` (default) or `"local"` (host machine zone).
- Executor returns plain text: `timezone`, ISO `datetime` (`Z` for UTC; offset for local), and `display` as `dd/MM/yyyy HH:mm`.
- Use when the task needs an exact clock — do not guess from training data.

### ShellExecute

- Session config `AvailableShellTypes` defaults from `DysonShell.AvailableForCurrentPlatform()` (Windows: `Pwsh`, `PowerShell`, `Cmd`; other platforms: none yet).
- MCP schema `shell` enum + description list those types; the model must pass `shell` plus `command` (optional `timeoutMs`, `workingDirectory` under the work root).
- Executor rejects shells outside the session list, then `DysonShell.Create` → `DysonWindowsShell` (Windows arg map: `pwsh`/`powershell.exe` `-NoProfile -NonInteractive -Command`, `cmd.exe` `/d /c`).
- Abstraction: `DysonShellType`, abstract `DysonShell` (`ShellType` get + `ExecuteAsync`), `DysonShellRunResult`, `DysonShell.Create` / `AvailableForCurrentPlatform`.

### Web search / fetch (in-process)

Port of [agent-search-mcp](https://github.com/lennney/agent-search-mcp) as catalog tools under `Search/` (not a Node MCP server). Free engines (default order): **DuckDuckGo** HTML first, **Bing** RSS fallback (HTML SERP captcha-prone), **Wikipedia** OpenSearch tertiary; optional **Brave** when `BRAVE_API_KEY` or `DysonAgentSessionConfig.BraveApiKey` is set. Engine HTTP/parse failures surface in `meta.partial_failures` (e.g. `bing: HTTP 429`), not silent empty lists.

| Tool | Behavior |
| ---- | -------- |
| `FreeSearch` | Parallel free engines (`duckduckgo`, `bing`, `wikipedia`); tool-owned summary (skip if ≤~1500 tokens); optional `summarizePrompt` |
| `FreeSearchAdvanced` | Waterfall (DDG+Bing+Wikipedia → Brave if keyed), domain filters, optional Jina enrich; tool-owned summary; optional `summarizePrompt` |
| `SearchWithSynthesis` | Waterfall search + string `prompt_hint` (no LLM call for synthesis); tool-owned summary; optional `summarizePrompt` |
| `FreeExtract` | Jina `r.jina.ai/{url}` markdown extract; SSRF-guarded; tool-owned summary; optional `summarizePrompt` |
| `WebFetch` | GET URL. Default: summarize (always) → summary only (`maxBytes` default **64KB**). `fullHtml: true` → return HTML JSON to parent (`maxBytes` default **2MB**). Optional `summarizePrompt` (ignored when `fullHtml`). SSRF-guarded |
| `FetchGithubReadme` | `raw.githubusercontent.com` README for a GitHub repo URL; tool-owned summary; optional `summarizePrompt` |

**Result summarization:** runs **inside** `DysonWorkspaceToolExecutor` via `DysonWebSearchSummarizer.SummarizeAsync` before MCP `Content` is returned. By default the parent session / UI never sees raw SERP dumps, Jina extracts, or HTML — not even transiently. **Exception:** `WebFetch` with `fullHtml: true` intentionally returns full HTML. Other web tools skip the LLM when already ≤ ~1500 tokens (`summarizePrompt` unused when skipped). Hard cap ≤ 10K tokens (`IDysonTokenCounter`); prompt text lives in `DysonWebSearchSummarizerPrompt` (editable constant; optional “Agent focus” from `summarizePrompt`). Optional dedicated model via `DysonAgentSessionConfig.SummarizerProvider` (null ⇒ session provider); UI: Settings → General → Web search summarizer.

SSRF validation lives in `SearchHttp.ValidateUrl` (blocks localhost, private IPs, metadata hosts). `SearchSelfCheck.RunSsrfChecks()` is a no-framework self-check (SSRF + DDG HTML / Bing RSS parser fixtures + summarizer policy checks; also run on UI startup).

Out of scope for this MVP: news tools, CSDN/Juejin, Baidu/Sogou/Yandex scrapers, separate MCP process.

## Staged tool calls

`DysonToolCall.Stage` orders execution:

1. Same-stage calls run **concurrently**.
2. Ascending stage order is a **barrier** between groups.
3. Status: `Queued` → `Working` → `Completed` | `Failed`.
4. UI binds `DysonAgentTurn.ToolCallStatusChanged` and `TrackedToolCalls`.

`DysonToolCallScheduler.RunStagedAsync` drives this; results append to `ResponseLog`.

### Turn timestamps

`DysonAgentTurn` carries **`StartedUtc`** (set on live turn create; restored from `CreatedUtc`) and **`CompletedUtc`** (set when the host persists turn completion; null while streaming). UI shows these as transcript chrome only — not injected into model messages. Display format in the UI: local wall clock `dd/MM/yyyy HH:mm`.

## Interrupts

Parent sessions observe subagents via `DysonAgentInterrupt` (`SubagentCompleted` / `SubagentStopped` / `SubagentFailed`) with `SubagentId`, optional `PersistenceId`, and `Summary`.

- `EnqueueInterrupt` / `TryDequeueInterrupt` / `WaitForInterruptAsync`
- Concrete `WaitForNotifyAsync` should drain the interrupt queue so Work does not busy-poll
- Hosts (e.g. `DysonUiHost`) watch completion interrupts and FIFO-auto-`PromptAsync` the parent with the report — preferred over `WaitForSubagent` when the parent can multitask

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
