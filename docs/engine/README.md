# Engine

Library: [`src/Harness/Harness.Engine`](../../src/Harness/Harness.Engine) (`net10.0`, namespace `DysonHarness`). Shared contracts and Result types live in [`Harness.Abstractions`](../../src/Harness/Harness.Abstractions).

Source is organized by concern under folders (namespace stays `DysonHarness`): `App/`, `Session/`, `Turns/`, `Mcp/`, `Shell/`, `Runtimes/`, `Providers/OpenAi/`, `Storage/`, `Context/`, `Search/`, `Messaging/`, plus `Migrations/` unchanged. Generated `DysonBuildInfo` stays in the build intermediate output. `Result/` types moved to `Harness.Abstractions`.

The engine is an abstract agent harness: `DysonEngine` exposes a root `DysonAgentSession`; sessions talk to an ephemeral `DysonAgentProvider` and run staged MCP-shaped tool calls. There is no concrete host in the engine itself — UI and demo hosts live elsewhere. Functional code belongs in Engine/Abstractions; UI hosts only attach — see [rules/rules_engine_ui.md](../../rules/rules_engine_ui.md).

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
- **Reasoning effort:** when `OpenAiCompatibleAgentProvider.ReasoningEffort` is non-empty, Completions send top-level `"reasoning_effort"` except `ManagedSource=openrouter`, which uses nested `"reasoning": { "effort": "…" }` (`orcarouter` stays top-level); Responses always send nested `"reasoning": { "effort": "…" }`. Blank/null omits the field (provider default). Sourced from slug `DefaultReasoningEffort` with optional session override.
- **Streaming SSE** (`stream: true`) for assistant text and optional reasoning; Completions reads `choices[0].delta.content` and `delta.reasoning_content` (+ incremental `tool_calls`, `stream_options.include_usage`); Responses handles `response.output_text.delta`, `response.reasoning_summary_text.delta` / `response.reasoning_text.delta`, function-call assembly (`output_item.added` / `function_call_arguments.delta|done` / `output_item.done`), and `error` / `response.failed`. Session consumes chunks per tool-loop round; `AssistantText` + H1 title parse only on the final no-tool round (preview stays raw until then). Per tool round, reasoning is committed into an ordered **`ReasoningLog`** (`Thought` + optional `InterimText` from non-final `reply.Content`) before previews clear; denormalized **`ReasoningText`** is the join of Thought segments only. Final assistant body stays in `AssistantText` (not InterimText). Log + text are UI + persistence only — never injected into transcript builders. Cancel/error commits any streamed Thought when applicable, then clears previews. After each successful Completions/Responses round (main tool loop + Explore recap), `OpenAiCompatibleHttp.TryParseUsage` fills `OpenAiModelReply.Usage` and the session appends a `usage_requests` row when `IDysonUsageAnalyticsRepository` is wired (append failure is logged, not a turn error). Demo sessions and web-search / turn-summarizer Completions do not write usage rows.
- **Transient inference retry:** each Completions/Responses stream round (including missing-tool full replay and Explore budget recap) retries automatically on OpenAI stream/endpoint errors (`OpenAiCompatibleHttp.IsTransientServerError`) — all known shapes (HTTP statuses, transport, stream read, incomplete SSE, Responses mid-stream) except **401 / 403** and cancellations. Schedule is **5 attempts** with fixed backoff **2s → 5s → 10s → 10s** between attempts (honors cancellation; no `Retry-After`). Session logs each retry (`OpenAI transient 503 — retry 1/4 after 2s`); streaming/reasoning previews clear before the next attempt. User cancel never hops and fails the turn immediately. After those retries exhaust (and **immediately** on **401 / 403**, which are not retried), one hop per prompt to `DysonAgentSessionConfig.FallbackChatProvider` if set (OpenAI-compatible, different `SlugId`): mutate live `session.Provider` mid-turn, persist `ModelSlugId` + `ReasoningEffort`, rebuild Completions/Responses (clear `previousResponseId`, recompute API mode), and continue the round. Fallback gets its own 5 attempts. If fallback also dies, fail with the fallback error (session stays on fallback). Null / empty `FallbackChatProvider` = disabled (fail the turn after retries; unlike other role pickers, empty does **not** mean “use session model”). Toast/`LastError` only after exhaustion (including fallback).
- **Native function tools** with required harness `stage` on every schema.
- **Direct image generation:** when the session has a resolved `ImageGenerationProvider`, `GenerateImage` calls only the direct OpenAI Images endpoint (`POST https://api.openai.com/v1/images/generations`). Eligibility requires an enabled, credentialed, unmanaged OpenAI-compatible slug at that exact HTTPS API root; managed providers (including CLIProxy, OpenRouter, and OrcaRouter) and other OpenAI-compatible endpoints are excluded. The dedicated setting is never replaced by the chat provider. Outputs are normalized to PNG and persisted as workspace artifacts; only compact metadata enters the tool transcript.
- **Tool loop** inside one `PromptAsync` (cap **50** rounds; **Explore 120** via `ResolveMaxToolRounds`): model tool_calls → staged executor (web tools summarize **inside** the tool) → feed results → call again. Non-Explore: hitting the budget soft-pauses the turn (Success + harness H1 note) and enqueues a **`RethinkToolUsage`** turn (no second rethink if already on rethink). On rethink, `ResumeCurrentTask` enqueues a Normal turn with a fresh budget; text-only ends the pause. Explore: no rethink — one final no-tools recap reply (findings may be incomplete).
- **Executors (v1):** `DysonWorkspaceToolExecutor` — real `RenameSession`, **`GetDateTime`** (host clock; `timezone`: `"utc"` default or `"local"`), **`GetOpenRulesConfig`** (JSON summary of all work-root `openrules.json` rows incl. `isUrl`/`Providers`; no bodies), **`InitializeOpenRules`** (create-if-missing default manifest with EntitySystems openrules `SKILL.md` URL; no overwrite), **`WaitForSeconds`** (1–300s blocking delay), **`JsonDynamicStructuredLanguageToolchain`** (nested JDSL program over catalog tools — see [json-dynamic-toolchain.md](json-dynamic-toolchain.md)), workdir-scoped file tools (`ReadFile` as `lineNumber|content`, `CreateFile`, `WriteFile` via `DysonTextEditApplier` cascade + optional `replace_all`, `Grep` text-only with binary/image path-only hits + dir excludes, **`LoadBinary`** short ack + `BinaryAttachment` for provider multimodal parts (filename in text label; Responses may upload `file_id`; non-native `image/*` such as ICO/BMP/TIFF/SVG normalized to PNG via Magick `DysonImageNormalize` before emit), **`ConvertImage`** Magick convert/re-encode to a work-dir path (`inputFile`/`outputFile`/`desiredFormat`; optional `quality` 1–100 default 85; same-format allowed; SVG in / ICO out; soft 50 MB input ceiling; overwrite default false; JSON ack only — no `BinaryAttachment`; use `LoadBinary` on the result for vision), **`GenerateImage`** (provider-gated direct OpenAI Images generation; required prompt plus optional generation settings; writes normalized PNG artifacts under `.dyson/image-gen/` and returns metadata only), `ListDirectory`, `CreateDirectory`), `ShellExecute` (session-available shells via `DysonShell`), **long-running shell tools** (`StartLongRunningShell`, `ListLongRunningShells`, `ReadLongRunningShellTail`, `AbortLongRunningShell`, `RequestLongRunningShellCancellation`, `LongRunningShellInteract`, `SubscribeToLongRunningShellCompletion`, `WaitForLongRunningShellCompletion`), in-process web search/fetch tools (`FreeSearch`, `FreeSearchAdvanced`, `SearchWithSynthesis`, `WebFetch`, `FetchGithubReadme`), **browser tools** (when `BrowserControl` set), **subagent tools** (`StartSubagent`, `ListSubagents`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`), **inter-agent / Ask** (`TriggerParentEvent`, `RespondToSubagentEvent`, `TriggerSubagentEvent`, `AskQuestion`, `AskQuestionFromParent`), **task completion** (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), and **rethink resume** (`ResumeCurrentTask`); other catalog tools return “not implemented yet”.
- **Planned (not shipped):** **`ConvertVideo(inputFile, outputFile, desiredFormat)`** via host **ffmpeg** — intent only; no package, catalog entry, or executor wiring yet.
- **RenameSession review:** every 8 **chrome-skipped** turns (1-based indices **1, 9, 17, …** among turns that are not `DisplayInfo` / `ModeSwitch` / `PlanResult` — those kinds do not consume review slots), the transcript builder appends an ephemeral yes/no `RenameSessionReviewMandate` on the **current incomplete** user message only. Eligible slot 1 is `InitializeSession` via `DysonSessionInitialization.CreateTurn` when that is the first counted turn; later review turns stay `Normal`. Completed/history turns always send clean `Instruction` — the mandate is never re-emitted. Soft every-turn rename nudges are not in system prompts; MCP description says rename only on harness review mandate or explicit user request.
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
| `Plan` | Planning / design (**top-level only** — cannot spawn a Plan child; a Plan parent may `StartSubagent` Explore). Soft read-only: publish via `SubmitPlan` → `.dyson/plans/`, then revise with `WriteFile`; first incomplete **Plan-stint** user prompt gets Explore mandate (transcript-only; skips `ModeSwitch` / `DisplayInfo` / `PlanResult` — not raw `Turns[0]`). |
| `Work` | Primary work loop / orchestrator |
| `Explore` | Codebase exploration (never spawns subagents) |
| `Drone` | Delegated implementation; may spawn **Explore** only |
| `Security Review` | Security-focused review |
| `Bug Review` | Bug-focused review |
| `Custom` | Category label; lookup uses `Config.CustomAgents` keys |

System prompts come from `DysonAgentSystemPrompts.ForMode`, then (when an `IDysonModelRepository` is available) an **available-models catalog** is appended, then an **openrules** block (`DysonOpenRules.BuildSystemPromptBlock(Async)`: Root + provider-filtered AutoInclude Rules/Skills from work-root `openrules.json`, or implicit `AGENTS.md`; Paths may be local or http(s) URLs). See [docs/openrules/README.md](../openrules/README.md). That models catalog is what UI / `StartSubagent.modelSlug` can select; effort tags are freeform for API `reasoning_effort` / `StartSubagent.reasoningEffort`. Mid-session mode changes use `DysonAgentSession.ApplyAgentMode` (rebuilds `Mode` / `SystemPrompt`, bumps `SystemPromptGeneration` for cache invalidation — Completions `system` / Responses `instructions` are replaced each hop); the UI host applies on prompt submit when the composer picker differs, appends a completed **`ModeSwitch`** turn before the Normal user turn (history boundary only — short harness user message in the OpenAI transcript; not omitted like `DisplayInfo`), and persists `AgentMode` + `SystemPromptSnapshot`. Work / Explore / Drone directives cover orchestrator routing, Work/Drone Wait on any Explore they start, and mandatory `SubmitSubagentReport`. Explore/Drone directives harden “report is mandatory” / “complete or impossible”, including `failed` + failure-reason summaries. Every child first turn prepends `SubagentReportRequiredMandate` (failure-reason reports are valid finishes); Explore/Drone also get mode-specific first-turn blocks (`ExploreFirstTurnReportMandate`, `DroneFirstTurnContextMandate`). Plan’s first incomplete **Plan-stint** user prompt prepends `PlanFirstTurnMandate` (Explore before finalize; transcript-only; skips `ModeSwitch` / `DisplayInfo` / `PlanResult` — not raw `Turns[0]`). Security Review / Bug Review directives briefly mirror the same when used as subagents.

### Plan artifacts

In Plan mode, `SubmitPlan` (`title` + `markdown`) writes `.dyson/plans/{slug}-{sha1}.md` via `DysonFileManager`, appends a harness `PlanResult` turn (no LLM) with a turn `Instruction` that mandates updating that same `planPath` via `WriteFile` (no second `SubmitPlan` unless the user asks for a new plan), and returns `planPath` in the tool result. After `PlanResult`, the UI shows a composer Plan-ready sticky until a later `BeginBuildPlan` turn (`Host.BuildPendingPlanAsync` → folds any buffered Explore completion reports into the BeginBuildPlan Instruction, switches to Work, then `PromptBeginBuildPlanAsync`; sticky also dismisses on legacy `[BuildPlan]` user prompts). BeginBuildPlan is layout-only (Recap + Agent actions; no implementation that turn): Agent actions must prepare technical multi-Drone parallel workstreams (multitasking preferred over serial solo work; still no `StartSubagent` this turn). It mandates session todos via `CreateTodo` for each Agent actions item after `ReadFile` on the plan. After it succeeds, the host auto-enqueues a Normal continuation (`DysonBeginBuildPlanFlow.ContinuationPrompt`) that runs the implementation (preferring parallel Drone multitasking). Mutating product tools remain available (soft / prompt-only read-only); only `SubmitPlan` is hard-gated to Plan mode.

**Plan-mode Explore reports:** while the parent is in Plan, `SubagentCompleted` / `Failed` / `Stopped` interrupts still enqueue and update subagent UI, but the host does **not** auto-start a `SubagentReportProcessing` harness turn (`ShouldDrainCompletionAutoTurn` is Plan-only buffering). Those reports are injected into BeginBuildPlan when the user clicks **Build plan**, or drained as usual if the user leaves Plan without Build (mode picker / prompt). Completions already consumed by a successful `WaitForSubagent` this cycle are **not** folded or drained later. `SubagentEvent` auto-turns and Ask UI keep working in Plan.

## Orchestrator subagents

Primary flow: `StartSubagent` is **non-blocking**; the child runs in the background; the child calls **`SubmitSubagentReport`**; the parent gets a `SubagentCompleted` / `SubagentFailed` interrupt; the host **auto-queues a `SubagentReportProcessing` parent turn** with the report (FIFO if the parent is busy — does not cancel in-flight parent work). That turn analyzes the report, writes concrete continuation instructions, and proceeds with parent work in the same turn (tools allowed). The host **does not** FIFO that auto-turn when the parent **Waited** that child this cycle (in-flight `WaitForSubagent` or consume marker from a successful Wait); the Wait tool result already delivered the report. Drones (and other children) the parent did not Wait still get the notification turn. Sibling completions still notify. Timeout/cancel Wait does **not** consume — a later completion still notifies. Separate exception: while the parent is in **Plan** mode, non-consumed completion reports are buffered (no `SubagentReportProcessing` auto-turn) until **Build plan** folds them into `BeginBuildPlan`, or until the session leaves Plan without Build (then they drain as usual). Wait-consumed completions must not resurrect on leave-Plan / BeginBuildPlan.

| Tool | Behavior |
| ---- | -------- |
| `StartSubagent` | `CreateChildAsync` — persist child (`ParentSessionId`), register runtime id, background `PromptAsync`. Soft gates via `ValidateSubagentSpawn`. Optional `modelSlug` (slug or display alias) resolves via `IDysonModelRepository.FindSlugByNameAsync`; omit → settings default for Explore / Drone / Security Review / Bug Review when configured (`DysonAgentSessionConfig.*DefaultProvider`), else inherit parent provider (same kind only). Optional `reasoningEffort` (freeform); omit/null → chosen slug’s `DefaultReasoningEffort`; when inheriting parent model, omit keeps the parent’s current effort. Optional `contextFiles` (work-relative paths) loads via `DysonSkillLoader.ResolveAndLoadAsync` (`loadIndexOnly: true`) and attaches File context onto the child’s first turn (`[File: relative/path]` then contents) before persist; omitted/empty is a no-op |
| `ListSubagents` | Session-owned roster of **direct** children (`SubSessions`): JSON array of `subagentId`, `persistenceId`, `agentMode`, `title`, `status`, optional `modelLabel`. Use before Wait/Inspect/Stop when ids are missing from recent context (resume / compaction) |
| `WaitForSubagent` | Block until child terminal or `timeoutMs`. Work/Drone **always** Wait on Explore they start; do **not** Wait on Drones — prefer the notification turn; Plan still Wait **only** if the child’s result is a blocker for the next automatic turn. Successful terminal Wait marks `HasWaitConsumedCompletion` (timeout/cancel does not); host then skips `SubagentReportProcessing` for that child (`ShouldSuppressWaitedCompletionAutoTurn` while waiting **or** consumed). Sibling completions still notify |
| `InspectSubagentLog` | `SnapshotLog` for a subagent id |
| `StopSubagent` | Cancel child CTS; mark `Stopped`; notify parent |
| `SubmitSubagentReport` | Child-only handoff (`summary`, optional `status` completed\|failed); **hard-blocks** a successful (`completed`) report when the session has incomplete todos (`Pending`/`Ongoing`); `status: failed` is still allowed with incomplete todos (blocker handoff); empty todo list passes. Catalog instruction: before a `completed` report, call `ListTodos` first and complete pending/ongoing todos via `UpdateTodo` (prompt/catalog guidance; the runtime gate still reads session todos directly — no “must have called ListTodos this turn” executor check). Omitted from **root** Work/Plan/Ask catalogs (child-only; same structural-gate family as CompleteTask-on-children; gate is root vs child (`Parent is null`), not mode name — Ask/Work **children** still finish with this tool) via `DysonSessionToolsetBuilder.OmitSubmitSubagentReport` / `ConfigureRootInterAgentTools` (ctor uses `BuildInitial`, which still includes it until that gate). `Build(..., omitRootTaskCompletionTools: true)` (children) keeps the tool. `CreateDefault` / `AllCatalogTools` still list it for Settings denylist. Persists meta and notifies parent (parent summary stays the agent-provided `summary`). Summary may be a success handoff or, when `status` is `failed`, a concrete failure reason. If the child is already harness-`Failed` (e.g. kickoff missed a report), a later agent **`completed`** report **supersedes** that Failed status, replaces `LastReportSummary`, persists again, and **re-notifies** the parent. A successful submit sets **`EndsCurrentTurn`** (soft-closes the tool loop). Success **Content** keeps the JSON object, then a blank line, then trailing prose: `Report accepted. Do not call any more tools; end the turn.` Same-turn retries still **error** (`already submitted`) — including Failed→Failed and post-`Completed` retries on the same turn. A later child turn starts a new report cycle via `TryReopenForNewParentTask` at `BeginInFlightPrompt` (`Completed`/`Failed` → `Active` in memory; new `WaitForSubagent` cycle; `LastReportSummary` kept until the next accepted submit) — parent `TriggerSubagentEvent`, host `ShellExited`, or any other child `PromptHarnessTurnAsync`. Durable `Active` persist remains `TriggerSubagentEvent`’s persist helper. Do not reopen `Stopped`/`Interrupted`; `Stopped` still rejects a second submit. Terminal-reject errors (`already submitted` / `session already {Status}`) instruct the child to call `TriggerParentEvent` instead of retrying (`To communicate with the parent without a new report cycle, call TriggerParentEvent instead. After TriggerParentEvent, do not call any more tools; end the turn.`) — same-turn mid-run path after a successful submit. After **2 failed** `SubmitSubagentReport` results on the current turn (per-turn count of `ResponseLog` `IsError` submits; a same-round success with `EndsCurrentTurn && !IsError` wins), the harness auto-accepts the last parseable report (bypass the incomplete-todo gate on that path only; preserve requested status `completed`\|`failed`; append a harness auto-submit note to the summary), marks the child `Completed`/`Failed` (never `Stopped`), notifies the parent, force-cancels the child KickOff `runCts` (`CancelBackgroundRun`, not `StopSubagentAsync`), and the OpenAI-compatible loop SoftCloses and **returns** so it cannot issue another Completions/Responses request. Parent `TriggerSubagentEvent` still reopens `Completed`/`Failed` for new work (durable Active persist); a new child turn also reopens in-memory at `BeginInFlightPrompt`. The 2nd tool row stays **FAILED** (auto-accept is harness-side, not a third model tool call). |
| `TriggerParentEvent` | Child → parent: queue event (`kind`, `payload`), **block** until `RespondToSubagentEvent`. Fails immediately if parent is inside `WaitForSubagent` for **any** child (deadlock guard). |
| `RespondToSubagentEvent` | Parent completes a pending event (`subagentId`, `eventId`, `reply`). **Not** wait-gated — works mid-`WaitForSubagent` for already-pending events. |
| `TriggerSubagentEvent` | Parent → child inject (`payload`, optional `interruptSubagent`). Default queues next-turn prompt; `interruptSubagent=true` cancels in-flight turn + any parent-event wait, then `PromptAsync` immediately. Non-interrupt fails if child is awaiting a parent-event reply. Inject into a child that already reported (`Completed`/`Failed`) reopens it to `Active` so `SubmitSubagentReport` works again; success JSON includes `reopened: true|false`. |
| `AskQuestion` | Root-only: 1–8 clarifying / design questions via composer UI; blocks until answered (Q#/A# text; per-question Skip → `A# - [skipped]`). Prefer **PromptUserDialog** for concrete action choices. |
| `AskQuestionFromParent` | L1 only: wraps `TriggerParentEvent(kind=askQuestion)`; host Auto UI answers via internal `RespondToSubagentEvent`. |
| `PromptUserDialog` | Root-only: modal action picker (`title`, `description`, 1–4 actions, at most one `primary`); UI always adds non-primary **Skip**; blocks until chosen. Result JSON `{ action, skipped }` (Skip includes short guidance). Prefer **AskQuestion** for open-ended design clarification. |
| `PromptUserDialogFromParent` | L1 only: wraps `TriggerParentEvent(kind=promptUserDialog)`; host modal answers via internal `RespondToSubagentEvent`. |

**Spawn policy (prompt + soft enforce):**

- **Work** may start any built-in mode the task needs **except Plan**.
- **Plan child banned** as `agentMode` (Plan is top-level only). A Plan parent may `StartSubagent` Explore.
- **Explore** never spawns.
- **Drone** may spawn **Explore** only (Drone→Drone rejected by default).
- Prefer **Work-owned Explore → then Drone** over Drone-owned Explore when Work can supply context.
- **Work context-before-drones:** estimate whether the brief is rich enough; if not, Explore first, Wait for Explore reports before parent implementation or Drone dispatch, then deploy Drones with a rich brief so they often skip their own Explore.

**Layer catalog gating** (`ConfigureInterAgentTools(depth)`): root keeps `AskQuestion` / `PromptUserDialog` / `RespondToSubagentEvent` / `TriggerSubagentEvent` (omits FromParent + TriggerParentEvent); L1 keeps AskQuestionFromParent / PromptUserDialogFromParent + TriggerParentEvent + Respond + TriggerSubagentEvent (omits root Ask/Dialog); deeper keeps TriggerParentEvent + Respond + TriggerSubagentEvent only.

Soft spawn / restore / inter-agent event coverage: `Harness.Tests` (`DysonSubagentSpawnGateTests`, `DysonSubagentRestoreTests`, `DysonParentEventTests`, `DysonPromptUserDialogTests`) — `dotnet test src/Harness/Harness.Tests/Harness.Tests.csproj`. Return shape: `DysonStartSubagentResult` (`subagentId`, `persistenceId`, `agentMode`, `title`, optional `modelSlug` / `modelLabel`). Kickoff failures (no `SubmitSubagentReport`) mark the child `Failed`, persist status + parent interrupt, and notify with a non-empty reason (PromptAsync error → last turn snippet → harness message; exceptions as `{Type}: {Message}`). A later successful `SubmitSubagentReport` can supersede that harness Failed (see tool row above).

On parent resume the host hydrates direct DB children into `SubSessions` / `SubagentsById` via `RestoreRegisteredSubagent` (bumps next runtime id; does not raise `SubagentSpawned`).

## Message bus

Process-local typed pub/sub in `Harness.Engine/Messaging/`. `DysonMessageBus` is a sealed singleton (no `IDysonMessageBus`). Payloads implement the `IDysonMessageBusEvent` marker. The engine is the authority; hosts subscribe. UI `Publish` is allowed but discouraged.

**Keys** (`DysonBusScopes`; free-form strings also accepted):

| Helper | Shape |
| ------ | ----- |
| `Wildcard` | `*` — every key for that event type |
| `Session(Guid)` | `session:{persistenceId:D}` |
| `Subject(string)` | `subject:{subjectId}` |
| `Host(Guid)` | `host:{hostId:D}` (per-circuit UI host) |

**Delivery**

- `Publish` / `PublishAsync` → `VoidResult<string>`; `Subscribe` → `Result<IDisposable, string>`.
- Synchronous fan-out on the publisher thread over a snapshot list: exact key plus `DysonBusScopes.Wildcard`.
- No replay, sticky last-value, dispatch queue, or per-key ordering.
- Handler exceptions are logged and not thrown to the publisher. Sync `Publish` fire-and-forgets async handlers (failures logged); `PublishAsync` awaits them.
- Dispose the subscription token (idempotent). Empty lists drop their dictionary entry so per-session keys do not leak.

**Publish points**

- `DysonSessionEventPublisher.Attach(root)` returns a Result token. Recursively hooks the tree and follows `SubagentSpawned`. Per-session refcount so runtime **and** UI can Attach the same tree without double-publishing. Dispose the token to decrement.
- Status, spawn, and turn-added publish immediately. Activity (`DysonSubagentActivityChangedEvent`) uses a reused 75ms `DysonNotifyCoalescer` plus tuple dedupe `(title, LatestTurnStepTitle, isRunning)`.
- `DysonAgentSession.StatusChanged` is the choke point: raised **outside** `_terminalGate` from `TryMarkTerminal` / `TryAcceptSubagentReport` / `TryReopenForNewParentTask` when status actually changes.
- Host UI: `DysonUiHost` coalescer sink publishes `DysonHostStateChangedEvent` on `Host.BusScopeKey` (`DysonBusScopes.Host(HostId)`). This replaces `DysonUiHost.Changed` (deleted). If the bus is not injected, the host owns a private bus. `DysonSessionRuntime.Changed` / `DysonRuntimeChange` is unchanged (recovery/reattach).

| Record | Key | Payload |
| ------ | --- | ------- |
| `DysonSubagentSpawnedEvent` | parent + child session | `ParentPersistenceId`, `ChildPersistenceId`, `RuntimeId`, `Title`, `AgentMode` |
| `DysonSubagentStatusChangedEvent` | child + parent session | `PersistenceId`, `ParentPersistenceId`, `RuntimeId`, `Status`, `IsRunning`, `Summary` |
| `DysonSubagentActivityChangedEvent` | session (+ parent) | `PersistenceId`, `RuntimeId`, `Title`, `LatestTurnStepTitle`, `IsRunning` |
| `DysonSessionTurnAddedEvent` | session | `PersistenceId`, `TurnId`, `Kind` |
| `DysonHostStateChangedEvent` | host | `DysonHostChangeKind` mask, optional `SessionId` |

DI (`DysonUiWebHost`): `AddSingleton<DysonMessageBus>()` then `AddSingleton<DysonSessionEventPublisher>()` next to `DysonSessionRuntimeRegistry`. Runtime Attach on first `EnsureRegistered`; dispose token in UnhookSession. Host Attach tokens disposed in `DetachSessionUiHandlers`.

Covered by `DysonMessageBusTests` + `DysonSessionEventPublisherTests` in `Harness.Tests` (`dotnet test src/Harness/Harness.Tests/Harness.Tests.csproj`). Host delegation tests subscribe on `host.Bus` / `host.BusScopeKey`.

## Plugin subsystem

The native plugin subsystem under `Harness.Engine/Plugins/` keeps package ownership separate from user-authored `.dyson/skills`, `.dyson/mcp`, and `openrules.json` content.

### Package formats and acquisition

| Format | Detection and supported discovery |
| ------ | --------------------------------- |
| Agent Plugins v1 | Root `plugin.json` with the exact local `https://agent-plugins.org/schemas/1.0.0/plugin.schema.json` schema; immediate `skills/*/SKILL.md`; root `mcp.json`. Unknown manifest fields are diagnosed and ignored; schemas are never downloaded. |
| OpenAI / Codex | `.codex-plugin/plugin.json`; declared paths replace defaults for `skills`, `mcpServers`, and `hooks`. `.app.json` / `apps` are recorded as unsupported OpenAI-hosted connectors. |
| Cursor | `.cursor-plugin/plugin.json`; declared `contributes` paths replace defaults for skills, rules, agents, commands, hooks, MCP, and variables. A repository `.cursor-plugin/marketplace.json` can identify child packages, but an ambiguous repository requires an explicit child subdirectory. |

`IDysonPluginPackageService` accepts a local ZIP, a copied local folder, or an explicit GitHub repository/ref/subdirectory. GitHub refs are resolved to an immutable commit before download. Acquisition applies compressed/uncompressed byte, entry-count, depth, collision, traversal, and link/reparse checks; declared component paths are revalidated beneath the staged/package root. Preview computes a content checksum and retains a service-owned staged snapshot. Install reparses and rechecks the checksum, then promotes only after the caller supplies an explicit `Project` or `Global` target.

Preview and install do **not** execute hooks, scripts, commands, skill resources, or stdio MCP processes. They only read package metadata/assets and copy validated content. The UI also requires a separate capability acknowledgement before installation.

### Scope, catalog, and contributions

- Project packages: `{workRoot}/.dyson/plugins/{plugin-id}/{version-or-content-id}/`; persistent package data: `{workRoot}/.dyson/plugin-data/{plugin-id}/`.
- Global packages: `{DysonAppPaths.GetRoot(mode)}/plugins/{plugin-id}/{version-or-content-id}/`; persistent data: `{root}/plugin-data/{plugin-id}/`.
- Effective catalogs contain global installs plus only the active work directory's project installs. A project record shadows the same normalized global id even when the project record is disabled, preventing cross-scope component merging.
- `DysonPluginContributionResolver` revalidates installed paths. Plugin skills stay metadata-first and load through the existing `LoadSkill` / composer skill catalog without copying into `.dyson/skills`; provenance is persisted on `DysonContextFileEntry`.
- Only enabled `alwaysApply` plugin rules enter the bounded session prompt snapshot. Manual/glob rules remain distinct and are not auto-injected. Plugin agents are explicit custom-agent choices. Plugin commands appear as collision-safe `/plugin-{plugin}-{command}` composer entries and insert editable instructions; selection does not send inference automatically.
- Backend catalog inspection, enable/disable, and ownership-checked uninstall (retain/delete `PLUGIN_DATA`) exist. The current UI ships import only; installed-plugin settings, update/cross-scope-copy flows, variable configuration, and runtime-permission management are not yet exposed.

### Managed MCP and security foundations

`DysonPluginMcpResolver` and `DysonPluginMcpHost` provide a package-owned MCP runtime seam, but it is default-deny and is **not attached to session tool pipelines by the current UI host**. Installation enablement alone never starts a server. A caller must supply an explicit per-installation/server executable or network grant. Tools use `plugin__{pluginId}__{serverId}__{toolName}` names and reject deterministic namespace collisions.

The resolver supports declared `stdio`, `streamable-http`/`http`, and optional `sse`. Agent Plugins require an explicit transport; vendor formats may infer only from exactly one of `command` or `url`. Stdio commands are one executable token, plugin-relative `./` commands must resolve inside the package, cwd stays under `PLUGIN_ROOT` or same-scope `PLUGIN_DATA`, and only those two reserved variables receive single-pass expansion in args/env/cwd. Remote URLs must be absolute HTTP(S), use HTTPS outside loopback, contain no userinfo/fragment, and use literal non-injected headers.

Security persistence and services are foundations, not an active hook runtime:

- Cursor variables are declarations. `DysonPluginVariableService` validates declared names/types and stores encrypted, subject-bound, tamper-evident values; list/status output is redacted. The current UI does not configure them and the MCP resolver deliberately leaves non-reserved variables unavailable rather than reading ambient environment variables.
- Hooks are parsed with `EnabledByDefault = false`. `DysonPluginHookSecurityService` stores/revokes checksum-bound review grants for a narrow event/permission vocabulary and writes bounded metadata-only audits. No hook executor is wired, so importing/enabling a plugin cannot run hook content.
- OpenAI `.app.json`, arbitrary Cursor IDE/cloud integrations, lifecycle scripts, and automatic marketplace/catalog scraping are unsupported.

## MCP access

`DysonMcpAccessMode` on `DysonAgentSessionConfig`:

- **FullAccess** — tools run with full access; no allowlist.
- **AutoReview** — calls route through in-process `DysonMcpAutoReviewProxy`; no allowlist.

`DysonMcpPipeline` holds the per-session tool catalog (`FormatToolsForPrompt`) and optional auto-review proxy. OpenAI-compatible sessions also expose the same tools as native function schemas (with required `stage`).

**Custom MCP (live):** workdir-scoped clients under `{workRoot}/.dyson/mcp/{serverId}.json` (stdio + HTTP SSE / streamable / auto-detect). Master switch `mcpActive` lives in `work_directory_configurations` (default on). `DysonCustomMcpHost` + `DysonCustomMcpPromptUpdater` (FileSystemWatcher + ~300ms debounce) connect servers, merge namespaced tools `{serverId}__{toolName}` into the catalog, and bump `SystemPromptGeneration`. `DysonWorkspaceToolExecutor` dispatches those names via `CallToolAsync` before the built-in switch. See [work-directories](../storage/work-directories.md).

Workspace file tools, `ShellExecute`, web search/fetch, and browser tools still run locally via `DysonWorkspaceToolExecutor`.

**Toolset builder / mode policy:** `DysonSessionToolsetBuilder` builds the catalog (`CreateDefault` → shell/Plan + inter-agent + root/child completion omit → mode denylist). `DysonAgentSessionConfig.ToolPolicy` / `DisabledTools` come from `app_settings` (`agent_mode_tool_policy`) via the UI host; `ApplyAgentMode` **rebuilds** the pipeline so re-enabled tools return. Structural gates (no shells, browser null, inter-agent depth, CompleteTask omit on children, `SubmitSubagentReport` omit on roots) still win for availability. Per-model overlays exist on the document and resolver signature but are not applied yet. Executor rejects calls whose tool name is absent from the current catalog.

Default tools include subagent control (`StartSubagent`, `ListSubagents`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport` — child-only catalog; omitted from roots via `OmitSubmitSubagentReport` / `ConfigureRootInterAgentTools`; `CreateDefault` / `AllCatalogTools` still list it for Settings denylist), inter-agent events + Ask/Dialog (`TriggerParentEvent`, `RespondToSubagentEvent`, `TriggerSubagentEvent`, `AskQuestion`, `AskQuestionFromParent`, `PromptUserDialog`, `PromptUserDialogFromParent` — layer-gated), task completion (`CompleteTask`, `ConfirmTaskComplete`, `ContinueWork`), rethink resume (`ResumeCurrentTask`), workspace file tools (including temporary visualization assets and `RenderHtmlVisualization`; see [HTML visualizations](#html-visualizations)), **`LoadSkill`** (included / `.dyson/skills` / literal / openrules AgentOptional incl. URL fetch; provider-filtered; required `loadIndexOnly`; attaches `ContextFiles` `Kind.Skill` and transcripts as `[Skill: name]`; StartSubagent `contextFiles` uses the same loader with `Kind.File` / `[File: relative/path]`), **`GetOpenRulesConfig`**, **`InitializeOpenRules`**, **`GetDateTime`**, **`WaitForSeconds`**, **`JsonDynamicStructuredLanguageToolchain`** (see [json-dynamic-toolchain.md](json-dynamic-toolchain.md)), **`ShellExecute`** and **long-running shell tools** (when the platform has available shells), **browser tools** (when `BrowserControl` is set), **web search/fetch** tools (below), and related harness tools. Every call carries harness fields: optional `callId`, required `stage` (int). OpenRules schema / Providers / URL Paths: [docs/openrules](../openrules/README.md).

### HTML visualizations

`CreateFile` has an optional `isTempFile` flag (default `false`). Normal mode retains its existing destination and overwrite behavior. In temporary mode, `path` must be a leaf name with an extension; the executor sanitizes the name, inserts a random suffix before the extension, writes below `.dyson/temp/`, and returns the exact workspace-relative generated path in its JSON acknowledgement. Temporary content is limited to **512 KiB UTF-8**. `overwrite` must be omitted or `false`; generated files are git-ignored and are **not automatically deleted**. There is deliberately no separate `CreateTempFile` or cleanup tool.

Use the returned path verbatim in a later-stage `RenderHtmlVisualization` call; never invent a `.dyson/temp/` path. `RenderHtmlVisualization` requires a nonblank title (at most 120 characters) plus separate `html`, `css`, and `js` asset objects. Each asset selects exactly one source: raw `content` or an exact generated `tempFile` with the matching extension (`.html`/`.htm`, `.css`, or `.js`/`.mjs`). Mixed raw/file inputs are valid; empty raw CSS and JavaScript are valid, but HTML must be non-empty. The executor permits **256 KiB UTF-8 per resolved asset**, **512 KiB total resolved source**, and **20 successful visualizations per session**. Temp paths are constrained to generated `.dyson/temp/` artifacts, checked for the expected random-name shape and extension, and rejected when missing or traversing a symlink/reparse point.

A successful render returns a small JSON acknowledgement in `DysonToolCallResult.Content` and attaches the executable source as a typed `DysonHtmlVisualization` on `DysonToolCallResult.HtmlVisualization`. The ordinary turn-tool-state serialization persists that typed payload, including across session reload; provider transcript builders receive only the short acknowledgement, not the HTML/CSS/JS source.

`DysonUiThemeSnapshot` is a validated `light`/`dark` + lowercase `#rrggbb` value object (`Default` is dark / `#4c8bf5`). The host captures live DOM (`dysonTheme.getResolved`) at create/resume, on theme/accent change, and at each host `PromptAsync`, then stores it on settable `Config.UiTheme` (children share the parent config). Each `PromptWithTurnAsync` calls `ApplyUiTheme`, which replaces only the `RenderHtmlVisualization` catalog description via `ApplyVisualizationTheme` (no-op if the tool is omitted). Same theme+hex ⇒ identical tools JSON ⇒ the prompt-cache prefix survives; `SystemPromptGeneration` / `prompt_cache_key` are not bumped. Already-rendered visualization iframes are not restyled.

The UI renders the payload only in an inline iframe using `sandbox="allow-scripts"` and `referrerpolicy="no-referrer"`; it also aggregates current-session results in the **Visualizations** bar and modal. The generated iframe document applies a restrictive CSP: no default source or network connections, no external scripts/styles/fonts, no parent/page or workspace access, no forms, popups, downloads, top navigation, frames, or objects. Visualizations must be self-contained and use browser APIs, inline SVG/Canvas, or data/blob media rather than external dependencies. This is a built-in result format only: there is no general custom rich-MCP-result protocol, including for external MCP servers.

### GenerateImage

`GenerateImage` is a built-in tool only for sessions whose dedicated `DysonAgentSessionConfig.ImageGenerationProvider` was resolved from the Agent behavior setting. It is structurally omitted when that setting is empty, malformed, missing, disabled, invalid, or ineligible; an agent-mode policy may further deny it but cannot make an omitted tool available. `AllCatalogTools()` still includes it so policies can express that deny rule.

The required input is `prompt`. Optional inputs are `size`, `quality`, `style`, `background`, `outputFormat` (`png`, `jpeg`, or `webp`), and `count` (1–10; default 1), plus the standard harness `stage`. The application selects the API model from the setting—callers cannot override it. The request goes to OpenAI Images only, never to a generic compatible endpoint or the session chat model.

Every successful output is decoded and saved as a PNG beneath `.dyson/image-gen/`, even when the requested provider output format was JPEG or WebP. The tool result returns a compact acknowledgement plus `DysonGeneratedImageArtifact` metadata (validated relative PNG path, filename, `image/png`, dimensions, byte length, and model label/slug); it contains no image base64 or preview URL. Turn tool-state persistence retains only that metadata and revalidates it on restore. The UI recreates a temporary preview URL from the active work root at render time, displays accessible generated-image cards in the turn and tool detail, and revokes that URL when the card is disposed.

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

### ReadFile

Workspace file reads return at most **32KiB** (~<20K tokens). Larger requested slices **error** (`IsError`) with instruction to pass `offset`+`limit` or Grep first — the file body is **not** returned (not a truncated dump).

- `offset` is 1-based; **negative = tail** (`-80` = last 80 lines).
- Per-line read capped at **8KiB** (giant EF/SQL lines are clipped).
- Binary/image → error, use `LoadBinary`.
- Implemented via `IDysonWorkspaceFileSystem.ReadLineSlice` (never full-file `ReadAllText` for this tool).

### ShellExecute

- Session config `AvailableShells` is an `IReadOnlyList<DysonConfiguredShellSpec>` (`Name` + `ExecutablePath` + optional `FixedArgs`). Empty ⇒ `ShellExecute` and all long-running shell tools are omitted. The UI host loads **enabled** rows from `configured_shells` (`IDysonConfiguredShellRepository.EnsureDefaultsAsync` + `ListEnabledSpecsAsync`) on new/resume.
- MCP schema `shell` enum + description list those **names**; the model must pass `shell` plus `command` (optional `timeoutMs`, `workingDirectory` under the work root).
- Default cwd is the session workspace FS `NativeRootPath` (from `DysonWorkspaceFileSystems.CreateLocalAsync` / `IDysonWorkspaceFileSystem`); optional `workingDirectory` is resolved through the same sandboxed FS.
- Executor resolves name → path/`FixedArgs` against `AvailableShells`, then `DysonWindowsShell.ExecuteWithPathAsync` via `ResolveFixedArgs`: non-empty spec `FixedArgs` win; else basename heuristics (`pwsh`/`powershell` → `-NoProfile -NonInteractive -Command`, `cmd` → `/d /c`, `bash`/`sh`/`zsh`/`git-bash` → `-c`, `python`/`python3` → `-c`, `node`/`nodejs` → `-e`). Unknown basename without Fixed args errors (list includes `python`/`python3`/`node`/`nodejs`) and still tells the user to set Fixed args in Settings → Shells.
- **Capture cap:** stdout and stderr are captured up to **64KiB each**; overflow is truncated (the command may still run until timeout). `DysonShellRunResult.StdoutTruncated` / `StderrTruncated`. Do not kill the process solely for overflow.
- **Snippet contract:** When the session enum includes `Python` and/or `Node` (`OrdinalIgnoreCase` on those names only), `ShellExecute` and `StartLongRunningShell` descriptions append the matching half (or both): “When shell is Python, command is a raw Python snippet (passed to `-c`), not a file path or shell command line.” / “When shell is Node, command is a raw JavaScript snippet (passed to `-e`), not a file path or shell command line.” The `command` schema keeps “Command line to execute in the chosen shell.” / “Command line to run in the background.” and appends “ For Python, pass a raw Python snippet (`-c`).” and/or “ For Node, pass a raw JavaScript snippet (`-e`).” Plan warning still appends after. No `DysonShellType` values added.
- Related: `DysonConfiguredShellSpec`, `DysonShellType` (legacy/heuristics), abstract `DysonShell`, `DysonShellRunResult`, `ResolveFixedArgs` / `MapFixedArgsFromExecutablePath`.
- **Plan soft warning:** in Plan mode, `ConfigureShellExecuteForMode(true)` appends a read-only-inspection warning to the tool description (prefer `dir` / `git status` / small reads; never run builds/installs/servers; prefer `ReadFile` / `Grep` / `ListDirectory`). The executor still runs the command but prepends the same WARNING to Ok/Error content. Non-Plan modes use the plain description. Covered by `DysonShellExecutePlanWarningTests` in `Harness.Tests`.

### Long-running shells

Workdir-scoped background processes for E2E runs, large builds, and keeping development servers running (in-memory only — UI restart orphans OS children; only Abort/Cancel kill them). Prefer `ShellExecute` for one-shot commands.

| Type / tool | Role |
| ----------- | ---- |
| `DysonLongRunningShellRegistry` | Static workdir buckets; incremental `longRunningShellId` ints per workdir; shared by parent/child sessions; completion subscribers and waiters |
| `DysonLongRunningShell` | Process + stdin + stdout/stderr/combined rings (~256KB each, memory ceiling); stores configured `ShellName` |
| `DysonLongRunningShellExitedFlow` | `ShellExited` turn Instruction (auto-read tail) + trim after completion; Instruction uses configured shell name |
| `StartLongRunningShell` | `shell` (enabled name) + `command` (+ optional `workingDirectory`) → `longRunningShellId` |
| `ListLongRunningShells` | Compact roster for the workdir (`id`, `status`, `shell` = configured name, short `command`, `exitCode`, `startedUtc`) |
| `ReadLongRunningShellTail` | Tail combined output; `maxChars` default **8KiB**, **clamped to 64KiB** (model-facing only). Optional `timeoutMs` wait for new bytes |
| `AbortLongRunningShell` | Kill process tree (same as UI Force stop) |
| `RequestLongRunningShellCancellation` | Soft cancel (`\x03` stdin, else `CloseMainWindow`) |
| `LongRunningShellInteract` | Write stdin (+ newline if missing) |
| `SubscribeToLongRunningShellCompletion` | Parent-only (root sessions). Fire-and-forget one-shot subscribe → on terminal, `LongRunningShellExited` interrupt → host `ShellExited` auto-turn (always drained, including Plan); Instruction auto-reads tail (`includeTailMaxChars` same **64KiB** clamp) then trims it after the turn. Subagents must use `WaitForLongRunningShellCompletion`. |
| `WaitForLongRunningShellCompletion` | Root and child. Blocks the current tool round until the shell is terminal or required `timeoutMs` (> 0, no default) elapses. Already-terminal returns immediately. Timeout is success `status: "timeout"` (WaitForSubagent pattern). Cancellation errors. Does not subscribe and does not queue `ShellExited`. No `includeTailMaxChars` — after Wait, call `ReadLongRunningShellTail` in a later stage. JSON `{ longRunningShellId, status, shellStatus, outcome, exitCode }` (`status` `Exited` / `Aborted` / `timeout`). |

Same platform gate as `ShellExecute` (omitted when no shells). Plan soft-warns on `StartLongRunningShell` (description + result preamble). The 256KB ring stays as a memory ceiling; the 64KiB clamp only limits what enters the model. Covered by `DysonLongRunningShellTests` in `Harness.Tests`.

### Browser control

Optional process-wide `IDysonBrowserControl` on `DysonAgentSessionConfig.BrowserControl` (Windows: CefSharp WPF via `Harness.WindowsBrowser`; see [packaging/webview](../packaging/webview.md)). When null, browser tools are **omitted** from the MCP catalog.

| Tool | Behavior |
| ---- | -------- |
| `OpenBrowser` | Open WPF agent browser window; optional `url` / `width` / `height` → `windowId` + `tabId` |
| `ListBrowserWindows` / `CloseBrowser` / `ResizeBrowser` | Window list / close / resize |
| `ListBrowserTabs` / `NewBrowserTab` / `CloseBrowserTab` / `ActivateBrowserTab` | Tab management |
| `BrowserNavigate` / `BrowserGoBack` / `BrowserGoForward` / `BrowserReload` | Navigation |
| `ClearBrowserCache` | Clear shared CEF HTTP cache once, then hard-reload every tab in every open agent window (no args). Does not clear cookies/storage. Empty windows → `{ windows: 0, tabsReloaded: 0 }`. Profile-wide: agent + shell share `%LocalAppData%\DysonHarness\cef-cache` (shell is not hard-reloaded) |
| `BrowserClick` / `BrowserType` / `BrowserFill` / `BrowserHover` / `BrowserPressKey` | Interaction (JS helpers for click/type/etc.) |
| `BrowserWaitForSelector` / `BrowserWaitForNavigation` | Waits (optional `timeoutMs`; omitted default 60s) |
| `BrowserExecuteJavaScript` / `BrowserGetHtml` / `BrowserTakeScreenshot` | Page inspection (screenshot via DevTools CDP). JS/HTML evaluation races the linked timeout token so `BrowserExecuteJavaScript` cannot hang forever |
| `BrowserReadConsoleLog` / `BrowserReadNetworkLog` | Thin collectors (console messages + main-frame loads until CDP deepens) |

Every browser tool accepts optional `timeoutMs` (default **60000**). The engine wraps the call with a linked CTS. Wait and screenshot omitted defaults are also 60s. Prompt cancel still fails as cancelled, not timeout.

Contracts: `IDysonBrowserControl` / `IDysonBrowserWindow` / `IDysonBrowserTab` + request/log DTOs in `Harness.Abstractions`. Null stand-in: `DysonNullBrowserControl`.

### Web search / fetch (in-process)

Port of [agent-search-mcp](https://github.com/lennney/agent-search-mcp) as catalog tools under `Search/` (not a Node MCP server). Free engines (default order): **DuckDuckGo** HTML first, **Bing** RSS fallback (HTML SERP captcha-prone), **Wikipedia** OpenSearch tertiary; optional **Brave** when `BRAVE_API_KEY` or `DysonAgentSessionConfig.BraveApiKey` is set. Engine HTTP/parse failures surface in `meta.partial_failures` (e.g. `bing: HTTP 429`), not silent empty lists.

| Tool | Behavior |
| ---- | -------- |
| `FreeSearch` | Parallel free engines (`duckduckgo`, `bing`, `wikipedia`); tool-owned summary (skip if ≤~1500 tokens); optional `summarizePrompt` |
| `FreeSearchAdvanced` | Waterfall (DDG+Bing+Wikipedia → Brave if keyed), domain filters, optional WebFetch enrich; tool-owned summary; optional `summarizePrompt` |
| `SearchWithSynthesis` | Waterfall search + string `prompt_hint` (no LLM call for synthesis); tool-owned summary; optional `summarizePrompt` |
| `WebFetch` | Default page extractor. GET URL; summarize by default → summary only (`maxBytes` default **64KB**). `fullHtml: true` may still **download** up to **2MB** internally (`maxBytes` default **2MB**), but model-facing `Content` is **64KB** via the global Ok/Error cap (intentional: 2MB HTML ~500K tokens). Optional `summarizePrompt` (ignored when `fullHtml`). SSRF-guarded |
| `FetchGithubReadme` | `raw.githubusercontent.com` README for a GitHub repo URL; tool-owned summary; optional `summarizePrompt` |

**Result summarization:** runs **inside** `DysonWorkspaceToolExecutor` via `DysonWebSearchSummarizer.SummarizeAsync` before MCP `Content` is returned. By default the parent session / UI never sees raw SERP dumps or HTML — not even transiently. **Exception:** `WebFetch` with `fullHtml: true` may still download up to 2MB internally for summarization, but model-facing `Content` is **64KB** via the global Ok/Error cap (intentional: 2MB HTML ~500K tokens). Other web tools skip the LLM when already ≤ ~1500 tokens (`summarizePrompt` unused when skipped). Hard cap ≤ 10K tokens (`IDysonTokenCounter`); prompt text lives in `DysonWebSearchSummarizerPrompt` (editable constant; optional “Agent focus” from `summarizePrompt`). Optional dedicated model via `DysonAgentSessionConfig.SummarizerProvider` (null ⇒ session provider); UI: Settings → General → Web search summarizer.

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
- Hosts (e.g. `DysonUiHost`) watch completion interrupts and FIFO-auto-`PromptAsync` the parent with the report **unless** that child was Waited this cycle (`DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn`: waiting or consume marker, **or** in-flight BugReview). Still preferred over `WaitForSubagent` for Drones when the parent can multitask; not preferred over Wait for Work-owned Explore
- `SubagentEvent` (non-`askQuestion`): host shows an expandable **Subagent event** block (spinner while unaddressed) and FIFO-auto-prompts the parent to `RespondToSubagentEvent`
- `askQuestion` events: same Subagent-event block + Ask popover Auto UI; parent LLM does **not** auto-Respond

In-flight parent events are **not** persisted across process restart.
## Task completion flow

Root sessions only (subagents use `SubmitSubagentReport`). Pending follow-ups live on the session **`ConcurrentQueue<DysonAgentTurn>`** (`EnqueuePendingTurn` / `TryDequeuePendingTurn`); the UI host drains them into its prompt queue and runs each via **`PromptHarnessTurnAsync`** so kinds stay intact.

After the model calls `CompleteTask`:

1. **Confirm** — enqueue **`TaskCompletionConfirm`** (`DysonTaskCompletionFlow.CreateCompletionConfirmTurn`); on that turn only, `ConfirmTaskComplete` or `ContinueWork` are valid
2. **Continue** — `ContinueWork` enqueues a **`Continuation`** turn if work remains
3. **Report** — `ConfirmTaskComplete` enqueues a **`ReportSummary`** turn (final handoff)

`DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn` is true only for **`ReportSummary`**. That is the completion-boundary signal, not an unconditional host `TryMarkTerminal`. Factories: `DysonTaskCompletionFlow` and session helpers `CreateCompletionConfirmTurn` / `CreateContinuationTurn` / `CreateReportSummaryTurn`. Covered by `DysonTaskCompletionTests` in `Harness.Tests`.

### Automatic code review

After **`ReportSummary`**, a root session with a **non-empty todo list** is evaluated by **`DysonTaskLifecycleFlow`** (host handles the event; subagents never recurse into this path):

| Setting (`automatic_code_review`) | After `ReportSummary` |
| --------------------------------- | --------------------- |
| `none` | Mark/persist the root completed immediately |
| unsupported `high` | Append `Automatic code review level High is not implemented; review skipped.`, then complete normally |
| `low` / `medium` | Snapshot `git status --porcelain` paths (or include a scope diagnostic), enqueue one **`BugReview`** orchestration turn (UI label **Code review**), wait for exactly one Bug Review child without `StartSubagent.modelSlug`, then mark completed only when no pending/ongoing todo was introduced |

`automatic_code_review_action` controls the orchestration follow-up: `report_only` (the default; root and reviewer report confirmed bugs, risks, or no findings without changing files) or `automatically_fix` (the reviewer stays review-only; the root validates and fixes confirmed findings once, verifies them, and reports unresolved items without a review/fix loop).

If any todo is still `Pending`/`Ongoing` after an eligible substantive turn, a **`TaskEndReflect`** turn (UI label **Task end reflection**) includes a compact incomplete-todo snapshot so the agent updates todos instead of declaring success. Root reflection is lifecycle-event driven; children get one reflection immediately before their ordinary missing-`SubmitSubagentReport` failure path. Reflection must not retrigger from lifecycle/finalization kinds. Host Evaluate is delayed ~300ms before deciding; it does not start reflection while a host/runtime prompt is already queued, the session is still busy, or any descendant is Active. `TaskEndReflect` is not host/runtime-queueable (`AllowEnqueue` false); a leftover queued reflect is dropped (work is not done). When idle, the host starts reflection immediately rather than enqueueing it.

Turn kinds (append-only after `DropContext=13`): **`TaskEndReflect=14`**, **`BugReview=15`**, **`FullSummarize=16`**. Dedup is derived from turn history (no new DB column). Tests: `DysonTaskLifecycleTests` / `DysonAgentTurnKindDisplayTests` / `DysonFullSummarizeTests`. The settings UI persists `automatic_code_review` plus `automatic_code_review_action`; legacy `end_of_task_auto_review` / `self_review_intensity` values are migrated on first resolve.

## Rethink tool usage

When a non-Explore OpenAI-compatible turn exhausts its tool-round budget (**50**), the session soft-pauses (Success, harness H1 note) and enqueues **`RethinkToolUsage`** via `DysonRethinkToolUsageFlow.CreateTurn` — unless the exhausted turn was already rethink (no double-rethink). Pending turns drain through the same host queue as CompleteTask.

On the rethink turn only: readonly tools when a peek is needed; optional `StartSubagent` Explore with mandatory `WaitForSubagent` this turn; **`ResumeCurrentTask`** (`rationale` and/or `continuationInstructions` required) enqueues a **`Normal`** turn (`CreateResumeTurn`) with a fresh budget. Text-only reply means stop. Available to root and subagents.

**Explore** budget is **120**. Explore sessions never enqueue `RethinkToolUsage`; hitting the budget runs one final Completions/Responses call with tools cleared (`ExploreBudgetRecapInstruction`) so the model recaps findings and notes they may be incomplete. Covered by `DysonRethinkToolUsageTests` in `Harness.Tests`.

## Expand thought process

`ExpandThoughtProcess` MCP queues an `ExpandThoughtProcess` turn via `CreateExpandThoughtProcessTurn` / `DysonExpandThoughtProcess`, sets `EndsCurrentTurn` on the tool result, and the OpenAI tool loop soft-closes the calling turn (`SoftCloseAfterEndsCurrentTurn`) — no further model rounds. Recursion on an in-flight expand turn is rejected.

## Start new turn

`StartNewTurn(promptInstructions)` hard-ends the current turn (`EndsCurrentTurn`) and enqueues a **Normal** turn whose `Instruction` is the provided text (e.g. “write the second 50-word paragraph”). Callable anytime; not a substitute for ExpandThoughtProcess. Soft-close keeps same-round `reply.Content` when non-empty; otherwise uses a tool-specific harness note (`StartNewTurn` / `ExpandThoughtProcess` / generic). Host drains pending turns the same way as other queued follow-ups. Covered by `DysonStartNewTurnTests` / soft-close asserts in `DysonExpandThoughtProcessTests`.

During any turn the model may call **`SummarizeTurns`** (`turnIds` from `[turnId=…]` history headers, required `reason`) so a harness worker compresses each turn into a `ContextSummary` stub (≤ **2K** tokens via tiktoken; provider = settings `TurnSummarizerModelSlugId` or the session model). Skips turns that already have a summary or are claimed for summarization; host and MCP share session claims + a single-flight gate. Summarized turns stay in the UI (Summarized badge) but Completions/Responses transcripts emit only `[turnId=…]` + summary instead of full instruction/tools/assistant. Prefer summarize when useful facts remain. **`DropTurnContext`** (same args shape) sets `IsExcludedFromContext` for true noise; each newly dropped turn appends `Turn {id} dropped, reason: …`. **`RestoreTurnContext`** clears the drop flag and logs `Turn {id} restored, reason: …`. Excluded turns stay in the UI (Dropped badge + Restore) but are omitted from transcripts and context-optimizer walks. After expand completes, the host enqueues a Normal continuation (`ShouldEnqueueContinuation` / `ContinuationPrompt`). Covered by `DysonExpandThoughtProcessTests` / `DysonTurnSummarizerTests` in `Harness.Tests`.

### Max target context + DropContext inject

When estimated outgoing Completions/Responses tokens (same `OpenAiCacheFriendlyTranscriptBuilder` path + tiktoken; see `DysonOutgoingContextTokens`) exceed the effective max target (`session.MaxTargetContextTokens` → slug `DefaultMaxTargetContextTokens` → **100_000**; session **0** = Off), the next **Send** (`PromptWithTurnAsync`) may inject a `DropContext` turn (`DysonDropContextFlow`, keep last **4** turns) so the agent can prefer **`SummarizeTurns`** on verbose-but-useful older turns or **`DropTurnContext`** on true noise, then continues the original prompt (`allowDropContextInject: false` prevents nesting). Footer/idle overage does **not** inject — only the start of the next prompt.

**Live tool `Content` is hard-capped at the tool-result boundary** (`DysonToolResultLimits`: 64KiB on every Ok/Error; ReadFile fail-closes at 32KiB). DropContext / the optimizer do **not** protect the current turn — they only compact or drop **older** turns (`KeepRecentTurns` = 4 DropContext / 2 optimizer). Unbounded live tool results (the ~12M-token incident class) must be capped at the executor, not after the fact.

Gates: effective max > 0; not already in a DropContext phase; at least one non-excluded turn older than the keep-last-4 window; and a **5-user-turn throttle** after a DropContext (`Normal` | `InitializeSession` counts; first inject with no prior DropContext is immediate). Session log: `drop-context: inject (estimated=… max=…)` on inject, or `drop-context: skip (reason; estimated=… max=…)` when over a positive max but skipping (`in-phase`, `no-droppable-older`, `throttle`; Evaluate also returns `off` when max is 0). Covered by `DysonDropContextTests`.

### Full summarize

Composer **`/summarize-full`** (host `PromptFullSummarizeAsync`) queues a **`FullSummarize`** turn (`DysonFullSummarizeFlow` / `CreateFullSummarizeTurn`) whose reply is a durable session handoff (hard-capped at **6,000** characters). DropContext inject is skipped for this kind so the model sees the full history. After a successful prompt, runtime and host persist paths call `ApplyAfterCompletion`: trim the summary, set `IsExcludedFromContext` on every earlier in-context turn (session log `Turn {id} dropped, reason: Full summarize`), persist the FullSummarize reply, then upsert the newly dropped prior turns. The FullSummarize turn itself stays in later transcripts. No auto-continue. Distinct from **`/summarize`** (keep-last-2 worker). Covered by `DysonFullSummarizeTests` / persist hook in `DysonSessionRuntimeTests`.

## Context optimizer

`DysonContextOptimizer` (code-generated compaction, no LLM):

- Triggers on turn count or unoptimized token size (`IDysonTokenCounter`, default Tiktoken).
- Compacts **older** turns only (`KeepRecentTurns`); sets `ToolHistoryOptimized` + `CompactToolHistory` for prompt-cache stability. Does not protect the in-flight turn — live tool `Content` is capped at Ok/Error instead (see Max target context + DropContext inject).
- Compact lines use a harness-tagged shape (`[compact] {Tool} params: … || result: …`), not natural “Called … with params” prose.
- Cache-friendly Completions/Responses builders inject compacted history as **`role: user`** with a fixed harness prefix (historical summary only — do not imitate; use native tool calls). It is **not** emitted as assistant content.
- If a no-`tool_calls` model round echoes only compact-shaped lines (new or legacy), the session clears that body instead of storing it as `AssistantText`.
- Call `OptimizeContextIfNeeded` before building the next provider request.

Covered by `DysonContextOptimizerTests` in `Harness.Tests`.

## Result pattern

Public expected-failure paths return `Result<TValue, TError>`, `VoidResult<TError>`, or `ValueResult<TValue>` from `Harness.Abstractions` — see [rules/rules_csharp.md](../../rules/rules_csharp.md). Do not use exceptions for ordinary control flow.
