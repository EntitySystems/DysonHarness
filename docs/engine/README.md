# Engine

Library: [`src/Harness/Harness.Engine`](../../src/Harness/Harness.Engine) (`net10.0`, namespace `DysonHarness`). Shared contracts and Result types live in [`Harness.Abstractions`](../../src/Harness/Harness.Abstractions).

Source is organized by concern under folders (namespace stays `DysonHarness`): `App/`, `Session/`, `Turns/`, `Mcp/`, `Shell/`, `Providers/OpenAi/`, `Storage/`, `Context/`, `Search/`, plus `Migrations/` unchanged. Generated `DysonBuildInfo` stays in the build intermediate output. `Result/` types moved to `Harness.Abstractions`.

The engine is an abstract agent harness: `DysonEngine` exposes a root `DysonAgentSession`; sessions talk to an ephemeral `DysonAgentProvider` and run staged MCP-shaped tool calls. There is no concrete host in the engine itself — UI and demo hosts live elsewhere.

For bindable public types, see [api-surface.md](api-surface.md). Persistence is covered under [docs/storage](../storage/models.md). JSON dynamic toolchain: [json-dynamic-toolchain.md](json-dynamic-toolchain.md).

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
- **Reasoning effort:** when `OpenAiCompatibleAgentProvider.ReasoningEffort` is non-empty, Completions send top-level `"reasoning_effort"` and Responses send nested `"reasoning": { "effort": "…" }`; blank/null omits the field (provider default). Sourced from slug `DefaultReasoningEffort` with optional session override.
- **Streaming SSE** (`stream: true`) for assistant text and optional reasoning; Completions reads `choices[0].delta.content` and `delta.reasoning_content` (+ incremental `tool_calls`, `stream_options.include_usage`); Responses handles `response.output_text.delta`, `response.reasoning_summary_text.delta` / `response.reasoning_text.delta`, function-call assembly (`output_item.added` / `function_call_arguments.delta|done` / `output_item.done`), and `error` / `response.failed`. Session consumes chunks per tool-loop round; `AssistantText` + H1 title parse only on the final no-tool round (preview stays raw until then). Per tool round, reasoning is committed into an ordered **`ReasoningLog`** (`Thought` + optional `InterimText` from non-final `reply.Content`) before previews clear; denormalized **`ReasoningText`** is the join of Thought segments only. Final assistant body stays in `AssistantText` (not InterimText). Log + text are UI + persistence only — never injected into transcript builders. Cancel/error commits any streamed Thought when applicable, then clears previews.
- **Transient inference retry:** each Completions/Responses stream round (including missing-tool full replay and Explore budget recap) retries automatically on HTTP **429 / 502 / 503 / 504** (`OpenAiCompatibleHttp.IsTransientServerError`). Schedule is **5 attempts** with fixed backoff **2s → 5s → 10s → 10s** between attempts (honors cancellation; no `Retry-After`). Session logs each retry (`OpenAI transient 503 — retry 1/4 after 2s`); streaming/reasoning previews clear before the next attempt. Non-transient errors and cancellations fail the turn immediately; toast/`LastError` only after exhaustion.
- **Native function tools** with required harness `stage` on every schema.
- **Tool loop** inside one `PromptAsync` (cap **35** rounds; **Explore 120** via `ResolveMaxToolRounds`): model tool_calls → staged executor (web tools summarize **inside** the tool) → feed results → call again. Non-Explore: hitting the budget soft-pauses the turn (Success + harness H1 note) and enqueues a **`RethinkToolUsage`** turn (no second rethink if already on rethink). On rethink, `ResumeCurrentTask` enqueues a Normal turn with a fresh budget; text-only ends the pause. Explore: no rethink — one final no-tools recap reply (findings may be incomplete).
- **Executors (v1):** `DysonWorkspaceToolExecutor` — real `RenameSession`, **`GetDateTime`** (host clock; `timezone`: `"utc"` default or `"local"`), **`GetOpenRulesConfig`** (JSON summary of all work-root `openrules.json` rows incl. `isUrl`/`Providers`; no bodies), **`InitializeOpenRules`** (create-if-missing default manifest with EntitySystems openrules `SKILL.md` URL; no overwrite), **`WaitForSeconds`** (1–300s blocking delay), **`JsonDynamicStructuredLanguageToolchain`** (nested JDSL program over catalog tools — see [json-dynamic-toolchain.md](json-dynamic-toolchain.md)), workdir-scoped file tools (`ReadFile` as `lineNumber|content`, `CreateFile`, `WriteFile` via `DysonTextEditApplier` cascade + optional `replace_all`, `Grep` text-only with binary/image path-only hits + dir excludes, **`LoadBinary`** short ack + `BinaryAttachment` for provider multimodal parts (filename in text label; Responses may upload `file_id`; non-native `image/*` such as ICO/BMP/TIFF/SVG normalized to PNG via Magick `DysonImageNormalize` before emit), **`ConvertImage`** Magick convert/re-encode to a work-dir path (`inputFile`/`outputFile`/`desiredFormat`; optional `quality` 1–100 default 85; same-format allowed; SVG in / ICO out; soft 50 MB input ceiling; overwrite default false; JSON ack only — no `BinaryAttachment`; use `LoadBinary` on the result for vision), `ListDirectory`, `CreateDirectory`), `ShellExecute` (session-available shells via `DysonShell`), **long-running shell tools** (`StartLongRunningShell`, `ListLongRunningShells`, `ReadLongRunningShellTail`, `AbortLongRunningShell`, `RequestLongRunningShellCancellation`, `LongRunningShellInteract`, `SubscribeToLongRunningShellCompletion`), in-process web search/fetch tools (`FreeSearch`, `FreeSearchAdvanced`, `SearchWithSynthesis`, `WebFetch`, `FetchGithubReadme`), **browser tools** (when `BrowserControl` set), **subagent tools** (`StartSubagent`, `ListSubagents`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`), **inter-agent / Ask** (`TriggerParentEvent`, `RespondToSubagentEvent`, `TriggerSubagentEvent`, `AskQuestion`, `AskQuestionFromParent`), **task completion** (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), and **rethink resume** (`ResumeCurrentTask`); other catalog tools return “not implemented yet”.
- **Planned (not shipped):** **`ConvertVideo(inputFile, outputFile, desiredFormat)`** via host **ffmpeg** — intent only; no package, catalog entry, or executor wiring yet.
- **RenameSession review:** every 8 turns (1-based indices **1, 9, 17, …** — when `TurnHistory.Count % 8 == 0` before adding the turn), the transcript builder appends an ephemeral yes/no `RenameSessionReviewMandate` on the **current incomplete** user message only. Turn 1 is `InitializeSession` via `DysonSessionInitialization.CreateTurn`; later review turns stay `Normal`. Completed/history turns always send clean `Instruction` — the mandate is never re-emitted. Soft every-turn rename nudges are not in system prompts; MCP description says rename only on harness review mandate or explicit user request.
- **Cache-friendly requests** (`OpenAiCacheFriendlyTranscriptBuilder`):
  1. Stable prefix first: system/instructions (mode prompt + MCP catalog) → `tools[]` (stable sort) → prior transcript → new user/tool deltas last.
  2. Never mutate an already-sent/optimized prefix (`OptimizeContextIfNeeded` before building).
  3. `prompt_cache_key` = `dyson:{PersistenceId}:sp{SystemPromptGeneration}` on every call (send-first; generation bumps on mid-session `ApplyAgentMode`).
  4. GPT-5.6+ **direct** OpenAI only: optional `prompt_cache_options.mode=explicit` + breakpoint on the system prefix when the slug looks like `gpt-5.6+`. Managed/CLIProxy (`ManagedSource` set) omits `prompt_cache_options` (unsupported) but still sends `prompt_cache_key` + stable prefix ordering.
  5. Completions always sends full local `messages[]`. Responses splits by `SupportsResponsesServerChaining` (`ManagedSource` unset = direct): **direct** uses `store: true`, omits `previous_response_id` on the first full rebuild, requires it on delta tool hops (outputs-only), and passes it on mid-loop full rebuilds when known; always resends `instructions`/`tools` on every hop; on exact 400 “No tool call found for function call output…”, retries once with chaining cleared and full item replay. **Managed/CLIProxy** never chains (`store: false`, no `previous_response_id`); requests `include: ["reasoning.encrypted_content"]` and replays raw `reasoning` → `function_call` → `function_call_output` on every tool hop. Explore budget recap always omits `previous_response_id`. Full rebuilds keep prefix/`prompt_cache_key` caching.
  6. User content for history turns is always `Instruction` only; rename-review mandate is appended only for the in-flight review turn.
  7. **Binary / vision attachments** (`LoadBinary`, `BrowserTakeScreenshot`): one-shot multimodal follow-up after the tool ack (only the unanswered in-flight round). Filename lives in a text / `input_text` label — never on Completions `image_url` or Responses `input_image`. **`LoadBinary`** passes through provider-native image MIME (`image/png` / `jpeg` / `gif` / `webp`); other `image/*` types Magick can decode are converted to PNG (alpha preserved, max edge 1280) before the attachment is built — Magick failure is a tool error, not opaque vision bytes. **Composer user images** (`DysonAgentTurn.UserImages`) are different: they persist on the turn (`UserImagesJson`) and are re-emitted as multimodal parts on that turn’s user message in every full transcript rebuild (Completions `image_url` data URL; Responses `file_id` preferred via `EnsureBinaryFileIdsAsync`, data-URL fallback). Wire shapes:

| Concern | Chat Completions | Responses |
| ------- | ---------------- | --------- |
| Image | `type: "image_url"`, nested `image_url: { url: data URL, detail: "auto" }` (no `file_id` on image parts) | Prefer Files upload `purpose=vision` → `input_image.file_id` + `detail: "auto"`; fallback `input_image.image_url` data-URL **string** + `detail` (no `filename`) |
| Non-image | `type: "file"`, `file: { filename, file_data }` | Prefer Files upload `purpose=user_data` → `input_file.file_id`; fallback `filename` + `file_data` |
| Upload | Not used for vision images | `OpenAiFilesClient` / `EnsureBinaryFileIdsAsync` before building Responses input; failures log a session note and fall back |

Root sessions have runtime `Id = 0`. Subagents get ids ≥ 1 via `RegisterSubagent` (sets child `Parent`).

## Agent modes

Built-in names in `DysonAgentModes`:

| Mode | Intent |
| ---- | ------ |
| `Ask` | Q&A without heavy mutation |
| `Plan` | Planning / design (**top-level only** — banned as a subagent mode). Soft read-only: publish via `SubmitPlan` → `.dyson/plans/`, then revise with `WriteFile`; first turn gets Explore mandate (transcript-only). |
| `Work` | Primary work loop / orchestrator |
| `Explore` | Codebase exploration (never spawns subagents) |
| `Drone` | Delegated implementation; may spawn **Explore** only |
| `Security Review` | Security-focused review |
| `Bug Review` | Bug-focused review |
| `Custom` | Category label; lookup uses `Config.CustomAgents` keys |

System prompts come from `DysonAgentSystemPrompts.ForMode`, then (when an `IDysonModelRepository` is available) an **available-models catalog** is appended, then an **openrules** block (`DysonOpenRules.BuildSystemPromptBlock(Async)`: Root + provider-filtered AutoInclude Rules/Skills from work-root `openrules.json`, or implicit `AGENTS.md`; Paths may be local or http(s) URLs). See [docs/openrules/README.md](../openrules/README.md). That models catalog is what UI / `StartSubagent.modelSlug` can select; effort tags are freeform for API `reasoning_effort` / `StartSubagent.reasoningEffort`. Mid-session mode changes use `DysonAgentSession.ApplyAgentMode` (rebuilds `Mode` / `SystemPrompt`, bumps `SystemPromptGeneration` for cache invalidation — Completions `system` / Responses `instructions` are replaced each hop); the UI host applies on prompt submit when the composer picker differs, appends a completed **`ModeSwitch`** turn before the Normal user turn (history boundary only — short harness user message in the OpenAI transcript; not omitted like `DisplayInfo`), and persists `AgentMode` + `SystemPromptSnapshot`. Work / Explore / Drone directives cover orchestrator routing, Wait-only-for-prerequisites, and mandatory `SubmitSubagentReport`. Explore/Drone directives harden “report is mandatory” / “complete or impossible”, including `failed` + failure-reason summaries. Every child first turn prepends `SubagentReportRequiredMandate` (failure-reason reports are valid finishes); Explore/Drone also get mode-specific first-turn blocks (`ExploreFirstTurnReportMandate`, `DroneFirstTurnContextMandate`). Plan’s first incomplete user turn prepends `PlanFirstTurnMandate` (Explore before finalize; transcript-only). Security Review / Bug Review directives briefly mirror the same when used as subagents.

### Plan artifacts

In Plan mode, `SubmitPlan` (`title` + `markdown`) writes `.dyson/plans/{slug}-{sha1}.md` via `DysonFileManager`, appends a harness `PlanResult` turn (no LLM) with a turn `Instruction` that mandates updating that same `planPath` via `WriteFile` (no second `SubmitPlan` unless the user asks for a new plan), and returns `planPath` in the tool result. After `PlanResult`, the UI shows a composer Plan-ready sticky until a later `BeginBuildPlan` turn (`Host.BuildPendingPlanAsync` → folds any buffered Explore completion reports into the BeginBuildPlan Instruction, switches to Work, then `PromptBeginBuildPlanAsync`; sticky also dismisses on legacy `[BuildPlan]` user prompts). BeginBuildPlan is layout-only (Recap + Agent actions; no implementation that turn): Agent actions must prepare technical multi-Drone parallel workstreams (multitasking preferred over serial solo work; still no `StartSubagent` this turn). It mandates session todos via `CreateTodo` for each Agent actions item after `ReadFile` on the plan. After it succeeds, the host auto-enqueues a Normal continuation (`DysonBeginBuildPlanFlow.ContinuationPrompt`) that runs the implementation (preferring parallel Drone multitasking). Mutating product tools remain available (soft / prompt-only read-only); only `SubmitPlan` is hard-gated to Plan mode.

**Plan-mode Explore reports:** while the parent is in Plan, `SubagentCompleted` / `Failed` / `Stopped` interrupts still enqueue and update subagent UI, but the host does **not** auto-start a `SubagentReportProcessing` harness turn. Those reports are injected into BeginBuildPlan when the user clicks **Build plan**, or drained as usual if the user leaves Plan without Build (mode picker / prompt). `SubagentEvent` auto-turns and Ask UI keep working in Plan.

## Orchestrator subagents

Primary flow: `StartSubagent` is **non-blocking**; the child runs in the background; the child calls **`SubmitSubagentReport`**; the parent gets a `SubagentCompleted` / `SubagentFailed` interrupt; the host **auto-queues a `SubagentReportProcessing` parent turn** with the report (FIFO if the parent is busy — does not cancel in-flight parent work). That turn analyzes the report, writes concrete continuation instructions, and proceeds with parent work in the same turn (tools allowed). Exception: while the parent is in **Plan** mode, completion reports are buffered (no `SubagentReportProcessing` auto-turn) until **Build plan** folds them into `BeginBuildPlan`, or until the session leaves Plan without Build (then they drain as usual).

| Tool | Behavior |
| ---- | -------- |
| `StartSubagent` | `CreateChildAsync` — persist child (`ParentSessionId`), register runtime id, background `PromptAsync`. Soft gates via `ValidateSubagentSpawn`. Optional `modelSlug` (slug or display alias) resolves via `IDysonModelRepository.FindSlugByNameAsync`; omit inherits parent provider (same kind only). Optional `reasoningEffort` (freeform); omit/null → chosen slug’s `DefaultReasoningEffort`; when inheriting parent model, omit keeps the parent’s current effort |
| `ListSubagents` | Session-owned roster of **direct** children (`SubSessions`): JSON array of `subagentId`, `persistenceId`, `agentMode`, `title`, `status`, optional `modelLabel`. Use before Wait/Inspect/Stop when ids are missing from recent context (resume / compaction) |
| `WaitForSubagent` | Block until child terminal or `timeoutMs`. Wait **only** when the child’s result is a **blocker for the next automatic turn** (typically Explore-before-implementation); do **not** Wait on Drones — prefer the notification turn |
| `InspectSubagentLog` | `SnapshotLog` for a subagent id |
| `StopSubagent` | Cancel child CTS; mark `Stopped`; notify parent |
| `SubmitSubagentReport` | Child-only handoff (`summary`, optional `status` completed\|failed); **hard-blocks** a successful (`completed`) report when the session has incomplete todos (`Pending`/`Ongoing`); `status: failed` is still allowed with incomplete todos (blocker handoff); empty todo list passes; persists meta and notifies parent (parent summary stays the agent-provided `summary`). Summary may be a success handoff or, when `status` is `failed`, a concrete failure reason. If the child is already harness-`Failed` (e.g. kickoff missed a report), a later agent report **supersedes** that Failed status (`Completed`/`Failed` per tool `status`), replaces `LastReportSummary`, persists again, and **re-notifies** the parent. After a successful `completed` report, further `SubmitSubagentReport` calls are **idempotent** tool COMPLETED (`idempotent: true`, original summary; no re-notify). A first real `failed` report stays `Failed` (not rewritten to Completed). `Stopped` still rejects a second submit. |
| `TriggerParentEvent` | Child → parent: queue event (`kind`, `payload`), **block** until `RespondToSubagentEvent`. Fails immediately if parent is inside `WaitForSubagent` for **any** child (deadlock guard). |
| `RespondToSubagentEvent` | Parent completes a pending event (`subagentId`, `eventId`, `reply`). **Not** wait-gated — works mid-`WaitForSubagent` for already-pending events. |
| `TriggerSubagentEvent` | Parent → child inject (`payload`, optional `interruptSubagent`). Default queues next-turn prompt; `interruptSubagent=true` cancels in-flight turn + any parent-event wait, then `PromptAsync` immediately. Non-interrupt fails if child is awaiting a parent-event reply. |
| `AskQuestion` | Root-only: 1–8 clarifying / design questions via composer UI; blocks until answered (Q#/A# text; per-question Skip → `A# - [skipped]`). Prefer **PromptUserDialog** for concrete action choices. |
| `AskQuestionFromParent` | L1 only: wraps `TriggerParentEvent(kind=askQuestion)`; host Auto UI answers via internal `RespondToSubagentEvent`. |
| `PromptUserDialog` | Root-only: modal action picker (`title`, `description`, 1–4 actions, at most one `primary`); UI always adds non-primary **Skip**; blocks until chosen. Result JSON `{ action, skipped }` (Skip includes short guidance). Prefer **AskQuestion** for open-ended design clarification. |
| `PromptUserDialogFromParent` | L1 only: wraps `TriggerParentEvent(kind=promptUserDialog)`; host modal answers via internal `RespondToSubagentEvent`. |

**Spawn policy (prompt + soft enforce):**

- **Work** may start any built-in mode the task needs **except Plan**.
- **Plan is banned** as `agentMode` — Plan exists only as a top-level session mode.
- **Explore** never spawns.
- **Drone** may spawn **Explore** only (Drone→Drone rejected by default).
- Prefer **Work-owned Explore → then Drone** over Drone-owned Explore when Work can supply context.
- **Work context-before-drones:** estimate whether the brief is rich enough; if not, Explore first, then deploy Drones with a rich brief so they often skip their own Explore.

**Layer catalog gating** (`ConfigureInterAgentTools(depth)`): root keeps `AskQuestion` / `PromptUserDialog` / `RespondToSubagentEvent` / `TriggerSubagentEvent` (omits FromParent + TriggerParentEvent); L1 keeps AskQuestionFromParent / PromptUserDialogFromParent + TriggerParentEvent + Respond + TriggerSubagentEvent (omits root Ask/Dialog); deeper keeps TriggerParentEvent + Respond + TriggerSubagentEvent only.

Soft spawn / restore / inter-agent event coverage: `Harness.Tests` (`DysonSubagentSpawnGateTests`, `DysonSubagentRestoreTests`, `DysonParentEventTests`, `DysonPromptUserDialogTests`) — `dotnet test src/Harness/Harness.Tests/Harness.Tests.csproj`. Return shape: `DysonStartSubagentResult` (`subagentId`, `persistenceId`, `agentMode`, `title`, optional `modelSlug` / `modelLabel`). Kickoff failures (no `SubmitSubagentReport`) mark the child `Failed`, persist status + parent interrupt, and notify with a non-empty reason (PromptAsync error → last turn snippet → harness message; exceptions as `{Type}: {Message}`). A later successful `SubmitSubagentReport` can supersede that harness Failed (see tool row above).

On parent resume the host hydrates direct DB children into `SubSessions` / `SubagentsById` via `RestoreRegisteredSubagent` (bumps next runtime id; does not raise `SubagentSpawned`).

## MCP access

`DysonMcpAccessMode` on `DysonAgentSessionConfig`:

- **FullAccess** — tools run with full access; no allowlist.
- **AutoReview** — calls route through in-process `DysonMcpAutoReviewProxy`; no allowlist.

`DysonMcpPipeline` holds the per-session tool catalog (`FormatToolsForPrompt`) and optional auto-review proxy. OpenAI-compatible sessions also expose the same tools as native function schemas (with required `stage`). Live remote MCP servers remain out of scope; workspace file tools, `ShellExecute`, web search/fetch, and browser tools run locally via `DysonWorkspaceToolExecutor`.

**Toolset builder / mode policy:** `DysonSessionToolsetBuilder` builds the catalog (`CreateDefault` → shell/Plan + inter-agent + subagent omit → mode denylist). `DysonAgentSessionConfig.ToolPolicy` / `DisabledTools` come from `app_settings` (`agent_mode_tool_policy`) via the UI host; `ApplyAgentMode` **rebuilds** the pipeline so re-enabled tools return. Structural gates (no shells, browser null, inter-agent depth, subagent completion omit) still win for availability. Per-model overlays exist on the document and resolver signature but are not applied yet. Executor rejects calls whose tool name is absent from the current catalog.

Default tools include subagent control (`StartSubagent`, `ListSubagents`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`), inter-agent events + Ask/Dialog (`TriggerParentEvent`, `RespondToSubagentEvent`, `TriggerSubagentEvent`, `AskQuestion`, `AskQuestionFromParent`, `PromptUserDialog`, `PromptUserDialogFromParent` — layer-gated), task completion (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), rethink resume (`ResumeCurrentTask`), workspace file tools, **`LoadSkill`** (included / `.dyson/skills` / literal / openrules AgentOptional incl. URL fetch; provider-filtered; required `loadIndexOnly`), **`GetOpenRulesConfig`**, **`InitializeOpenRules`**, **`GetDateTime`**, **`WaitForSeconds`**, **`JsonDynamicStructuredLanguageToolchain`** (see [json-dynamic-toolchain.md](json-dynamic-toolchain.md)), **`ShellExecute`** and **long-running shell tools** (when the platform has available shells), **browser tools** (when `BrowserControl` is set), **web search/fetch** tools (below), and related harness tools. Every call carries harness fields: optional `callId`, required `stage` (int). OpenRules schema / Providers / URL Paths: [docs/openrules](../openrules/README.md).

### GetDateTime

- Catalog tool (no work root). Optional `timezone`: `"utc"` (default) or `"local"` (host machine zone).
- Executor returns plain text: `timezone`, ISO `datetime` (`Z` for UTC; offset for local), and `display` as `dd/MM/yyyy HH:mm`.
- Use when the task needs an exact clock — do not guess from training data.

### WaitForSeconds

- Required integer `seconds` in **1–300** (reject out of range; do not clamp).
- Blocks the tool call via `Task.Delay` until the wait finishes; prompt cancel aborts with a tool error.
- Success JSON: `{ status: "ok", waitedSeconds: N }`. Available to root and subagents.

### JsonDynamicStructuredLanguageToolchain

- Nested-only JSON program over **catalog tools** (`Entry` / `FunctionCall` object / `Loop`). Flat FunctionCall strings rejected.
- Branches on nested `IsError` (`OnSuccess` / `OnFailure` / `ContinueWith`); refs `fromArg:` / `fromResult:$0` / `fromResult:path`.
- Caps: depth 8, 50 nested invocations, `MaxIterations` 1–20 (default 5). No self-call.
- Result includes program-shaped `flow` tree (`executed` flags) for the UI flow modal. Spec: [json-dynamic-toolchain.md](json-dynamic-toolchain.md). Agent guide: `Resources/Skills/JDSL.md` — load via `LoadSkill(name: "JDSL", loadIndexOnly: true)` or composer `/skill-jdsl`. Covered by `DysonJsonDynamicToolchainTests` / `DysonSkillLoaderTests`.

### ShellExecute

- Session config `AvailableShells` is an `IReadOnlyList<DysonConfiguredShellSpec>` (`Name` + `ExecutablePath` + optional `FixedArgs`). Empty ⇒ `ShellExecute` and all long-running shell tools are omitted. The UI host loads **enabled** rows from `configured_shells` (`IDysonConfiguredShellRepository.EnsureDefaultsAsync` + `ListEnabledSpecsAsync`) on new/resume.
- MCP schema `shell` enum + description list those **names**; the model must pass `shell` plus `command` (optional `timeoutMs`, `workingDirectory` under the work root).
- Default cwd is the session workspace FS `NativeRootPath` (from `DysonWorkspaceFileSystems.CreateLocalAsync` / `IDysonWorkspaceFileSystem`); optional `workingDirectory` is resolved through the same sandboxed FS.
- Executor resolves name → path/`FixedArgs` against `AvailableShells`, then `DysonWindowsShell.ExecuteWithPathAsync` via `ResolveFixedArgs`: non-empty spec `FixedArgs` win; else basename heuristics (`pwsh`/`powershell` → `-NoProfile -NonInteractive -Command`, `cmd` → `/d /c`, `bash`/`sh`/`zsh`/`git-bash` → `-c`). Unknown basename without Fixed args errors and points to Settings → Shells.
- Related: `DysonConfiguredShellSpec`, `DysonShellType` (legacy/heuristics), abstract `DysonShell`, `DysonShellRunResult`, `ResolveFixedArgs` / `MapFixedArgsFromExecutablePath`.
- **Plan soft warning:** in Plan mode, `ConfigureShellExecuteForMode(true)` appends a read-only-inspection warning to the tool description (prefer `dir` / `git status` / small reads; never run builds/installs/servers; prefer `ReadFile` / `Grep` / `ListDirectory`). The executor still runs the command but prepends the same WARNING to Ok/Error content. Non-Plan modes use the plain description. Covered by `DysonShellExecutePlanWarningTests` in `Harness.Tests`.

### Long-running shells

Workdir-scoped background processes for E2E runs, large builds, and keeping development servers running (in-memory only — UI restart orphans OS children; only Abort/Cancel kill them). Prefer `ShellExecute` for one-shot commands.

| Type / tool | Role |
| ----------- | ---- |
| `DysonLongRunningShellRegistry` | Static workdir buckets; incremental `longRunningShellId` ints per workdir; shared by parent/child sessions; completion subscribers |
| `DysonLongRunningShell` | Process + stdin + stdout/stderr/combined rings (~256KB each); stores configured `ShellName` |
| `DysonLongRunningShellExitedFlow` | `ShellExited` turn Instruction (auto-read tail) + trim after completion; Instruction uses configured shell name |
| `StartLongRunningShell` | `shell` (enabled name) + `command` (+ optional `workingDirectory`) → `longRunningShellId` |
| `ListLongRunningShells` | Compact roster for the workdir (`id`, `status`, `shell` = configured name, short `command`, `exitCode`, `startedUtc`) |
| `ReadLongRunningShellTail` | Tail combined output; optional `timeoutMs` wait for new bytes |
| `AbortLongRunningShell` | Kill process tree (same as UI Force stop) |
| `RequestLongRunningShellCancellation` | Soft cancel (`\x03` stdin, else `CloseMainWindow`) |
| `LongRunningShellInteract` | Write stdin (+ newline if missing) |
| `SubscribeToLongRunningShellCompletion` | Non-blocking subscribe → on terminal, `LongRunningShellExited` interrupt → host `ShellExited` auto-turn (always drained, including Plan); Instruction auto-reads tail then trims it after the turn |

Same platform gate as `ShellExecute` (omitted when no shells). Plan soft-warns on `StartLongRunningShell` (description + result preamble). Covered by `DysonLongRunningShellTests` in `Harness.Tests`.

### Browser control

Optional process-wide `IDysonBrowserControl` on `DysonAgentSessionConfig.BrowserControl` (Windows: CefSharp WPF via `Harness.WindowsBrowser`; see [packaging/webview](../packaging/webview.md)). When null, browser tools are **omitted** from the MCP catalog.

| Tool | Behavior |
| ---- | -------- |
| `OpenBrowser` | Open WPF agent browser window; optional `url` / `width` / `height` → `windowId` + `tabId` |
| `ListBrowserWindows` / `CloseBrowser` / `ResizeBrowser` | Window list / close / resize |
| `ListBrowserTabs` / `NewBrowserTab` / `CloseBrowserTab` / `ActivateBrowserTab` | Tab management |
| `BrowserNavigate` / `BrowserGoBack` / `BrowserGoForward` / `BrowserReload` | Navigation |
| `BrowserClick` / `BrowserType` / `BrowserFill` / `BrowserHover` / `BrowserPressKey` | Interaction (JS helpers for click/type/etc.) |
| `BrowserWaitForSelector` / `BrowserWaitForNavigation` | Waits |
| `BrowserExecuteJavaScript` / `BrowserGetHtml` / `BrowserTakeScreenshot` | Page inspection (screenshot via DevTools CDP; optional `timeoutMs`, default 30000) |
| `BrowserReadConsoleLog` / `BrowserReadNetworkLog` | Thin collectors (console messages + main-frame loads until CDP deepens) |

Contracts: `IDysonBrowserControl` / `IDysonBrowserWindow` / `IDysonBrowserTab` + request/log DTOs in `Harness.Abstractions`. Null stand-in: `DysonNullBrowserControl`.

### Web search / fetch (in-process)

Port of [agent-search-mcp](https://github.com/lennney/agent-search-mcp) as catalog tools under `Search/` (not a Node MCP server). Free engines (default order): **DuckDuckGo** HTML first, **Bing** RSS fallback (HTML SERP captcha-prone), **Wikipedia** OpenSearch tertiary; optional **Brave** when `BRAVE_API_KEY` or `DysonAgentSessionConfig.BraveApiKey` is set. Engine HTTP/parse failures surface in `meta.partial_failures` (e.g. `bing: HTTP 429`), not silent empty lists.

| Tool | Behavior |
| ---- | -------- |
| `FreeSearch` | Parallel free engines (`duckduckgo`, `bing`, `wikipedia`); tool-owned summary (skip if ≤~1500 tokens); optional `summarizePrompt` |
| `FreeSearchAdvanced` | Waterfall (DDG+Bing+Wikipedia → Brave if keyed), domain filters, optional WebFetch enrich; tool-owned summary; optional `summarizePrompt` |
| `SearchWithSynthesis` | Waterfall search + string `prompt_hint` (no LLM call for synthesis); tool-owned summary; optional `summarizePrompt` |
| `WebFetch` | Default page extractor. GET URL; summarize by default → summary only (`maxBytes` default **64KB**). `fullHtml: true` → return HTML JSON to parent (`maxBytes` default **2MB**). Optional `summarizePrompt` (ignored when `fullHtml`). SSRF-guarded |
| `FetchGithubReadme` | `raw.githubusercontent.com` README for a GitHub repo URL; tool-owned summary; optional `summarizePrompt` |

**Result summarization:** runs **inside** `DysonWorkspaceToolExecutor` via `DysonWebSearchSummarizer.SummarizeAsync` before MCP `Content` is returned. By default the parent session / UI never sees raw SERP dumps or HTML — not even transiently. **Exception:** `WebFetch` with `fullHtml: true` intentionally returns full HTML. Other web tools skip the LLM when already ≤ ~1500 tokens (`summarizePrompt` unused when skipped). Hard cap ≤ 10K tokens (`IDysonTokenCounter`); prompt text lives in `DysonWebSearchSummarizerPrompt` (editable constant; optional “Agent focus” from `summarizePrompt`). Optional dedicated model via `DysonAgentSessionConfig.SummarizerProvider` (null ⇒ session provider); UI: Settings → General → Web search summarizer.

SSRF validation lives in `SearchHttp.ValidateUrl` (blocks localhost, private IPs, metadata hosts). Covered by `SearchTests` in `Harness.Tests` (SSRF + DDG HTML / Bing RSS parser fixtures + summarizer policy).

Out of scope for this MVP: news tools, CSDN/Juejin, Baidu/Sogou/Yandex scrapers, separate MCP process.

## Staged tool calls

`DysonToolCall.Stage` orders execution:

1. Same-stage calls run **concurrently**.
2. Ascending stage order is a **barrier** between groups.
3. Status: `Queued` → `Working` → `Completed` | `Failed`.
4. UI binds `DysonAgentTurn.ToolCallStatusChanged` and `TrackedToolCalls`.

`DysonToolCallScheduler.RunStagedAsync` drives this; results append to `ResponseLog`.

### Turn timestamps

`DysonAgentTurn` carries **`StartedUtc`** (set on live turn create; restored from `CreatedUtc`) and **`CompletedUtc`** (set when the host persists turn completion; null while streaming). Ordered **`ReasoningLog`** (`Thought` / `InterimText` per tool round) plus denormalized **`ReasoningText`** (Thought join) hold provider thinking for UI + DB reload — never injected into model transcripts. Restore synthesizes one Thought from legacy `ReasoningText` when the log is empty. UI shows timestamps as transcript chrome only — not injected into model messages. Display format in the UI: local wall clock `dd/MM/yyyy HH:mm`.

## Interrupts

Parent sessions observe subagents via `DysonAgentInterrupt` (`SubagentCompleted` / `SubagentStopped` / `SubagentFailed` / `SubagentEvent`) with `SubagentId`, optional `PersistenceId`, and `Summary`. `SubagentEvent` also carries `EventId`, `EventKind`, and `Payload`.

- `EnqueueInterrupt` / `TryDequeueInterrupt` / `WaitForInterruptAsync`
- Concrete `WaitForNotifyAsync` should drain the interrupt queue so Work does not busy-poll
- Hosts (e.g. `DysonUiHost`) watch completion interrupts and FIFO-auto-`PromptAsync` the parent with the report — preferred over `WaitForSubagent` when the parent can multitask
- `SubagentEvent` (non-`askQuestion`): host shows an expandable **Subagent event** block (spinner while unaddressed) and FIFO-auto-prompts the parent to `RespondToSubagentEvent`
- `askQuestion` events: same Subagent-event block + Ask popover Auto UI; parent LLM does **not** auto-Respond

In-flight parent events are **not** persisted across process restart.
## Task completion flow

Root sessions only (subagents use `SubmitSubagentReport`). Pending follow-ups live on the session **`ConcurrentQueue<DysonAgentTurn>`** (`EnqueuePendingTurn` / `TryDequeuePendingTurn`); the UI host drains them into its prompt queue and runs each via **`PromptHarnessTurnAsync`** so kinds stay intact.

After the model calls `CompleteTask`:

1. **Confirm** — enqueue **`TaskCompletionConfirm`** (`DysonTaskCompletionFlow.CreateCompletionConfirmTurn`); on that turn only, `ConfirmTaskComplete` or `ContinueWork` are valid
2. **Continue** — `ContinueWork` enqueues a **`Continuation`** turn if work remains
3. **Report** — `ConfirmTaskComplete` enqueues a **`ReportSummary`** turn (final handoff); after that reply the host calls `TryMarkTerminal(Completed)` + persists

Factories: `DysonTaskCompletionFlow` and session helpers `CreateCompletionConfirmTurn` / `CreateContinuationTurn` / `CreateReportSummaryTurn`. Covered by `DysonTaskCompletionTests` in `Harness.Tests`.

## Rethink tool usage

When a non-Explore OpenAI-compatible turn exhausts its tool-round budget (**35**), the session soft-pauses (Success, harness H1 note) and enqueues **`RethinkToolUsage`** via `DysonRethinkToolUsageFlow.CreateTurn` — unless the exhausted turn was already rethink (no double-rethink). Pending turns drain through the same host queue as CompleteTask.

On the rethink turn only: readonly tools when a peek is needed; optional `StartSubagent` Explore with mandatory `WaitForSubagent` this turn; **`ResumeCurrentTask`** (`rationale` and/or `continuationInstructions` required) enqueues a **`Normal`** turn (`CreateResumeTurn`) with a fresh budget. Text-only reply means stop. Available to root and subagents.

**Explore** budget is **120**. Explore sessions never enqueue `RethinkToolUsage`; hitting the budget runs one final Completions/Responses call with tools cleared (`ExploreBudgetRecapInstruction`) so the model recaps findings and notes they may be incomplete. Covered by `DysonRethinkToolUsageTests` in `Harness.Tests`.

## Expand thought process

`ExpandThoughtProcess` MCP queues an `ExpandThoughtProcess` turn via `CreateExpandThoughtProcessTurn` / `DysonExpandThoughtProcess`, sets `EndsCurrentTurn` on the tool result, and the OpenAI tool loop soft-closes the calling turn (`SoftCloseAfterEndsCurrentTurn`) — no further model rounds. Recursion on an in-flight expand turn is rejected.

## Start new turn

`StartNewTurn(promptInstructions)` hard-ends the current turn (`EndsCurrentTurn`) and enqueues a **Normal** turn whose `Instruction` is the provided text (e.g. “write the second 50-word paragraph”). Callable anytime; not a substitute for ExpandThoughtProcess. Soft-close keeps same-round `reply.Content` when non-empty; otherwise uses a tool-specific harness note (`StartNewTurn` / `ExpandThoughtProcess` / generic). Host drains pending turns the same way as other queued follow-ups. Covered by `DysonStartNewTurnTests` / soft-close asserts in `DysonExpandThoughtProcessTests`.

During any turn the model may call **`DropTurnContext`** (`turnIds` from `[turnId=…]` history headers, required `reason`) to set `IsExcludedFromContext` on prior turns; each newly dropped turn appends a session log line `Turn {id} dropped, reason: …`. **`RestoreTurnContext`** (same args shape) clears the flag and logs `Turn {id} restored, reason: …` — available but not mandated. Excluded turns stay in the UI (Dropped badge + Restore) but are omitted from Completions/Responses transcripts and context-optimizer walks. After expand completes, the host enqueues a Normal continuation (`ShouldEnqueueContinuation` / `ContinuationPrompt`). Covered by `DysonExpandThoughtProcessTests` in `Harness.Tests`.

### Max target context + DropContext inject

When estimated outgoing Completions/Responses tokens (same `OpenAiCacheFriendlyTranscriptBuilder` path + tiktoken; see `DysonOutgoingContextTokens`) exceed the effective max target (`session.MaxTargetContextTokens` → slug `DefaultMaxTargetContextTokens` → **100_000**; session **0** = Off), the next **Send** (`PromptWithTurnAsync`) may inject a `DropContext` turn (`DysonDropContextFlow`, keep last **4** turns) so the agent can `DropTurnContext` on older noise, then continues the original prompt (`allowDropContextInject: false` prevents nesting). Footer/idle overage does **not** inject — only the start of the next prompt.

Gates: effective max > 0; not already in a DropContext phase; at least one non-excluded turn older than the keep-last-4 window; and a **5-user-turn throttle** after a DropContext (`Normal` | `InitializeSession` counts; first inject with no prior DropContext is immediate). Session log: `drop-context: inject (estimated=… max=…)` on inject, or `drop-context: skip (reason; estimated=… max=…)` when over a positive max but skipping (`in-phase`, `no-droppable-older`, `throttle`; Evaluate also returns `off` when max is 0). Covered by `DysonDropContextTests`.

## Context optimizer

`DysonContextOptimizer` (code-generated compaction, no LLM):

- Triggers on turn count or unoptimized token size (`IDysonTokenCounter`, default Tiktoken).
- Compacts **older** turns only (`KeepRecentTurns`); sets `ToolHistoryOptimized` + `CompactToolHistory` for prompt-cache stability.
- Compact lines use a harness-tagged shape (`[compact] {Tool} params: … || result: …`), not natural “Called … with params” prose.
- Cache-friendly Completions/Responses builders inject compacted history as **`role: user`** with a fixed harness prefix (historical summary only — do not imitate; use native tool calls). It is **not** emitted as assistant content.
- If a no-`tool_calls` model round echoes only compact-shaped lines (new or legacy), the session clears that body instead of storing it as `AssistantText`.
- Call `OptimizeContextIfNeeded` before building the next provider request.

Covered by `DysonContextOptimizerTests` in `Harness.Tests`.

## Result pattern

Public expected-failure paths return `Result<TValue, TError>`, `VoidResult<TError>`, or `ValueResult<TValue>` from `Harness.Abstractions` — see [rules/rules_csharp.md](../../rules/rules_csharp.md). Do not use exceptions for ordinary control flow.
