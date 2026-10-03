# Meta Agent mode

Never-blocking orchestrator plus isolated implementer. Page-launched; not in the composer mode picker. Storage: [plans.md](../storage/plans.md). UI: [UI README](../ui/README.md)#meta-agent-page. Pass 1 already covers the 15-turn tick and the 100K cap in [README.md](README.md)#meta-agent-maintenance-tick and [sessions.md](../storage/sessions.md)#meta-agent-turn-window — this page is the toolset, plan tools, and the child-report loop.

## Modes

`DysonAgentModes.MetaAgent` (`"Meta Agent"`) and `MetaAgentDrone` (`"Meta Agent Drone"`) are in `BuiltIns`. Neither is in `ComposerSelectable`. The page-launched root is not in the work-session list and is not user-deletable (`Meta Agent sessions cannot be deleted.`). `DeleteMetaAgent` still deletes terminal drones. Directives: `DysonAgentSystemPrompts.MetaAgentDirective` / `MetaAgentDroneDirective`. Child first turns prepend `SubagentReportRequiredMandate`; Meta Agent Drone also gets `MetaAgentDroneFirstTurnMandate`. Default provider: `DysonAgentSessionConfig.MetaAgentDroneDefaultProvider` (same omit-slug cascade as Drone). Settable at Settings → Agent behavior via `meta_agent_drone_model_slug_id` (effort: `meta_agent_drone_reasoning_effort`); empty inherits the parent chat model, and an explicit spawn `modelSlug` still wins.

`ValidateSubagentSpawn` (`DysonMetaAgentSpawnGateTests`):

- Meta Agent → Meta Agent Drone, Explore, Bug Review, or Security Review. Meta Agent itself cannot be a child (`"Meta Agent cannot be used as a subagent mode (page-launched only)."`).
- Meta Agent Drone → Explore, classic Drone, Bug Review, or Security Review. Nested Meta Agent Drone is rejected (`"No multi-layer Meta Agent Drones; spawn a Drone or Explore."`).
- Classic Drone is still Explore only (`"Drone may only spawn Explore subagents."`). Explore still cannot spawn, including these reviews (`"Explore cannot spawn subagents."`).
- Anyone else spawning Meta Agent Drone is rejected (`"Meta Agent Drone may only be spawned by a Meta Agent session."`).

## Toolset (allowlist strip)

`DysonSessionToolsetBuilder.ApplyModeCatalog` runs **after** default catalog, inter-agent depth, completion omit, plugin/custom merge, and the mode denylist. Meta Agent is an allowlist, not a denylist: everything not in `DysonMetaAgentTools.AllowedToolNames` is removed except `CreateBrowserTools` names already on the pipeline, shared schemas (`SummarizeTurns`, todos, `GetOpenRulesConfig`, `LoadSkill`) are restored from a no-browser `CreateDefault`, then meta-only tools are added. Browser tools are not copied back, so a denylist removal and a null `BrowserControl` both stay absent. `OmitRootTaskCompletionTools` also runs — a meta session never completes.

Enforced list: `DysonMetaAgentTools.ExcludedToolNames`, asserted by `DysonMetaAgentNoFileAccessTests` (catalog contains none of those names, does contain `LoadSkill` + `GetOpenRulesConfig`, and no plan-tool schema accepts a `path` argument). `DysonMetaAgentToolsetTests` asserts the live catalog **is exactly** `AllowedToolNames` when `BrowserControl` is null, and that a non-null control (including `DysonNullBrowserControl`) keeps every `CreateBrowserTools` name while file and shell tools stay absent.

Kept:

| Tool | Notes |
| ---- | ----- |
| `StartAsyncMetaAgentDrone` | `CreateChildAsync(MetaAgentDrone)`. Required boolean `useWorktree` (no default): file-mutating tasks (writing code, editing the repo) should set true (own worktree, merge on completion); non-coding tasks (ops, testing, CI, pushes, read-and-run) should set false (parent checkout, no branch, no merge). Optional `existingWorktreePath`: false plus a path rebinds to an already-listed worktree and does not call `Ensure` or set worktree columns; false without it stays on the registered checkout; true still forks; true plus a path errors before spawn. `BeginBuildPlan` still passes true. `purpose` `build` (default) or `plan` (`plan` prepends the explore-then-`SubmitMetaPlan` mandate onto `task`). Returns `{droneId, persistenceId, worktreeBranch}` (`worktreeBranch` null when false). Optional `contextFiles` (the only way the meta agent passes paths; still no filesystem tools). WriteTempFile and ReadTempFile are the only file tools, and they only touch generated files under .dyson/temp/. Never waits. |
| `StartAsyncExploreAgent` | `CreateChildAsync(Explore)` only. Returns `{agentId, persistenceId}`. Optional `context` and `contextFiles`. Never waits. Does not take `useWorktree` or `existingWorktreePath`. |
| `StartAsyncBugReviewAgent` | `CreateChildAsync(Bug Review)`. Optional `context` and `contextFiles`. Returns `{agentId, persistenceId}`. Never waits. No `BindOwnWorktree`, no `useWorktree`, no `existingWorktreePath`. |
| `StartAsyncSecurityReviewAgent` | `CreateChildAsync(Security Review)`. Optional `context` and `contextFiles`. Returns `{agentId, persistenceId}`. Never waits. No `BindOwnWorktree`, no `useWorktree`, no `existingWorktreePath`. |
| `ListMetaAgentDrones` | `FormatChildRosterJson(includeReports: true)` — `kind` (`drone` / `explore` / `other`), `worktreeBranch` (null for Explore), `lastReport`, `finishedAt`, plus the `ListSubagents` fields. Plain stable projection: no notices or counts (`DysonMetaAgentToolsetTests` byte-identical across calls). Prune pressure lives in the [maintenance tick](README.md#meta-agent-maintenance-tick), not here, so the cached prompt prefix is not busted on every dispatch. |
| `ReadMetaAgentDroneLog` | `InspectSubagentLog` / `SnapshotLog`. In-memory; empty after process restart even though the child session row survives. Shows full-text `USER COMMENT (injected mid-turn, turn {id8}): …` and `PARENT MESSAGE: …` lines (the `prompt:` line stays truncated to 120 chars). `MessageMetaAgentDrone` with interrupt keeps earlier queued parent messages (FIFO) and runs them after the interrupt turn. |
| `StopMetaAgentDrone` | `StopSubagentAsync`. Optional `discardWorktree` → `DysonSessionWorktree.RemoveAsync(..., force: true)` and clears worktree columns. |
| `MessageMetaAgentDrone` | `TriggerSubagentEventAsync` (reopens `Completed`/`Failed`). |
| `DeleteMetaAgent` | Terminal-only (walks descendants); then `_store.DeleteSessionAsync` (unmerged worktree still fails with the existing merge-or-delete message) and `UnregisterSubagent`. |
| `PostConversationMessage` | `AppendDisplayInfoTurn`. Optional `actions` of `{ name, func }`. `open_plan:{id}`, `open_file:{path}`, and `open_url:{url}` are built-in and resolved on click without `RegisterConversationAction`. Any other key still uses the in-memory map (empty after restart; not copied to children). A bad id, a path outside the work directory, a non-http(s) URL, an unknown key, or a failed func is `Result` text on the bubble and the message stays. `MetaAgentDirective` (Talking to the user) instructs the model to attach one action per plan, workspace file, or http(s) URL the user would open, on status updates, questions, and results, up to 8. Meta Agent Drone has no `PostConversationMessage`. Optional `visualizationId` is separate from `actions`: a GUID of a successful visualization on this session adds a button that is not a func. Unknown or malformed is a tool error and no turn. Still `{ok:true}` when it posts. Still does not end the turn. Still not in the provider transcript. Assistant text is not shown on the meta page — this is the user-visible channel. Not for questions; those go through `PostUserQuestion`. |
| `PostUserQuestion` | Non-blocking question card on a `DisplayInfo` bubble. Root allowlist plus `RejectUnlessMetaAgent` (not on the drone, Work, or Explore). Required `message` (bubble body), `question` (card title), and `choices` (2–12 unique strings). `multiSelect` defaults false; `allowCustomAnswer` defaults true. Optional `actions` share the `PostConversationMessage` cap of 8. No `visualizationId`. Returns `{ok:true, questionId}` and does not end the turn. The answer is a later Normal user turn (`HiddenInstruction` carries the question, because this bubble is not in the provider transcript). Submit locks the card in `turns.UserQuestionJson`; a failed send clears that attempt. |
| `CompactConversation` | Enqueues `DysonFullSummarizeFlow.CreateTurn()` and **ends the current turn**. |
| `RemoveTodos` | Runtime `_session.Mode == MetaAgent` check. `DeleteTodo` is not in the catalog. |
| `ListPlans` / `SetPlanStatus` / `BeginBuildPlan` / `DeletePlan` | Plan tools below. |
| `ReadMetaPlan` / `EditMetaPlan` | Plan-body tools, shared with the drone (one `CreatePlanBodyTools()` definition). The root cannot match `old_text` without the text, so it reads bodies on demand; `ListPlans` still never carries a body. Neither schema takes a `path`. See [Plan tools](#plan-tools). |
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

Structurally absent (not an exhaustive list — see `ExcludedToolNames`): file/shell/search tools except `WriteTempFile` and `ReadTempFile` (generated `.dyson/temp/` leaves only), `WaitForSubagent`, `StartSubagent` and the classic subagent quartet, `AskQuestion` / `PromptUserDialog` (they block; a question the user must decide uses non-blocking `PostUserQuestion` instead), completion tools, `SubmitSubagentReport`, `DropTurnContext` / `RestoreTurnContext`, `StartNewTurn` / `ExpandThoughtProcess`, `GetDateTime`, `RenameSession`, `InitializeOpenRules`, `SubmitPlan` / `EditPlan`.

### `LoadSkill` Literal gate

Root + AutoInclude bodies are already in the system prompt. `LoadSkill` still runs `DysonSkillLoader.ResolveAndLoadAsync`, then **post-filters** `Source == DysonSkillSource.Literal` in Meta Agent mode (`DysonMetaAgentTools.LoadSkillLiteralRejectedMessage`). Named skills, `.dyson/skills`, openrules AgentOptional, included `Resources/Skills`, and plugins pass. A work-relative path such as `docs/ui/README.md` is the hole this closes. Covered by `DysonMetaAgentLoadSkillTests`. Post-filter beats a second resolver so resolution changes cannot drift.

### Meta Agent Drone catalog

`ApplyDroneAllowlist`: Work catalog minus `AskQuestionFromParent` and `PromptUserDialogFromParent` only (`TriggerParentEvent` stays), plus `ReadMetaPlan`, `EditMetaPlan`, and `SubmitMetaPlan`. `PostUserQuestion` is not on the drone. It also adds `StartAsyncBugReviewAgent` and `StartAsyncSecurityReviewAgent` (not `StartAsyncExploreAgent`, not `StartAsyncMetaAgentDrone`). Those reviews use the same `CreateChildAsync` path as Explore and inherit the drone checkout. The drone waits with the `WaitForSubagent` it already has. `StartSubagent` is still how it spawns Explore and classic Drone. The review tools do not take `useWorktree` or `existingWorktreePath`. Questions and section-boundary status pings go up as `TriggerParentEvent` with kind `message`; the parent answers with `RespondToSubagentEvent` — immediately for a status (and posts it to the user), after the user decides for a question. `SubmitSubagentReport` stays. Covered by `DysonMetaAgentToolsetTests`.

Meta parents (`Meta Agent` and `Meta Agent Drone`) always auto-turn every child event, including `askQuestion` and `promptUserDialog`. The focused card may still open and is not the only delivery. Work, Ask, and Plan stay card-only for valid Ask/Dialog JSON. One re-inject (`DysonSubagentHostLogic.MetaParentEventRetryCap = 1`) then the child gets `Parent did not answer this event.` and the root Meta Agent gets a `DisplayInfo` turn. A root `DisplayInfo` parks the event until the next user message carries that same event in `HiddenInstruction`. Depth greater than 1 `askQuestion` / `promptUserDialog` fails fast (`Use kind message.`); the deadlock guard is unchanged. `BuildSubagentEventContinuationPrompt` carries the reply contract on every delivery (the first auto-turn, the one retry, and the parked `HiddenInstruction`), chosen from the receiving session's mode. The system-prompt parent-event sentences are only a backstop.

A parent-event continuation is kind `ParentEvent` (20): it stays in the session transcript and is not a meta-chat bubble, and a posted message may relay the status or question and must not name the event.

`StartAsyncMetaAgentDrone` requires boolean `useWorktree` (no default). File-mutating tasks should set it true: own git worktree and branch; a completed report merges under `MergeGates`. Non-coding tasks (ops, testing, CI, pushes, read-and-run) should set it false: parent's checkout, no `dyson/` branch, no merge. Optional `existingWorktreePath` is only valid with false: it rebinds tools to an already-listed checkout, does not call `Ensure`, and leaves worktree columns null so that drone's completed report does not merge. False without the path stays on the registered checkout. True still forks. True plus the path errors before spawn. `BeginBuildPlan` still passes true. Explore never merges. Classic drones inherit the parent drone checkout and do not merge on their own report. A content conflict aborts the registered checkout (usable again), leaves the drone worktree, and the failed `SubagentFailed` report is the resolve note. The parent spawns a resolver with `useWorktree` false and `existingWorktreePath` set to that worktree, does not `StopMetaAgentDrone`, does not pass `discardWorktree`, and does not edit files. The conflicted drone stays `Failed` with its columns until the parent messages it to `SubmitSubagentReport` completed, which retries the same merge. The worktree is removed only when that retry succeeds. `EnsureSessionWorktreeIfNeededAsync` still early-returns when `session.Parent is not null`.

## Plan tools

Every executor call is scoped by `_workDirectoryId` (the session's work-directory Guid, not the live worktree path). `IDysonPlanRepository.GetAsync` / `UpdateAsync` / `DeleteAsync` look up `Id == planId && WorkDirectoryId == workDirectoryId`. A `planId` alone can never cross work directories. That is load-bearing: `planId` is a sequential `long` (globally unique across the table), so `4` is guessable in a way a Guid is not. `planId <= 0` is rejected with `DysonMetaAgentTools.PlanIdMustBePositiveMessage` before the query. Runtime mode checks (`MetaAgent` vs `MetaAgentDrone`) sit in the executor — the catalog is not the security boundary.

Meta plan bodies live in the `plans` table against that work directory, not as `.dyson/plans/*.md` on a drone branch. `SubmitPlan` is unchanged (Plan-mode only).

Who does what: a plan is **authored** by a plan drone (`purpose: plan`: explore, then `SubmitMetaPlan`). It is **revised** with `EditMetaPlan` by the authoring drone, a build drone that finds the plan wrong or outdated, or the root Meta Agent for small comment-driven fixes (substantive revisions are relayed to the authoring drone with `MessageMetaAgentDrone`). The root prompt, the drone directive and mandate, the `purpose: plan` brief, `DysonMetaBuildBrief`, the `Plan comments on metaplan:…` turn (`DysonFileViewerComments.FormatPrompt`), and the `SubmitMetaPlan` / `EditMetaPlan` catalog descriptions all say so; `DysonEditMetaPlanTests.Prompts_briefs_and_descriptions_name_EditMetaPlan` pins that each names the tool.

### `ListPlans` JSON contract

Returns an array of objects with **exactly** `planId`, `title`, `status`, `buildAgentId`, `updatedUtc`. **No `planRelativePath`, no `markdown`, no `note`.** `IDysonPlanRepository.ListAsync` already omits `Markdown`; the tool projection also drops `PlanRelativePath` and `Note`. `DysonPlanStatusTests.ListPlans_serialized_json_omits_path_and_markdown` locks the JSON property set. Widening the payload is a regression against prompt-cache stability and the no-filesystem rule (a path in the roster is a path the agent can try to use).

`status` is the enum name lowercased (`draft` / `building` / `completed` / `stale`).

### `SetPlanStatus`

Accepts `building` / `completed` / `stale` only (`DysonMetaAgentTools.SetPlanStatusAcceptedMessage`). `draft` cannot be set back. Optional `note`. Does not clear `BuildAgentId`.

### `BeginBuildPlan`

`DysonMetaBuildBrief.Build(planId, title, extraInstructions)` — names the `planId` and tells the drone to call `ReadMetaPlan`; **never inlines markdown**. Unrelated to `DysonBeginBuildPlanFlow` (Plan-mode layout-only turn of the same English name). `DysonBeginBuildPlanToolTests` asserts the layout-only Instruction is absent from the brief.

Reuse: `agentId` → `TriggerSubagentEventAsync` with that brief. Otherwise spawn via the same `CreateMetaAgentDroneChildAsync` core (`useWorktree: true`, `purpose: build`). Then `UpdateAsync(..., status: Building, buildAgentId: persistenceId)`.

Second builder: `FindLiveBuilder` refuses a new spawn while a **non-terminal** child still matches `BuildAgentId` (`"Plan {id} is already being built by agent #{n}. Pass that agentId to extend the build, or SetPlanStatus stale first."`). A `Building` row whose builder is already terminal can start a new one. That is the live-builder check, not a `Status == Building` check.

### `DeletePlan`

Refuses while `Status == Building` (`"Call SetPlanStatus with stale, or stop the builder first."`). Status is the delete gate; `BuildAgentId` is not consulted.

### `SubmitMetaPlan` (drone-only) / `ReadMetaPlan` / `EditMetaPlan` (root and drone)

`SubmitMetaPlan` inserts `Kind = MetaPlan`, `Status = Draft`, or overwrites `Title`/`Markdown` of the **existing row in place** when `planId` is given (`UpdateAsync` on the same id; it never creates a second row, and a cross-workdir id is not found). Its description tells the model to prefer `EditMetaPlan` for revisions and to create large plans from a short skeleton. Returns `{planId, title, status}`. Writes **no file**. Cross-workdir `planId` is `"Plan '{id}' not found."`. Empty markdown is a Result error. After a successful create or revise (and after `SetPlanStatus` / `BeginBuildPlan` / `DeletePlan` succeed), the executor publishes `DysonPlansChangedEvent` on `DysonBusScopes.WorkDirectory` so an already-open meta page can refresh `MetaPlanList` without a reload.

It is **not** a report: no `EndsCurrentTurn`, does not call `SubmitSubagentReportAsync`, does not enqueue a parent interrupt. The drone must still `SubmitSubagentReport`. A plan-authoring drone that stops after `SubmitMetaPlan` is caught by the existing unfinished-work / child-report watch. Covered by `DysonSubmitMetaPlanTests`.

`ReadMetaPlan` returns `{planId, title, markdown, status}` for a row in **this** work directory, to the root Meta Agent and to drones. The `markdown` string is exactly what `EditMetaPlan` matches `old_text` against (no line-number prefixes, stored EOLs kept).

`EditMetaPlan` edits an existing plan in place, mirroring `WriteFile` with `planId` in place of `path`:

```json
{ "planId": 49,
  "old_text": "...", "new_text": "...", "replace_all": false,
  "edits": [ { "old_text": "...", "new_text": "...", "replace_all": false } ],
  "content": "...",
  "title": "optional rename", "summary": "optional revision note" }
```

- Exactly one of `content`, `old_text`+`new_text`, or `edits[]` per call; a mixture is rejected (`exactly one of`). `title` alone renames. Unlike `WriteFile`, `old_text` plus `edits[]` is rejected instead of concatenated, items in `edits[]` missing a string `old_text`/`new_text` are an error instead of skipped, and there is no `path`.
- Edits run through `DysonTextEditApplier.TryApplyEdits`, the same ordered all-or-nothing applier `WriteFile` and `UpdateNote` use: each edit sees the previous edit's result, `old_text` must match exactly once unless `replace_all`, and the first failure aborts with `edits[i]` and the match count (`old_text` for the single form). Nothing is written on failure (`Plan {id} is unchanged`).
- Persistence is `UpdateAsync` on the same row: `Markdown` (and `Title` when given) change, `UpdatedUtc` bumps, `Status` / `Note` / `BuildAgentId` are untouched. There is no revision counter or history column, so none is returned. `DysonPlansChangedEvent` is published once on success. An open `FileViewerOverlay` keeps the text it loaded; only `MetaPlanList` refreshes.
- Same-stage calls run concurrently, so read-modify-write runs under a per-`planId` gate (static dictionary in `DysonWorkspaceToolExecutor`, shared with `SubmitMetaPlan` revisions): two edits to one plan compose. The catalog description still asks for at most one call per plan per stage, with multiple hunks in `edits[]`.
- Result: `{planId, title, status, updatedUtc, mode, edits:[{index, replacements}], chars, lines}`. Errors are `Result` text: unknown or foreign `planId` (`Plan '{id}' not found.`), mixed modes, bad edit shape, not-found / ambiguous, or the repository's non-empty-`Markdown` rule.
- Review comments: `FileViewerOverlay` comments hold no stored anchor (an excerpt of the clicked block plus text, in memory until sent as a turn), so an edit cannot break one. See [plans.md](../storage/plans.md).

A `SubmitMetaPlan` / `EditMetaPlan` argument that is not valid JSON returns `"{tool}: invalid JSON arguments: {parser reason + position} ({n} chars received[, and they do not end with '}' ...]). Send a smaller payload ..."` pointing at `EditMetaPlan`. Provider adapters parse arguments once (`OpenAiCompatibleHttp.SplitStageFromArguments`); a malformed (not cut off) body still reaches the executor as raw text, still containing `"stage"`. A body that is truncated because the model's stream ended early or hit its output limit never gets that far: the adapter marks the call (`DysonToolCall.ArgumentsError`) and the scheduler returns `"{tool}: the call was not run. Its arguments were cut off at N chars because …. Submit a short skeleton with SubmitMetaPlan, then grow it with EditMetaPlan edits[]"`. See the cut-off rounds note in [README](README.md).

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
- `SubmitMetaPlan` is not a report. A drone that returns after it leaves the parent waiting until the child-report watch fires. Neither is `EditMetaPlan`.
- Resubmitting a whole large plan to revise it is the failure mode `EditMetaPlan` exists to avoid: one malformed or cut-off JSON argument loses the entire revision.
- `BuildAgentId` lingering after a completed/stale plan is expected. UI and `DeletePlan` use `Status`.
- Sequential `planId`s are guessable; always pass `_workDirectoryId`.
- `ReadMetaAgentDroneLog` / reminder counts / `LastReportSummary` die with the process. The child **session** row rehydrates; the log lines do not.
- `SubagentFailed` after a user Stop is a closed loop, not a failed build. Read `Status`.
- `useWorktree: false` without `existingWorktreePath` shares the parent checkout and must not switch branches. `existingWorktreePath` (only with false) rebinds to an already-listed worktree and that drone does not merge on completion. Two `useWorktree: true` drones editing the same files still conflict at merge. A failed report that starts with `Merge conflict.` is the resolve note, not a dead task: spawn the resolver, do not stop the conflicted drone, and do not edit files.
- `IDysonPlanRepository.UpdateAsync` cannot null `Note` / `BuildAgentId`. `DysonPlanKind.ClassicPlan = 0` is reserved; every row today is `MetaPlan`.
}
