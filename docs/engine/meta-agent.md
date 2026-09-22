# Meta Agent mode

Never-blocking orchestrator plus isolated implementer. Page-launched; not in the composer mode picker. Storage: [plans.md](../storage/plans.md). UI: [UI README](../ui/README.md)#meta-agent-page. Pass 1 already covers the 15-turn tick and the 100K cap in [README.md](README.md)#meta-agent-maintenance-tick and [sessions.md](../storage/sessions.md)#meta-agent-turn-window — this page is the toolset, plan tools, and the child-report loop.

## Modes

`DysonAgentModes.MetaAgent` (`"Meta Agent"`) and `MetaAgentDrone` (`"Meta Agent Drone"`) are in `BuiltIns`. Neither is in `ComposerSelectable`. The page-launched root is not in the work-session list and is not user-deletable (`Meta Agent sessions cannot be deleted.`). `DeleteMetaAgent` still deletes terminal drones. Directives: `DysonAgentSystemPrompts.MetaAgentDirective` / `MetaAgentDroneDirective`. Child first turns prepend `SubagentReportRequiredMandate`; Meta Agent Drone also gets `MetaAgentDroneFirstTurnMandate`. Default provider: `DysonAgentSessionConfig.MetaAgentDroneDefaultProvider` (same omit-slug cascade as Drone). Settable at Settings → Agent behavior via `meta_agent_drone_model_slug_id` (effort: `meta_agent_drone_reasoning_effort`); empty inherits the parent chat model, and an explicit spawn `modelSlug` still wins.

`ValidateSubagentSpawn` (`DysonMetaAgentSpawnGateTests`):

- Meta Agent → Meta Agent Drone or Explore only. Meta Agent itself cannot be a child (`"Meta Agent cannot be used as a subagent mode (page-launched only)."`).
- Meta Agent Drone → Explore or classic Drone. Nested Meta Agent Drone is rejected (`"No multi-layer Meta Agent Drones; spawn a Drone or Explore."`).
- Anyone else spawning Meta Agent Drone is rejected (`"Meta Agent Drone may only be spawned by a Meta Agent session."`).

## Toolset (allowlist strip)

`DysonSessionToolsetBuilder.ApplyModeCatalog` runs **after** default catalog, inter-agent depth, completion omit, plugin/custom merge, and the mode denylist. Meta Agent is an allowlist, not a denylist: everything not in `DysonMetaAgentTools.AllowedToolNames` is removed except `CreateBrowserTools` names already on the pipeline, shared schemas (`SummarizeTurns`, todos, `GetOpenRulesConfig`, `LoadSkill`) are restored from a no-browser `CreateDefault`, then meta-only tools are added. Browser tools are not copied back, so a denylist removal and a null `BrowserControl` both stay absent. `OmitRootTaskCompletionTools` also runs — a meta session never completes.

Enforced list: `DysonMetaAgentTools.ExcludedToolNames`, asserted by `DysonMetaAgentNoFileAccessTests` (catalog contains none of those names, does contain `LoadSkill` + `GetOpenRulesConfig`, and no plan-tool schema accepts a `path` argument). `DysonMetaAgentToolsetTests` asserts the live catalog **is exactly** `AllowedToolNames` when `BrowserControl` is null, and that a non-null control (including `DysonNullBrowserControl`) keeps every `CreateBrowserTools` name while file and shell tools stay absent.

Kept:

| Tool | Notes |
| ---- | ----- |
| `CreateAsyncMetaAgentDrone` | `CreateChildAsync(MetaAgentDrone)`. Required boolean `useWorktree` (no default): file-mutating tasks (writing code, editing the repo) should set true (own worktree, merge on completion); non-coding tasks (ops, testing, CI, pushes, read-and-run) should set false (parent checkout, no branch, no merge). `purpose` `build` (default) or `plan` (`plan` prepends the explore-then-`SubmitMetaPlan` mandate onto `task`). Returns `{droneId, persistenceId, worktreeBranch}` (`worktreeBranch` null when false). No contextFiles. WriteTempFile and ReadTempFile are the only file tools, and they only touch generated files under .dyson/temp/. Never waits. |
| `StartAsyncExploreAgent` | `CreateChildAsync(Explore)`. Returns `{agentId, persistenceId}`. Does take `contextFiles`. Never waits. |
| `ListMetaAgentDrones` | `FormatChildRosterJson(includeReports: true)` — `kind` (`drone` / `explore` / `other`), `worktreeBranch` (null for Explore), `lastReport`, `finishedAt`, plus the `ListSubagents` fields. Plain stable projection: no notices or counts (`DysonMetaAgentToolsetTests` byte-identical across calls). Prune pressure lives in the [maintenance tick](README.md#meta-agent-maintenance-tick), not here, so the cached prompt prefix is not busted on every dispatch. |
| `ReadMetaAgentDroneLog` | `InspectSubagentLog` / `SnapshotLog`. In-memory; empty after process restart even though the child session row survives. |
| `StopMetaAgentDrone` | `StopSubagentAsync`. Optional `discardWorktree` → `DysonSessionWorktree.Remove(..., force: true)` and clears worktree columns. |
| `MessageMetaAgentDrone` | `TriggerSubagentEventAsync` (reopens `Completed`/`Failed`). |
| `DeleteMetaAgent` | Terminal-only (walks descendants); then `_store.DeleteSessionAsync` (unmerged worktree still fails with the existing merge-or-delete message) and `UnregisterSubagent`. |
| `PostConversationMessage` | `AppendDisplayInfoTurn`. Optional `actions` of `{ name, func }` where `func` is a string key registered on the session. Buttons render on the meta bubble; click invokes that key. A missing key or a failed func is `Result` text on the bubble and the message stays. Optional `visualizationId` is separate from `actions`: a GUID of a successful visualization on this session adds a button that is not a func. Unknown or malformed is a tool error and no turn. Still `{ok:true}` when it posts. Still does not end the turn. Still not in the provider transcript. Assistant text is not shown on the meta page — this is the user-visible channel. |
| `CompactConversation` | Enqueues `DysonFullSummarizeFlow.CreateTurn()` and **ends the current turn**. |
| `RemoveTodos` | Runtime `_session.Mode == MetaAgent` check. `DeleteTodo` is not in the catalog. |
| `ListPlans` / `SetPlanStatus` / `BeginBuildPlan` / `DeletePlan` | Plan tools below. |
| `ListNotes` | Name and token count only. No body, no caps, no `allowed`. |
| `CanCreateNote` | Pre-write budget check. Optional `name` and `content`. No roster. Does not write. |
| `CreateNote` | New note. Fails if the name exists, if this would be note 21, or if a token cap would break. |
| `UpdateNote` | Same edit arguments as `WriteFile` (`content`, or `old_text`/`new_text`, or `edits`) on an existing note. Missing name is an error. |
| `DeleteNote` | Deletes one note. No token check. |
| `RenderHtmlVisualization` | Root allowlist only (the themed pipeline instance, not a second registration). Inline `content` or a `tempFile` whose path came from `WriteTempFile`. Ack JSON includes `visualizationId`. |
| `WriteTempFile` | Meta Agent only. Writes a generated leaf under `.dyson/temp/` via `CreateTemporaryFileAsync`. Not a project file. |
| `ReadTempFile` | Meta Agent only. Reads a generated `.dyson/temp/` leaf. Refuses every other path. |
| `GetOpenRulesConfig` / `LoadSkill` | Rules without files. |
| `SummarizeTurns` / `CreateTodo` / `ListTodos` / `UpdateTodo` | Shared schemas. |
| Browser tools (`CreateBrowserTools`) | Present only when `BrowserControl` is set and the name survived the denylist. Not in `AllowedToolNames`. `BrowserWaitForSelector` and `BrowserWaitForNavigation` return in this turn, bounded by required `timeoutMs`; they are not a stand-in for `WaitForSubagent`. A long `timeoutMs` stalls the orchestrator until the call returns. |

Todos are the record of dispatches; posted messages are not in later transcripts.

### Scratch notes

Root Meta Agent only (`ListNotes`, `CanCreateNote`, `CreateNote`, `UpdateNote`, `DeleteNote`). Not on the drone, Work, Explore, or Plan catalogs. Files are `.dyson/scratch/*.md` on the session work root, already gitignored by `**/.dyson/`. `DysonScratchNotes.CheckWriteAsync` is the one scan for the roster and the budget: 20 notes, 1000 tokens each (`DysonTiktokenTokenCounter`), 20000 total. `ListNotes` returns `{name, tokens}` sorted by name and nothing else. There is no `ReadNote`, and note text is not copied into the system prompt. `CanCreateNote` returns `allowed`, `reason`, `noteCount`, `totalTokens`, `noteTokens`, and the three caps — no names and no bodies. Create and update call that check; they do not remember that `CanCreateNote` ran. Delete has no cap. The directive still opens with "You cannot touch the filesystem" and does not name this directory.

Structurally absent (not an exhaustive list — see `ExcludedToolNames`): file/shell/search tools except `WriteTempFile` and `ReadTempFile` (generated `.dyson/temp/` leaves only), `WaitForSubagent`, `StartSubagent` and the classic subagent quartet, `AskQuestion` / `PromptUserDialog` (they block), completion tools, `SubmitSubagentReport`, `DropTurnContext` / `RestoreTurnContext`, `StartNewTurn` / `ExpandThoughtProcess`, `GetDateTime`, `RenameSession`, `InitializeOpenRules`, `SubmitPlan` / `EditPlan`.

### `LoadSkill` Literal gate

Root + AutoInclude bodies are already in the system prompt. `LoadSkill` still runs `DysonSkillLoader.ResolveAndLoadAsync`, then **post-filters** `Source == DysonSkillSource.Literal` in Meta Agent mode (`DysonMetaAgentTools.LoadSkillLiteralRejectedMessage`). Named skills, `.dyson/skills`, openrules AgentOptional, included `Resources/Skills`, and plugins pass. A work-relative path such as `docs/ui/README.md` is the hole this closes. Covered by `DysonMetaAgentLoadSkillTests`. Post-filter beats a second resolver so resolution changes cannot drift.

### Meta Agent Drone catalog

`ApplyDroneAllowlist`: Work catalog minus `AskQuestionFromParent` and `PromptUserDialogFromParent` only (`TriggerParentEvent` stays), plus `ReadMetaPlan` and `SubmitMetaPlan`. Questions and section-boundary status pings go up as `TriggerParentEvent` with kind `message`; the parent answers with `RespondToSubagentEvent` — immediately for a status (and posts it to the user), after the user decides for a question. `SubmitSubagentReport` stays. Covered by `DysonMetaAgentToolsetTests`.

A parent-event continuation is kind `ParentEvent` (20): it stays in the session transcript and is not a meta-chat bubble, and a posted message may relay the status or question and must not name the event.

`CreateAsyncMetaAgentDrone` requires boolean `useWorktree` (no default). File-mutating tasks should set it true: own git worktree and branch, merge on completed report. Non-coding tasks (ops, testing, CI, pushes, read-and-run) should set it false: parent's checkout, no `dyson/` branch, no merge. `BeginBuildPlan` passes true. Classic children still copy the parent's worktree columns. `EnsureSessionWorktreeIfNeededAsync` still early-returns when `session.Parent is not null`.

## Plan tools

Every executor call is scoped by `_workDirectoryId` (the session's work-directory Guid, not the live worktree path). `IDysonPlanRepository.GetAsync` / `UpdateAsync` / `DeleteAsync` look up `Id == planId && WorkDirectoryId == workDirectoryId`. A `planId` alone can never cross work directories. That is load-bearing: `planId` is a sequential `long` (globally unique across the table), so `4` is guessable in a way a Guid is not. `planId <= 0` is rejected with `DysonMetaAgentTools.PlanIdMustBePositiveMessage` before the query. Runtime mode checks (`MetaAgent` vs `MetaAgentDrone`) sit in the executor — the catalog is not the security boundary.

Meta plan bodies live in the `plans` table against that work directory, not as `.dyson/plans/*.md` on a drone branch. `SubmitPlan` is unchanged (Plan-mode only).

### `ListPlans` JSON contract

Returns an array of objects with **exactly** `planId`, `title`, `status`, `buildAgentId`, `updatedUtc`. **No `planRelativePath`, no `markdown`, no `note`.** `IDysonPlanRepository.ListAsync` already omits `Markdown`; the tool projection also drops `PlanRelativePath` and `Note`. `DysonPlanStatusTests.ListPlans_serialized_json_omits_path_and_markdown` locks the JSON property set. Widening the payload is a regression against prompt-cache stability and the no-filesystem rule (a path in the roster is a path the agent can try to use).

`status` is the enum name lowercased (`draft` / `building` / `completed` / `stale`).

### `SetPlanStatus`

Accepts `building` / `completed` / `stale` only (`DysonMetaAgentTools.SetPlanStatusAcceptedMessage`). `draft` cannot be set back. Optional `note`. Does not clear `BuildAgentId`.

### `BeginBuildPlan`

`DysonMetaBuildBrief.Build(planId, title, extraInstructions)` — names the `planId` and tells the drone to call `ReadMetaPlan`; **never inlines markdown**. Unrelated to `DysonBeginBuildPlanFlow` (Plan-mode layout-only turn of the same English name). `DysonBeginBuildPlanToolTests` asserts the layout-only Instruction is absent from the brief.

Reuse: `agentId` → `TriggerSubagentEventAsync` with that brief. Otherwise spawn via the same `CreateAsyncMetaAgentDrone` core (`purpose: build`). Then `UpdateAsync(..., status: Building, buildAgentId: persistenceId)`.

Second builder: `FindLiveBuilder` refuses a new spawn while a **non-terminal** child still matches `BuildAgentId` (`"Plan {id} is already being built by agent #{n}. Pass that agentId to extend the build, or SetPlanStatus stale first."`). A `Building` row whose builder is already terminal can start a new one. That is the live-builder check, not a `Status == Building` check.

### `DeletePlan`

Refuses while `Status == Building` (`"Call SetPlanStatus with stale, or stop the builder first."`). Status is the delete gate; `BuildAgentId` is not consulted.

### `SubmitMetaPlan` / `ReadMetaPlan` (drone-only)

`SubmitMetaPlan` inserts `Kind = MetaPlan`, `Status = Draft` (or overwrites `Title`/`Markdown` in place when `planId` is given). Returns `{planId, title, status}`. Writes **no file**. Cross-workdir `planId` is `"Plan '{id}' not found."`. Empty markdown is a Result error. After a successful create or revise (and after `SetPlanStatus` / `BeginBuildPlan` / `DeletePlan` succeed), the executor publishes `DysonPlansChangedEvent` on `DysonBusScopes.WorkDirectory` so an already-open meta page can refresh `MetaPlanList` without a reload.

It is **not** a report: no `EndsCurrentTurn`, does not call `SubmitSubagentReportAsync`, does not enqueue a parent interrupt. The drone must still `SubmitSubagentReport`. A plan-authoring drone that stops after `SubmitMetaPlan` is caught by the existing unfinished-work / child-report watch. Covered by `DysonSubmitMetaPlanTests`.

`ReadMetaPlan` returns `{planId, title, markdown, status}` for a row in **this** work directory.

### Status vs `BuildAgentId`

- **`Status` is authoritative for "is this building"** on the page and for `DeletePlan`. The UI spinner and the Build button both key off `Status` only (`MetaPlanList`: `BuildAgentId` can linger after a build ends). Clicking a plan opens `FileViewerOverlay` with display path `metaplan:{planId}/{slug}.md` from `GetAsync` (no disk). **Build** sends a session message; the Meta Agent calls `BeginBuildPlan` itself.
- **`BuildAgentId` is history.** It is set when a build starts and is **not cleared on completion** (`IDysonPlanRepository.UpdateAsync` cannot null it — null means leave unchanged). Do not treat a leftover id as "still building".

## Child-report watch (all parents)

Not Meta-Agent-only. A child that goes terminal without `SubmitSubagentReport` would otherwise leave every parent idle forever (Meta Agent never polls; Work `WaitForSubagent` would sit until timeout). `DysonChildReportWatch.Evaluate` is a pure function (`DysonChildReportWatchTests`):

| Inputs | Action |
| ------ | ------ |
| not a child / already `Completed` or `Failed` / pending or in-flight work | `None` |
| `Active`, idle, unreported, `remindersSent < 2` | `Remind` — enqueue `ChildReportReminder` (kind 19) on the **child** |
| `Active`, idle, unreported, reminders spent | `Synthesize` — child becomes `Failed` |
| `Stopped` / `Interrupted` / `Failed` without a report | `Synthesize` immediately |

`remindersSent` (`_childReportRemindersSent`) and `LastReportSummary` are **in-memory, per-process**. They are not session columns. After a restart the counter is gone.

### Synthetic report

`DysonAgentSession.SubmitSyntheticReportAsync` calls `SubmitSubagentReportAsync(..., failed: true, bypassIncompleteTodos: true, allowStoppedOrInterrupted: true)`. Same pipeline: `TryAcceptSubagentReport` → `NotifySubagentFailed` → `EnqueueInterrupt` → host `SubagentReportProcessing`. Idempotency is the existing "already submitted" guard (`_subagentReportAccepted`), not a second counter.

`KickOffChildPrompt` synthesizes on provider/exception failure; after a successful turn with nothing queued it runs `ApplyChildReportWatchAsync`.

### Stop semantics

A user halt (`DysonUiHost.StopAllExecution`) and `StopSubagent` / `StopMetaAgentDrone` mark the child **`Stopped`**, then synthesize.

`TryAcceptSubagentReport(..., allowStoppedOrInterrupted: true)` **keeps** `Stopped` (and `Interrupted`). A deliberate halt is not rewritten to `Failed`. The parent still receives `SubagentFailed` — that interrupt means **"this child is done and will not report"**, not "the work failed". `ListMetaAgentDrones` and the UI cards read `Status`, which stays `Stopped`. Covered by `DysonSyntheticReportTests`.

The widened guard is harness-only. An agent's own second `SubmitSubagentReport` still rejects (`"session already Stopped"`). A second synthetic also rejects (Stop + cancelled-run KickOff both reach the same stop; the parent must be told once).

Idle-without-report after two reminders **does** set `Failed` — that child was still `Active`.

### Recovery

`DysonSessionRecoveryService` has no live session graph. For each Active descendant it marks `Interrupted` and appends one parent `Interrupt` log (`SubagentFailed`, summary `application restart`, child's `RuntimeId` / persistence id) via `PersistSyntheticParentReportAsync`. Idempotent if that log already exists. It does **not** call `SubmitSyntheticReportAsync` (nothing is in memory to accept a report). It does not replay model or tool calls. A persisted reminder counter would never be read: Interrupted descendants synthesize immediately on this path.

## Maintenance tick and 100K cap

See [README.md](README.md)#meta-agent-maintenance-tick and [sessions.md](../storage/sessions.md)#meta-agent-turn-window for the numbers (`IntervalTurns = 15`, `MaxTurns = 40`, `StaleAgentThreshold = 20`, live count ~40–54).

**Why one tail turn, not a roster notice:** `ListMetaAgentDrones` is called on every dispatch. A varying `notice` / count on that result would change the prompt prefix every turn and destroy prompt caching. Evicting per turn past 40 would miss the cache continuously. Batching eviction **and** the prune request onto one `MetaMaintenance` turn (kind 18) at the tail shares a single cache break every 15 completed turns. `MetaMaintenance` turns do not increment the counter.

`SelectEvicted` never takes the in-flight turn, an incomplete turn (`CompletedUtc` null — this is also how an unprocessed `MetaMaintenance` survives), or the newest `FullSummarize`. Independent of DropContext (token guard) and `CompactConversation`.

**100K is not configurable** for Meta Agent: `ResolveEffectiveMaxTargetContextTokens` returns `DysonMaxTargetContextTokens.HarnessDefault` and ignores session/slug overrides including `0 = Off`. `DysonUiHost.SetSessionMaxTargetContextTokensAsync` rejects with `"Meta Agent context is fixed at 100K."`. Composer hides the ± stepper when `SelectedAgentMode` is Meta Agent (`ContextLimitLocked`). Meta Agent Drones keep the normal cascade. Covered by `DysonMetaAgentContextCapTests`.

## Traps

- `ListPlans` JSON is a closed property set. Adding `markdown` / `path` / `note` / a prune `notice` is a contract break (`DysonPlanStatusTests`).
- `LoadSkill` with a file path in Meta Agent mode is the silent hole; the Literal post-filter is the whole no-filesystem design (`DysonMetaAgentLoadSkillTests`).
- `SubmitMetaPlan` is not a report. A drone that returns after it leaves the parent waiting until the child-report watch fires.
- `BuildAgentId` lingering after a completed/stale plan is expected. UI and `DeletePlan` use `Status`.
- Sequential `planId`s are guessable; always pass `_workDirectoryId`.
- `ReadMetaAgentDroneLog` / reminder counts / `LastReportSummary` die with the process. The child **session** row rehydrates; the log lines do not.
- `SubagentFailed` after a user Stop is a closed loop, not a failed build. Read `Status`.
- `useWorktree: false` drones share the parent checkout and must not switch branches. Two `useWorktree: true` drones editing the same files still conflict at merge.
- `IDysonPlanRepository.UpdateAsync` cannot null `Note` / `BuildAgentId`. `DysonPlanKind.ClassicPlan = 0` is reserved; every row today is `MetaPlan`.
}
