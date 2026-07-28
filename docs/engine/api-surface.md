# Engine API surface

Public types in `Harness.Engine` that hosts and UI typically bind to. Namespace: `DysonHarness`.

Conceptual overview: [README.md](README.md).

## Core host

| Type | Notes |
| ---- | ----- |
| `DysonEngine` | Abstract; exposes `RootSession` |
| `DysonAgentSession` | Abstract session: mode, prompt, MCP pipeline, subagents, interrupts, log, turns, optimizer hooks |
| `DysonAgentProvider` | Abstract ephemeral model provider (no durable state) |
| `OpenAiCompatibleAgentProvider` | OpenAI-compatible ephemeral provider (`BaseUrl`, `ApiKey`, `Slug`, `OpenAiApiMode`, optional `ManagedSource`, optional `ReasoningEffort`, …). Completions send top-level `reasoning_effort`; Responses send nested `reasoning.effort` |
| `OpenAiCompatibleAgentSession` | Completions/Responses tool-loop session; `MaxToolRounds` 35 / Explore 120 (`ResolveMaxToolRounds`); soft-pause → `RethinkToolUsage` via `SoftPauseAfterToolLoopExhaustion` (non-Explore); Explore budget hit → no-tools recap |
| `OpenAiCompletionsClient` / `OpenAiResponsesClient` | Streaming SSE adapters (`StreamCreateAsync` → `OpenAiStreamChunk`); Responses body uses nested `reasoning.effort`; `store`/`include`/`previous_response_id` follow chaining gate; `call_id` from model only (never `fc_*`/Guid); merges tools from `response.completed.output` |
| `OpenAiFilesClient` | Multipart `POST …/files`; images `purpose=vision`, non-images `purpose=user_data`; `EnsureBinaryFileIdsAsync` sets `DysonBinaryAttachment.FileId` before Responses transcript build (data-URL fallback on failure) |
| `OpenAiCacheFriendlyTranscriptBuilder` | Stable-prefix transcript + `prompt_cache_key`; each history turn’s user content is prefixed with `[turnId={guid}]`; after Instruction, emits one user message per `SkillsUsed` entry as `[Skill: {DisplayName}]\n\n{MarkdownContent}`; skips `IsExcludedFromContext` turns. Responses: direct = `store: true` + delta/`previous_response_id`; managed = `store: false` + reasoning→call→output replay. Explicit breakpoints via `SupportsExplicitPromptCache` (GPT-5.6+ and not managed). Binary attachments: Completions nested `image_url` data URL; Responses `file_id` preferred (see README vision matrix). **UserImages** on a turn become multimodal parts on that turn’s user message (persisted; re-emitted in history — not one-shot) |
| `DysonWorkspaceToolExecutor` | Workdir-scoped file tools (via `IDysonWorkspaceFileSystem`) + `RenameSession` + `GetDateTime` + `WaitForSeconds` (1–300) + **`JsonDynamicStructuredLanguageToolchain`** (nested JDSL over catalog tools; see [json-dynamic-toolchain.md](json-dynamic-toolchain.md)) + **`LoadSkill`** (`name` + required `loadIndexOnly`; resolve included → `.dyson/skills` → literal; attaches `SkillsUsed` on current turn) + `SubmitPlan` (Plan mode only → `.dyson/plans/` + PlanResult turn) + `ShellExecute` + **long-running shell tools** (`StartLongRunningShell` / `ListLongRunningShells` / `ReadLongRunningShellTail` / `AbortLongRunningShell` / `RequestLongRunningShellCancellation` / `LongRunningShellInteract` / `SubscribeToLongRunningShellCompletion`) + web search/fetch tools (tool-owned summarize) + **subagent tools** (`StartSubagent` / `ListSubagents` / `WaitForSubagent` / `InspectSubagentLog` / `StopSubagent` / `SubmitSubagentReport`) + **inter-agent / Ask** (`TriggerParentEvent` / `RespondToSubagentEvent` / `TriggerSubagentEvent` / `AskQuestion` / `AskQuestionFromParent`) + **task completion** (`CompleteTask` / `ConfirmTaskComplete` / `ContinueWork`) + **`ResumeCurrentTask`** (rethink phase) + **session todo tools** (`ListTodos` / `CreateTodo` / `UpdateTodo` / `DeleteTodo`) + **browser tools** (when `BrowserControl` set: `OpenBrowser`, `ListBrowserWindows`, `CloseBrowser`, `ResizeBrowser`, tab/nav/click/type/JS/screenshot/log helpers); stubs for the rest |
| `DysonSkillLoader` / `DysonLoadedSkill` / `DysonSkillCatalogEntry` / `DysonSkillSource` | Resolve/load skills: included embedded `Resources/Skills` → `.dyson/skills/{name}` → literal work-relative path; `loadIndexOnly` entry vs full dir concat; `ListCatalog` for slash searcher |
| `DysonFileManager` | Work-root sandbox helper over `IDysonWorkspaceFileSystem`: `WriteNewPlan` / `ReadText` / `EnsurePlansDirectory` under `.dyson/plans/` |
| `IDysonWorkspaceFileSystem` / `DysonWorkspaceSubjects` / `DysonWorkspaceEntry` | Sandboxed workspace IO; `InitializeAsync(subjectId)` (local: `"local_fs"`); `NativeRootPath` for shells/git |
| `IDysonWorkspaceChangeWatcher` / `DysonWorkspaceChangeKind` / `DysonWorkspaceChangeEventArgs` | Live FS change notifications from an initialized workspace FS |
| `DysonLocalWorkspaceFileSystem` / `DysonWorkspaceFileSystems` | Local/SMB/UNC path-backed FS + `CreateLocalAsync` factory |
| `DysonShell` / `DysonWindowsShell` | Shell runners; path-based execute + basename fixed-arg heuristics; legacy `DysonShellType` map kept for tests |
| `DysonConfiguredShellSpec` / `DysonShellType` / `DysonShellRunResult` | Session shell name+path+optional FixedArgs; legacy enum; process result |
| `DysonConfiguredShellEntity` / `DysonConfiguredShellStore` | Persisted shells (`configured_shells`, optional `FixedArgsJson`); seed defaults; list enabled specs |
| `DysonLongRunningShellRegistry` / `DysonLongRunningShell` | Workdir-keyed in-memory background shells (rings, Abort/Cancel/Interact/List/Subscribe); identity via `ShellName`; not persisted across UI restart |
| `DysonLongRunningShellStatus` / `DysonLongRunningShellInfo` / `DysonLongRunningShellTail` | Status enum + list/tail DTOs (`ShellName` on info) |
| `DysonOpenAiApiModes` | `Completions` / `Responses` constants |
| `DysonAgentSessionConfig` | `CustomAgents`, `McpAccessMode`, `AvailableShells`, optional `BraveApiKey`, optional `SummarizerProvider`, optional `BrowserControl` (`IDysonBrowserControl`), optional `DisabledTools` / `ToolPolicy` (mode denylist; see MCP) |
| `DysonAgentSessionEvent` | Abstract notify payload for `WaitForNotifyAsync` |

### Managed / CLIProxy

| Type | Notes |
| ---- | ----- |
| `DysonManagedSources` | Managed-source constants (`cliproxy-codex`, `cliproxy-grok`, `cliproxy-antigravity`, `cliproxy-kimi`, `cliproxy-claude`) |
| `ManagedEndpointKind` | `OpenAiCompatible` / `AnthropicCompatible` (Anthropic Messages session path reserved, not shipped) |
| `ManagedInferenceProviderBase` | Shared Import / BeginConnection / CompleteConnection / VerifyConnection for CLIProxy-backed providers |
| `ManagedCodexInferenceProvider` | ChatGPT Codex managed path (`ManagedSource=cliproxy-codex`, `codex-auth-url?is_webui=true`, OAuth port 1455 preflight) |
| `ManagedGrokInferenceProvider` | Grok Build managed path (`ManagedSource=cliproxy-grok`, `xai-auth-url`) |
| `ManagedAntigravityInferenceProvider` | Antigravity managed path (`ManagedSource=cliproxy-antigravity`, `antigravity-auth-url?is_webui=true`, OAuth port 51121 preflight) |
| `ManagedKimiInferenceProvider` | Kimi managed path (`ManagedSource=cliproxy-kimi`, `kimi-auth-url`) |
| `ManagedClaudeInferenceProvider` | Claude Code managed path (`ManagedSource=cliproxy-claude`, `anthropic-auth-url?is_webui=true`, OAuth port 54545 preflight; OpenAI/Responses via proxy) |
| `ManagedInferenceProviderCatalog` | DI catalog of managed providers; `FindBySource` |
| `ManagedConnectionBegin` / `Complete` / `Verify` | Connection-flow DTOs |
| `DysonCliProxyHost` | Local CLIProxy process host (`IsInstalled`, `EnsureInstalledAsync`, `EnsureRunningAsync`, `LocalBaseUrl`) |
| `DysonCliProxyDownloader` / `DysonCliProxyPaths` / `DysonCliProxyAssetResolver` | Download, unpack paths, asset URL resolution |
| `DysonThirdPartyResources` | Pinned third-party release URLs (`CliProxyApi.ReleaseTagUrl` / `Version`) |
| `OpenAiCompatibleHttp.SupportsExplicitPromptCache` | True for direct GPT-5.6+ slugs when `ManagedSource` is unset |
| `OpenAiCompatibleHttp.SupportsResponsesServerChaining` | True when `ManagedSource` is unset (direct OpenAI store+`previous_response_id`); false for CLIProxy managed → local item replay |
| `OpenAiCompatibleHttp.IsUsableResponsesCallId` / `IsMissingToolCallForOutputError` | `call_id` must be usable (not `fc_*`/Guid); detector for the known store-chain 400 |

### Session members (high level)

- Identity: `Id` (runtime int; root `0`)
- Persistence (when wired): `PersistenceId` (`Guid`), `DisplayTitle`, `Turns`, `TurnAdded`, `AddTurn`, `RestoreFromPersisted`
- Todos: `Todos` (`IReadOnlyList<DysonSessionTodo>`), `TodosChanged`, `RestoreTodos`, `ListTodosAsync` / `CreateTodoAsync` / `UpdateTodoAsync` / `DeleteTodoAsync` / `ReplaceTodosAsync` (persist when `PersistenceId` set)
- Rename: `RenameAsync(title)` → validates (trim, max 120) → sets `DisplayTitle` → raises `SessionRenamed` (`DysonSessionRenamedEventArgs`: `PersistenceId`, `Title`); host/tool executor persists `sessions.Title`
- Config / mode: `Config`, `Mode`, `SystemPrompt`, `SystemPromptGeneration`, `ApplyAgentMode`, `McpPipeline`, `Provider`
- Subagents: `Parent`, `SubSessions`, `RegisterSubagent`, `RestoreRegisteredSubagent` (resume re-link + next-id bump; no `SubagentSpawned`), `FormatListSubagentsJson`, `CreateChildAsync` (optional `initialTodos` seed), `WaitForSubagentAsync` (tracks `WaitingOnSubagentIds` / `IsWaitingOnAnySubagent`), `InspectSubagentLog` (sync), `StopSubagentAsync`, `SubmitSubagentReportAsync` (`skipTasksCheck` gates incomplete session todos; harness-`Failed` may be superseded by a later agent report; post-`Completed` retries are idempotent success), `TryAcceptSubagentReport`, `ValidateSubagentSpawn`, `TriggerParentEventAsync` / `RespondToSubagentEvent` / `TriggerSubagentEventAsync`, `AskQuestionAsync` / `AskQuestionFromParentAsync` / `RespondToAskQuestion`
- Interrupts: `EnqueueInterrupt`, `TryDequeueInterrupt`, `WaitForInterruptAsync`; `NotifySubagentCompleted` / `Stopped` / `Failed` (include optional child `PersistenceId`); `SubagentEvent` kind with `EventId` / `EventKind` / `Payload`; `LongRunningShellExited` with `LongRunningShellId` / `ExitCode` / `ShellOutcome` / `IncludeTailMaxChars`
- Log: `AppendLog`, `SnapshotLog`, `LogAppended`
- Turns / context: `CreateExpandThoughtProcessTurn`, completion-turn helpers, `EnqueuePendingTurn` / `TryDequeuePendingTurn` / `ClearPendingTurns`, `IsInTaskCompletionConfirmPhase`, `IsInRethinkToolUsagePhase`, `IsInExpandThoughtProcessPhase`, `OptimizeContextIfNeeded`
- Loop: `LoadFunctionalContextAsync`, `PromptAsync`, `PromptHarnessTurnAsync`, `PromptBeginBuildPlanAsync`, `PromptSubagentReportProcessingAsync`, `PromptShellExitedAsync`, `WaitForNotifyAsync`

## Modes & prompts

| Type | Notes |
| ---- | ----- |
| `DysonAgentModes` | Built-in mode name constants (`Plan` top-level only) |
| `DysonProviderKinds` | Known provider-kind strings (`demo`, `OpenAICompatible`, `Anthropic`) |
| `DysonOpenAiApiModes` | OpenAICompatible API surface (`Completions` default, `Responses`) |
| `DysonAgentSystemPrompts` | `ForMode` → mode system prompt; `FormatAvailableModelsBlock` / `BuildAvailableModelsBlockAsync` / `BuildSystemPromptWithModelsAsync` append same-kind slug catalog (alias, slug, defaultEffort, modes); Work/Explore/Drone orchestrator directives; Plan soft read-only + `SubmitPlan` / `PlanFirstTurnMandate` (first incomplete Plan turn, transcript-only); `SubagentReportRequiredMandate` (all children first turn); `ExploreFirstTurnReportMandate` / `DroneFirstTurnContextMandate` |
| `DysonStartSubagentResult` | StartSubagent / `CreateChildAsync` return: `SubagentId`, `PersistenceId`, `AgentMode`, `Title`, optional `ModelSlug` / `ModelLabel` |
| `DysonParentEvent` / `DysonAskQuestion` | Inbound parent-event registry + AskQuestion parse/format helpers (max 8; Skip → `A# - [skipped]`) |
| `DysonSessionTodo` | Runtime/UI/MCP mirror of a session todo (`TaskCode`, `DisplayName`, `Status`, `Comments`, `Sequence`, timestamps) |
| `DysonSessionTodoStatus` | `Pending` / `Ongoing` / `Complete` (ints 0/1/2) |

## Turns & tools

| Type | Notes |
| ---- | ----- |
| `DysonAgentTurn` | Turn kind, instruction, agent title, optional `PlanRelativePath` (PlanResult / BeginBuildPlan), `AssistantText`, `ReasoningLog` / `ReasoningText` (ordered Thought+InterimText log + denormalized Thought join; UI + persist only, not in model transcript), `SkillsUsed` (`DysonSkillUsedEntry` list — slash / `LoadSkill`; persisted as `SkillsUsedJson`; injected into transcripts), `UserImages` (`DysonBinaryAttachment` list — composer attaches; persisted as `UserImagesJson`; multimodal transcript parts), `StartedUtc` / `CompletedUtc` (UI chrome + persistence; not in model transcript), `IsExcludedFromContext` (omit from provider transcripts; UI Dropped + Restore), live `StreamingPreview`/`IsStreaming` + `ReasoningStreamingPreview`/`IsReasoningStreaming`/`AssistantTextChanged`, tool calls, tracked status, response log, compact history |
| `DysonSkillUsedEntry` / `DysonSkillsUsedSerializer` | Skill attached to a turn (`SkillId`, `DisplayName`, `MarkdownContent`, `ResolvedPath`, `LoadIndexOnly`, `UsedUtc`); JSON round-trip for `SkillsUsedJson` |
| `DysonUserImagesSerializer` / `DysonUserImageFactory` | Persist/restore turn `UserImages`; factory compresses composer bytes/data URLs to JPEG via `DysonImageCompress` |
| `DysonReasoningSegment` / `DysonReasoningSegmentKind` | Ordered log entry (`Thought` / `InterimText`) with `RoundIndex`; serialized as turn `ReasoningLogJson` |
| `DysonReasoningHistoryUi` | `ShouldExpandSegment` — latest Thought/InterimText slot open until assistant body (`AssistantText` or streaming preview) unless reasoning still streams; priors collapsed |
| `DysonAgentTurnKind` | `Normal`, `ExpandThoughtProcess`, `TaskCompletionConfirm`, `Continuation`, `ReportSummary`, `InitializeSession`, `PlanResult`, `BeginBuildPlan`, `SubagentReportProcessing`, `ShellExited`, `RethinkToolUsage` (=10) |
| `DysonAgentTurnKindDisplay` | `GetDisplayName` → UI labels (e.g. TaskCompletionConfirm → "Completion confirmed", RethinkToolUsage → "Rethink tool usage") |
| `DysonPlanResultFlow` | Factory + Instruction continuity mandate after `SubmitPlan`; legacy `BuildPlanMarker` / `BuildPlanUserPrompt` for sticky dismissal of old sessions; `AppendPlanResultTurn` on session |
| `DysonBeginBuildPlanFlow` | Factory + layout-only Recap / Agent-actions Instruction for composer Build plan (`PromptBeginBuildPlanAsync`); Agent actions mandate technical multi-Drone prep with multitasking preferred; required `ReadFile` + `CreateTodo` per Agent actions item (no StartSubagent / WriteFile / shell / product work that turn); optional Explore report blocks folded in from Plan-mode buffer; `ContinuationPrompt` + `ShouldEnqueueBuildContinuation` (host enqueues a Normal turn that implements after successful BeginBuildPlan, preferring parallel Drone multitasking) |
| `DysonSubagentReportPrompt` | Shared completion report block + `SubagentReportProcessing` Instruction/`CreateTurn`; `ShouldDrainCompletionAutoTurn` (false in Plan) |
| `DysonRethinkToolUsageFlow` | Soft-pause rethink Instruction + Explore budget recap/fallback text + `CreateTurn` / `CreateResumeTurn` (Normal) after tool-round budget |
| `DysonLongRunningShellExitedFlow` | `ShellExited` locked Instruction (auto-read tail) + `TrimInstructionAfterCompletion` + outcome mapping |
| `DysonPlanReadyUi` / `DysonPlanReadyInfo` | Derive Plan-ready sticky from turns (`TryGetPending`) until a later `BeginBuildPlan` (or legacy `[BuildPlan]` user) turn |
| `DysonSessionInitialization` | First-turn factory (`CreateTurn` → `InitializeSession`); `RenameSessionReviewMandate` + `IsRenameReviewTurn` (every 8 turns: 1, 9, 17, …; mandate appended only for incomplete current turn) |
| `DysonToolCall` | `CallId`, `ToolName`, `Stage`, `ArgumentsJson` |
| `DysonToolCallStatus` | `Queued`, `Working`, `Completed`, `Failed` |
| `DysonTrackedToolCall` | Live status + result for UI rows |
| `DysonToolCallResult` | Completed/failed payload (`IsError`, `Content`, optional `BinaryAttachment`, `EndsCurrentTurn` soft-closes the calling turn after the staged round; keeps same-round model content when present, else tool-specific harness note) |
| `DysonBinaryAttachment` | LoadBinary / screenshot / **composer user image** media (`FileName` with ext, `Extension`, `MimeType`, `Base64Data`, optional mutable `FileId` after Responses Files upload, optional `HtmlRef` for browser snips — empty today / future DOM ref); Completions emits nested `image_url` data URL; Responses prefers `input_image`/`input_file` `file_id`. Tool-result attachments are one-shot (cleared after the model sees them); turn `UserImages` persist and re-emit |
| `DysonToolCallStatusChangedEventArgs` | Previous/new status + tracked row |
| `DysonToolCallScheduler` | `RunStagedAsync` — concurrent same-stage, barrier across stages; multi-round Queued-only runs |

`DysonAgentTurn.TryParseAgentTitle` requires agent replies to start with a Markdown H1. `PrepareAdditionalTrackedCalls` supports multi-round tool loops on one turn.

## MCP

| Type | Notes |
| ---- | ----- |
| `DysonMcpAccessMode` | `FullAccess`, `AutoReview` |
| `DysonMcpPipeline` | Tool catalog + optional auto-review proxy; `ConfigureShellExecuteForMode` / `CreateLongRunningShellTools` / `PlanShellExecuteWarning` for Plan soft shell gates; `CreateBrowserTools` when `browserControlAvailable` |
| `DysonSessionToolsetBuilder` | Builds session catalog: default tools → structural gates → mode denylist (`ApplyDisabledTools`); `AllCatalogTools` / `AllCatalogToolNames` for Settings; used by session ctor / `ApplyAgentMode` rebuild / child gating |
| `DysonToolPolicyDocument` / `DysonToolPolicyStore` / `DysonToolPolicyResolver` | App-settings JSON denylist (`agent_mode_tool_policy`); resolver applies mode list only (model overlay signature unused in v1) |
| `DysonMcpTool` | Name, description, input schema JSON |
| `DysonJsonDynamicToolchainSchema` / `DysonJsonDynamicToolchainInterpreter` | Strict nested JDSL program parse + interpreter (`JsonDynamicStructuredLanguageToolchain`); flow/step result DTOs — [json-dynamic-toolchain.md](json-dynamic-toolchain.md) |
| `DysonMcpAutoReviewProxy` | In-process review gate when mode is AutoReview |
| `DysonTextEditApplier` | Cascading `old_text` matcher for `WriteFile` (exact → line-trim → block-anchor → whitespace/indent/escape → context); EOL normalize; unique match unless `replace_all`. Covered by `DysonTextEditApplierTests` in `Harness.Tests` |

Default catalog includes session tools (`StartSubagent`, `ListSubagents`, `WaitForSubagent`, `InspectSubagentLog`, `StopSubagent`, `SubmitSubagentReport`), inter-agent + Ask tools (`TriggerParentEvent`, `RespondToSubagentEvent`, `TriggerSubagentEvent`, `AskQuestion`, `AskQuestionFromParent` — gated by `ConfigureInterAgentTools(depth)`), **session todos** (`ListTodos`, `CreateTodo`, `UpdateTodo`, `DeleteTodo`), completion tools, **`ResumeCurrentTask`** (rethink phase only; root + subagents), **`ExpandThoughtProcess`** (queues expand turn + `EndsCurrentTurn`; recursion blocked on in-flight expand), **`StartNewTurn`** (required `promptInstructions`; queues Normal + `EndsCurrentTurn`; anytime; soft-close keeps same-round model content when present), **`DropTurnContext`** / **`RestoreTurnContext`** (anytime; `turnIds` + required `reason`; drop sets `IsExcludedFromContext`, restore clears it; session log lines on newly dropped/restored turns), workspace file tools (`ReadFile` lines as `lineNumber|content`; `WriteFile` prefers targeted `old_text`/`new_text` or `edits[]` with optional `replace_all`, OpenCode-style fuzzy match via `DysonTextEditApplier` — never paste ReadFile `N|` prefixes into edits; `content` only for full rewrite), **`LoadSkill`** (required `name` + `loadIndexOnly`; resolve included `Resources/Skills` → `.dyson/skills/{name}` → literal work-relative path; attaches `SkillsUsed` on the current turn and returns markdown; readonly — rethink allowlist), **`SubmitPlan`** (Plan mode only; `{ title, markdown }` → `.dyson/plans/{slug}-{hash}.md`, returns `planPath`, appends `PlanResult` turn with WriteFile continuity Instruction), **`RenameSession`** (`{ "title": string }` required) for UI/list titles, **`GetDateTime`** (optional `timezone`: `"utc"` default | `"local"`; returns ISO + `dd/MM/yyyy HH:mm` display), **`WaitForSeconds`** (`seconds` 1–300; blocking `Task.Delay`), **`JsonDynamicStructuredLanguageToolchain`** (`program` object/string; nested catalog-only JDSL — [json-dynamic-toolchain.md](json-dynamic-toolchain.md)), **`ShellExecute`** and **long-running shell tools** (`StartLongRunningShell`, `ListLongRunningShells`, `ReadLongRunningShellTail`, `AbortLongRunningShell`, `RequestLongRunningShellCancellation`, `LongRunningShellInteract`, `SubscribeToLongRunningShellCompletion`) when shells are available — Plan mode soft-warns ShellExecute + StartLongRunningShell (description + result preamble; command still runs; see [README.md](README.md)#shellexecute and [README.md](README.md)#long-running-shells) — **browser tools** when `DysonAgentSessionConfig.BrowserControl` is set (`OpenBrowser`, `ListBrowserWindows`, `CloseBrowser`, `ResizeBrowser`, `ListBrowserTabs`, `NewBrowserTab`, `CloseBrowserTab`, `ActivateBrowserTab`, `BrowserNavigate`, `BrowserGoBack`, `BrowserGoForward`, `BrowserReload`, `BrowserClick`, `BrowserType`, `BrowserFill`, `BrowserHover`, `BrowserPressKey`, `BrowserWaitForSelector`, `BrowserWaitForNavigation`, `BrowserExecuteJavaScript`, `BrowserGetHtml`, `BrowserTakeScreenshot`, `BrowserReadConsoleLog`, `BrowserReadNetworkLog`; see [README.md](README.md)#browser-control and [packaging/webview](../packaging/webview.md)) — and **web search/fetch** tools: `FreeSearch`, `FreeSearchAdvanced`, `SearchWithSynthesis`, `FreeExtract`, `WebFetch`, `FetchGithubReadme` (see [README.md](README.md)#web-search--fetch-in-process). Call `RenameSession` only when the harness every-8 rename-review mandate asks, or when the user explicitly requests a rename. `DysonMcpPipeline.CreateDefault(accessMode, availableShellTypes, browserControlAvailable)` builds the dynamic ShellExecute / long-running / browser schemas; `DysonSessionToolsetBuilder` + `ApplyAgentMode` / create-load apply Plan shell descriptions, layer gating, and mode denylist omit; `DysonWorkspaceToolExecutor` rejects tools absent from the catalog.

**Session todo tools:** operate on the current session’s list only (root and subagent each own a list). Status strings: `pending` / `ongoing` / `complete`. `CreateTodo` requires `displayName` + `taskCode` (unique per session); optional `status`, `comments`. `UpdateTodo` requires `taskCode`; optional patch `displayName` / `status`; `comments` replaces the full list; `appendComment` appends one. No comment-delete tool. `DeleteTodo` / `ListTodos` by current session.

**Subagent tools (see [README.md](README.md)#orchestrator-subagents):** `StartSubagent` is non-blocking (`agentMode` + `task`, optional `context`, optional `modelSlug` slug/display-alias — omit to inherit parent model, same provider kind only; optional `reasoningEffort` freeform — omit/null → slug `DefaultReasoningEffort`, or keep parent effort when inheriting; optional `todos` seed array with `displayName` / `taskCode` / optional `status` / `comments`; Plan banned; Explore parents cannot spawn; Drone→Explore only). Success JSON includes `modelSlug` / `modelLabel` when known. `ListSubagents` returns the session-owned direct-child roster (ids/status/title) for Wait/Inspect/Stop after resume or compaction. `WaitForSubagent` blocks for prerequisites only. `SubmitSubagentReport` (`summary`, optional `status`, optional `skipTasksCheck`) is the child handoff that drives parent interrupts / host auto-turn. Summary may be a success handoff or a failure reason when `status` is `failed`. By default it **errors** (session stays non-terminal) when any session todo is still `Pending` or `Ongoing`; pass `skipTasksCheck: true` to override — success JSON then includes `incompleteTodos` (`taskCode`, `displayName`, `status`) and `skipTasksCheck: true`. Parent notification still uses the agent `summary` unchanged. Empty todo list always passes. If the child’s background prompt fails without a report, the harness notifies the parent with a concrete failure reason (PromptAsync error, last assistant/streaming snippet, or exception `{Type}: {Message}`) under `## Report`, and persists child `Failed` + parent interrupt log. A later agent `SubmitSubagentReport` **supersedes** that harness `Failed` (status + summary + persist + re-notify parent). After a successful `completed` report, further submits are accepted as no-op COMPLETED (`idempotent: true`; no second parent interrupt). A first real `failed` report stays `Failed`. `Stopped` still rejects.

## Search (in-process)

| Type | Notes |
| ---- | ----- |
| `SearchOrchestrator` | `FreeSearchAsync` / `FreeSearchAdvancedAsync` / `SearchWithSynthesisAsync` |
| `SearchEngines` | DuckDuckGo HTML (default first), Bing RSS, Wikipedia OpenSearch, optional Brave API; returns `Result` with HTTP/parse errors |
| `SearchFetch` | `WebFetchAsync` (caller supplies `maxBytes`; clamp 1KB–2MB, default **64KB** if null), `FreeExtractAsync`, `FetchGithubReadmeAsync` |
| `SearchHttp` | Shared `HttpClient` (`Api-User-Agent` = DysonHarness) + `ValidateUrl` SSRF guard |
| `SearchAggregation` | Dedup, filter (keeps titled http(s) hits with short snippets), confidence 1–3 scoring, waterfall basket |
| `DysonWebSearchSummarizer` / `DysonWebSearchSummarizerPrompt` | Tool-owned LLM summarize for web tools (`SummarizeAsync` + optional `summarizePrompt`; ≤10K tokens) |
| `SearchHit` / `SearchResponse` / `SearchOptions` / `WebFetchResult` | Search DTOs |

Assert coverage for spawn gates, parent events, session todos, PlanResult, rethink, ExpandThoughtProcess / StartNewTurn / DropTurnContext / RestoreTurnContext, shells, Grep/LoadBinary, SSRF/search parsers, and related helpers lives in `Harness.Tests` (`dotnet test src/Harness/Harness.Tests/Harness.Tests.csproj`).

## Interrupts & completion

| Type | Notes |
| ---- | ----- |
| `DysonAgentInterrupt` | Kind, subagent id, optional `PersistenceId`, optional summary |
| `DysonAgentInterruptKind` | `SubagentCompleted`, `SubagentStopped`, `SubagentFailed` |
| `DysonSubagentInterruptEvent` | Session-event shape for subagent interrupts |
| `DysonExpandThoughtProcess` | Expand-thought turn factory; Instruction includes optional `DropTurnContext` hygiene (anytime, not expand-only); `ContinuationPrompt` + `ShouldEnqueueContinuation` (host Normal follow-up) |
| `DysonSessionInitialization` | First-prompt turn factory; periodic rename review mandate (ephemeral, not in subsequent history) |
| `DysonTaskCompletionFlow` | Confirm / continuation / report-summary factories; `ShouldMarkTerminalAfterTurn` |

## Context & tokens

| Type | Notes |
| ---- | ----- |
| `DysonContextOptimizer` | Thresholds + compact older tool history |
| `IDysonTokenCounter` | Token estimate for optimizer |
| `DysonTiktokenTokenCounter` | Default counter |

## Result types

Live in `Harness.Abstractions` (namespace `DysonHarness`).

| Type | Notes |
| ---- | ----- |
| `Result<TValue, TError>` | Value or error; error path has optional `Exception` (null on success). `AsError(error)`, `AsError(error, exception)`, `AsError(error, debugCode, exception = null)` |
| `VoidResult<TError>` | Side-effect success or error; same optional `Exception` + matching `AsError` overloads / constructors |
| `ValueResult<TValue>` | Success value vs error flag |
| `DebugCodes` | Optional debug code on error results |

Keep `TError` / user-facing messages clean — do not stringify exceptions into `Error` by default. Call sites that need detail read `result.Exception` (e.g. host logging).

## Browser (Abstractions)

| Type | Notes |
| ---- | ----- |
| `IDysonBrowserControl` | Process-wide singleton: `OpenBrowserAsync` / `ListWindowsAsync` / `GetWindowAsync`; `SnipCaptured` (`DysonBrowserSnipPayload`) for chrome snips → UI host pending images |
| `IDysonBrowserWindow` | Tabs + close/resize/bring-to-front |
| `IDysonBrowserTab` | Navigate, interact, JS, screenshot, console/network logs |
| `DysonBrowserClickRequest` / `DysonBrowserTypeRequest` / `DysonBrowserKeyRequest` | Interaction DTOs |
| `DysonBrowserConsoleEntry` / `DysonBrowserNetworkEntry` | Log DTOs |
| `DysonBrowserSnipPayload` / `DysonBrowserSnipCrop` | Snip event payload + DIP→pixel crop math |
| `DysonNullBrowserControl` | All methods → `"browser control unavailable"` |
| `DysonCefBrowserControl` | Windows CefSharp impl in `Harness.WindowsBrowser` (not referenced by Engine) |

## Workspace filesystem

| Type | Notes |
| ---- | ----- |
| `IDysonWorkspaceFileSystem` | `InitializeAsync(subjectId)` then sandboxed resolve/exists/read/write/enumerate/delete; `NativeRootPath` + `SubjectId` |
| `DysonWorkspaceSubjects` | `LocalFs = "local_fs"` |
| `DysonWorkspaceEntry` | `Name` + `IsDirectory` |
| `IDysonWorkspaceChangeWatcher` | `Changed` / `Failed` + start/stop/`IDisposable` |
| `DysonWorkspaceChangeKind` / `DysonWorkspaceChangeEventArgs` | Created / Changed / Deleted / Renamed (+ native `FullPath`) |
| `DysonLocalWorkspaceFileSystem` | Path-based local/SMB/UNC (incl. Azure Files mounts); accepts only `"local_fs"` |
| `DysonWorkspaceFileSystems.CreateLocalAsync` | Validate dir → construct → init with `"local_fs"` |

Cloud hosts implement `IDysonWorkspaceFileSystem` themselves — see [Cloud hosting / custom implementations](../storage/work-directories.md#cloud-hosting--custom-implementations).

## Persistence-facing types

Documented under [docs/storage](../storage/models.md), [sessions.md](../storage/sessions.md), and [work-directories.md](../storage/work-directories.md):

- `DysonAppMode`, `DysonAppPaths`, `DysonBuildInfo`
- `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonWorkDirectoryStore`, `DysonAppSettingsStore`
- `DysonModelProviderEntity` (providers own `ApiKey` / `BaseUrl` / `ProviderKind` / optional `ManagedSource` / `OpenAiApiMode`)
- `DysonModelSlugEntity` (slugs own `Slug` + `DisplayAlias` + `IsEnabled` + optional `DefaultReasoningEffort` + `ReasoningModes`)
- `DysonModelStore.UpsertManagedProviderAsync` / `SetSlugEnabledAsync` / `SetSlugDefaultReasoningEffortAsync` — managed import + per-slug enable + default effort (see [storage/models.md](../storage/models.md)#managed-providers-cliproxy)
- `DysonAppSettingEntity` / `DysonAppSettingKeys` (key/value prefs, e.g. web search summarizer slug; `cliproxy_*` mirrors)
- `DysonWorkDirectoryEntity`, `DysonNativeFolderPicker`, `DysonGitInfo`
- Session/turn/log entities and `DysonPersistedSession` (sessions reference `ModelSlugId`, optional `ReasoningEffort`, + optional `WorkDirectoryId`; aggregate includes todos)
- `DysonSessionTodoEntity` / `DysonSessionTodo` / `DysonSessionTodoStatus` / todo request DTOs on `DysonSessionStore`
