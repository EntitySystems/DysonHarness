# UI

Landing project: [`src/Harness/Harness.UI`](../../src/Harness/Harness.UI) — Blazor Interactive Server (`net10.0-windows` on Windows, else `net10.0`), references `Harness.Engine` and (Windows only) `Harness.WindowsBrowser`.

## How to run

From repo root:

```bash
dotnet run --project src/Harness/Harness.UI --urls http://localhost:5180
```

Or open the solution and set **Harness.UI** as the startup project. The app uses Interactive Server rendering globally.

DI (scoped): `ThemeService`, `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonWorkDirectoryStore`, `DysonAppSettingsStore`, `DysonToolPolicyStore`, `DysonConfiguredShellStore`, `HttpClient` (via `IHttpClientFactory`), `ManagedInferenceProviderCatalog`, `DysonUiHost`. Singleton: `DysonCliProxyHost` (disposed on app shutdown).

**Windows only:** `Program.cs` also registers `AddSingleton<IDysonBrowserControl, DysonCefBrowserControl>()`. `DysonUiHost` injects that singleton into every `DysonAgentSessionConfig.BrowserControl` (new + resume). When set, the engine MCP catalog includes browser tools (`OpenBrowser`, …) that open in-process CefSharp WPF windows. See [packaging/webview](../packaging/webview.md).

On first open, a default **Demo Mock** provider + slug is seeded if none exists. SQLite lives under the platform app-data folder for the current `DysonAppMode` (see [storage/models](../storage/models.md)).

`DysonUiHost` branches on effective `ProviderKind` (see below): `demo` (no credentials) → `DemoDysonAgentSession`; `OpenAICompatible` → `OpenAiCompatibleAgentSession` (engine). Anthropic is not wired yet. OpenAI-compatible sessions expose in-process web search MCP tools (`FreeSearch`, `WebFetch`, …) via the engine catalog — see [engine README](../engine/README.md)#web-search--fetch-in-process. Web tools summarize **inside** the executor (`DysonWebSearchSummarizer`); optional summarizer slug from Settings → General (empty ⇒ session model via `SummarizerProvider` null). Assert coverage for search SSRF, session todos, file manager, PlanResult, Plan shell warning, long-running shells, and UI helpers lives in `Harness.Tests` (`dotnet test src/Harness/Harness.Tests/Harness.Tests.csproj`) — not UI startup.

**Provider routing:** session type follows the slug’s provider `ProviderKind`. Demo mode is for offline UI testing — `DemoDysonAgentSession` injects mock tools every turn (`read_file`, `grep`, `list_dir`) and mocks `RenameSession` only on rename-review turns (1, 9, 17, …), without calling an LLM. OpenAI-compatible providers call the real API and only run tools the model requests.

**Mis-tagged providers:** if a row has `ProviderKind = demo` but a Base URL or API key is set, create/save in **Settings → Models** coerces it to `OpenAICompatible`, and `ResolveProviderAsync` treats it as OpenAI-compatible at session start even before repair. Use **Repair mis-tagged providers** on the Models settings page for a one-shot DB fix of existing rows (does not run on startup; leaves **Demo Mock** unchanged).

## Routes

| Route | Layout | Role |
| ----- | ------ | ---- |
| `/` | `MainLayout` → `AppShell` | Agent shell: workdirs + sessions + chat |
| `/settings` | `SettingsLayout` | Redirects to `/settings/general` |
| `/settings/general` | `SettingsLayout` | Theme / accent (`ThemeSwitcher`) + web search summarizer model (`ModelSlugPicker`, optional) |
| `/settings/agent-behavior` | `SettingsLayout` | End of task auto review toggle (`end_of_task_auto_review` in `app_settings`; persist only — no reviewer spawn yet) |
| `/settings/models` | `SettingsLayout` | Provider/slug CRUD (`ModelsPanel`) |
| `/settings/shells` | `SettingsLayout` | Configured shells CRUD (name, path, enable/disable; native file Browse) |
| `/settings/agent-modes` | `SettingsLayout` | List of `DysonAgentModes.BuiltIns`; link to per-mode tool toggles |
| `/settings/agent-modes/{Mode}` | `SettingsLayout` | Enable/disable MCP tools for one mode (`Uri.EscapeDataString` for spaces); persists denylist |

`SettingsLayout` nests under `MainLayout` (side nav: General, Agent behavior, Models, Shells, Agent modes, Back to agent).

## Layout

| Path | Role |
| ---- | ---- |
| `Components/Pages/Home.razor` | Agent IDE shell; `Host.LastError` shows as a 20s auto-expiring toast (border countdown + dismiss X) via `ErrorToast` |
| `Components/Pages/Settings/` | Settings pages (`Index`, `General`, `AgentBehavior`, `Models`, `Shells`, `AgentModes`, `AgentModeDetail`) |
| `Components/Layout/SettingsLayout.razor` | Settings side-nav shell |
| `Components/Shell/` | `AppShell`, `Sidebar`, `SessionHeader`, `RailSidePanel`, `ErrorToast` |
| `Components/Sessions/` | `WorkDirectorySwitcher`, `SessionList` |
| `Components/Chat/` | `ChatPanel`, `SessionTodoOverview`, `SessionSubagentOverview`, `TurnBlock`, `PlanResultBlock`, `PlanReadyPopover`, `SubagentCard`, `SubagentEventBlock`, `AskQuestionPopover`, `Composer`, `AgentModePicker` |
| `Components/Files/` | `FileViewerOverlay` (chat-preserving plan/file viewer) |
| `Components/Tools/` | `ToolCallPanel`, `ToolCallRow`, `QueuedToolCallRow`, tool-specific bodies under `Variants/` |
| `Components/Models/` | `ModelsPanel` (settings CRUD), `ModelSlugPicker` (agent pick) |
| `Components/Theme/` | `ThemeSwitcher` |
| `Theme/ThemeService.cs` | Theme/accent state + JS interop |
| `Demo/` | `DemoDysonEngine`, `DemoDysonAgentSession`, `DemoDysonAgentProvider`, `DysonUiHost`, `DysonToolCallUi` (tool-row parse/summary helpers; Facts in `Harness.Tests`) |
| `wwwroot/app.css` | Charcoal IDE theme (CSS variables); markdown styles under `.turn-block__body` |
| `Markdown/MarkdownRenderer.cs` | Markdig pipeline for agent turn bodies (`DisableHtml` for XSS safety) |
| `wwwroot/theme.js` | `localStorage` theme + active workdir (`dyson-workdir`); `dysonChat` stick-to-bottom scroll for the transcript |

## Component map

| Component | Role |
| --------- | ---- |
| `AppShell` | Sidebar \| main \| right rail |
| `ErrorToast` | Home-only `Host.LastError` banner: 20s auto-clear (`ClearLastError`), CSS border-thickness countdown, dismiss X (`icons/cancel.svg`); Settings form `error-banner`s stay until the next action |
| `RailSidePanel` | Right-rail Files / Git / Usage tabs (placeholders for now); Session log stays as a sibling panel on `Home` |
| `Sidebar` | Work directory switcher, sessions, Settings link, app-mode badge |
| `WorkDirectorySwitcher` | Register/switch/remove workdirs; native folder pick via `DysonNativeFolderPicker` |
| Settings → Shells | Enable/disable/edit/remove configured shells; optional Fixed args (space-separated → JSON); Browse via `DysonNativeFolderPicker.PickFileAsync`; seeds Windows defaults when empty |
| `SessionList` | Sessions for active workdir; click a row to resume/load it; hover (or focus) shows a trash icon that confirms then deletes via `DysonUiHost.DeleteSessionAsync`; **New** disabled until a workdir is selected |
| `ModelSlugPicker` | Compact chip + search modal; switches the **active** session model (and persists `ModelSlugId` + resets effort to slug default) when a session is focused; otherwise sets the slug / pending effort for the next New session; also General summarizer (`AllowEmpty` → use session model); lists enabled slugs only (keeps a currently selected disabled slug visible so resume is not force-switched) |
| `AgentModePicker` | Compact chip + search modal of `DysonAgentModes.BuiltIns`; picker may change freely; **submit** commits via `Host.PromptAsync(prompt, agentMode)` → `ApplyAgentMode` when it differs from `session.Mode` (rebuilds system prompt + persists); resume / host `Changed` syncs picker from `session.Mode` |
| `ChatPanel` | Transcript (`.chat-panel__turns` flex-scrolls inside the main column; stick-to-bottom via `dysonChat` in `theme.js` while near bottom / on new turns); forwards model / effort / mode / git branch / `OnNewSession` to Composer |
| `TurnBlock` | Single turn (title left; muted mono local `dd/MM/yyyy HH:mm` on header right from `StartedUtc`/`CompletedUtc` — start only while in progress, `{start} – {end}` when complete; not in model transcript; older turns collapse when a new turn starts — header click toggles expand/collapse; user prompt with right-side spinner while reply in flight; hover → danger cancel SVG click cancels; when idle, muted Retry (`icons/retry.svg`) resubmits the prompt; **thinking history** above the reply — each `Thought`/`InterimText` is a controlled toggle panel (H1 title when present; prior collapsed; latest open until assistant body / while live reasoning streams; all collapse once `AssistantText` or streaming preview appears), final reply stays in the assistant body; streaming plain-text preview while `IsStreaming`, Markdig assistant body when complete, tools; **`PlanResultBlock`** for `PlanResult` turns (Open plan → file viewer; Build plan → `BuildPendingPlanAsync`); **`SubagentCard`** under each completed/working `StartSubagent` tool call) |
| `PlanResultBlock` | Plan title + relative path + **Open plan** / **Build plan** (`stopPropagation`) → `Host.OpenFileViewerAsync` / `Host.BuildPendingPlanAsync` |
| `PlanReadyPopover` | Composer sticky after latest `PlanResult` until a `BeginBuildPlan` turn (or legacy `[BuildPlan]` user prompt); **View plan file** / **Build plan** (`Host.BuildPendingPlanAsync` → consume buffered Explore reports into BeginBuildPlan Instruction, Work mode + `PromptBeginBuildPlanAsync`) |
| `FileViewerOverlay` | Fixed backdrop overlay on `Home`; subscribes to `Host.Changed` so open paints without a parent re-render; Escape / ×; markdown via `MarkdownRenderer` or monospace code; Comment popup accumulates drafts in-viewer (edit only); comments panel pinned above scrollable file content when drafts exist; **Submit comments** posts one Normal turn via `Host.PromptAsync` then closes (discard on close without submit) |
| `SubagentCard` | Compact parent-turn card: child title + muted mono `#RuntimeId` + muted model label (`Alias · Provider / slug`, child provider with parent fallback) + latest child turn title + spinner while running; click → `NavigateToSessionAsync` |
| `SubagentEventBlock` | Expandable “Subagent event” transcript block (kind, subagent, eventId, payload); spinner while unaddressed |
| `AskQuestionPopover` | Composer overlay for root `AskQuestion` / L1 `askQuestion` parent events; per-question Skip; Submit when all resolved; disables Send while open |
| `Composer` | Prompt + left-aligned toolbar (model chip, **Effort** `<select>` of the selected slug’s `ReasoningModes` plus **None** (null → omit; legacy current value kept as an extra option if not in the list), mode, git branch chip); **PlanReadyPopover** above the textarea when `Host.PendingPlanReady` is set; typing `/` as the whole prompt token opens a dense slash-command overlay above the textarea (`/ask` `/plan` `/work` modes, `/new` → `OnNewSession` / `StartNewAsync`, `/[model]` fuzzy match on **enabled** slug/alias like the model picker — applies to the live session via `SetSessionModelSlugAsync` when focused; max 5; ↑↓ Enter Esc; send strips+applies a leading command). Logic in `ComposerSlashCommands` (Facts in `Harness.Tests`) |
| `ToolCallPanel` / `ToolCallRow` / `QueuedToolCallRow` | Live tool status; shared expand chrome with tool-specific collapsed summaries and expanded bodies (`Components/Tools/Variants/`), generic args/result fallback for unknown tools |
| `ThemeSwitcher` | Light/Dark + Blue/Green/Red/Purple (settings → General) |
| `SessionHeader` | Title (`DisplayTitle`), mode, ids, MCP, git branch, **Shells N** count pill (running long-running shells for the workdir; opens list/log modal with Force stop → Abort), **Tools** menu → Open Browser when `IDysonBrowserControl` is registered (Windows), app mode; when viewing a child (`ParentSessionId` set), **← Parent** → `NavigateToParentAsync` |
| `SessionTodoOverview` | Between `SessionHeader` and `ChatPanel`; hidden when the session todo list is empty; collapsed (default) shows `{complete}/{total} tasks done`; expanded lists DisplayName, TaskCode, status badge, comments; refreshes on `TodosChanged` via host `Notify` |
| `SessionSubagentOverview` | Below todos; session-level roster of direct children (not only spawn-turn cards); collapsed shows `{n} subagents ({active} active)`; expanded reuses `SubagentCard`; refreshes on host `Notify` after hydrate / spawn |
| `ModelsPanel` | Provider/slug CRUD — settings → Models; OpenAICompatible shows Completions/Responses API mode toggle; slug create/edit **Reasoning modes** chip/list editor + default reasoning effort (new forms prefill `high`; blank = omit); **Repair mis-tagged providers** fixes demo rows that have credentials; **Third-party managed providers** section imports Codex / Grok / Antigravity / Kimi / Claude Code via CLIProxy (download progress, Connect / Complete / Verify; managed rows are view-only except per-slug default-effort dropdown + Enable/Disable + Default; user effort is preserved across Verify). Shows a warning when CLIProxy is not installed under `external/cliproxy/{version}`. **Disconnect** clears pending local auth-session tracking only — does not delete the managed row or stop the proxy |
| `SettingsLayout` | Settings side nav + content |

General also hosts **Web search summarizer**: optional slug stored in `app_settings` (`web_search_summarizer_model_slug_id`); cleared = use session model.

**Agent behavior** settings: **End of task auto review** toggle stored in `app_settings` (`end_of_task_auto_review` = `"true"` / `"false"`; missing ⇒ off). Persist only for now — does not spawn a reviewer yet.

**Agent modes** settings: list built-ins; detail page toggles catalog tool names via `DysonToolPolicyStore` (`agent_mode_tool_policy` JSON). Disabled tools are omitted from new sessions and on mid-session `ApplyAgentMode` rebuild — running sessions are not live-refreshed without a mode switch.

## Work directories

- Active workdir id in `localStorage` key `dyson-workdir`
- Session list filtered by `WorkDirectoryId`
- Composer / header branch chip from `DysonGitInfo.TryGetBranch` on the active path
- Details: [storage/work-directories.md](../storage/work-directories.md)

## Theming

- CSS variables with `data-theme` (light/dark) and `data-accent` (Blue / Green / Red / Purple)
- Persist preference via JS interop (`theme.js`) + `localStorage`
- Visual direction: Cursor/Factory charcoal IDE look — dense, functional, not marketing

`ThemeService` + `ThemeSwitcher` own the applied attributes (General settings page).

## Demo host (`DysonUiHost`)

- **Live session registry:** `_sessionsById` keeps parent + children running while the UI focuses one session (`ActiveSessionId` / `Session`). Switching focus does not dispose other registry entries.
- **Navigate:** `NavigateToSessionAsync(Guid)` / `NavigateToParentAsync()` — focus live registry entry or load from DB; sidebar stays **roots only** (children via cards / back, not listed).
- **Subagent cards:** `GetSubagentCardState(persistenceId)` → title, `RuntimeId` (child session id for muted `#id` on the card), `ModelLabel` (child `Provider`, else parent), latest turn `AgentTitle`, `IsRunning` / status for `SubagentCard`.
- **Auto-turn on report:** on parent `SubagentCompleted` / `SubagentFailed` / `SubagentStopped` interrupt, enqueue a harness `SubagentReportProcessing` turn for that parent; when parent `!IsBusy` and **not** in Plan mode, FIFO `PromptSubagentReportProcessingAsync` (`# Subagent report` mandate + bold **Report** block; analyze then continue in one turn — does not cancel in-flight parent work). Kickoff failures also surface a concrete reason here. In **Plan** mode, completions are buffered (UI still updates); **Build plan** folds them into the BeginBuildPlan Instruction (one `#` title + bold labels; reply still mandates `` ## Recap `` / `` ## Agent actions ``), or leaving Plan without Build drains them as `SubagentReportProcessing` auto-turns. `SubagentEvent` / Ask UI are not deferred.
- **Auto-turn on shell exit:** on `LongRunningShellExited` (from `SubscribeToLongRunningShellCompletion`), always FIFO-drain a `ShellExited` harness turn via `PromptShellExitedAsync` (auto-read tail in Instruction; trimmed after the turn completes — including while Plan). Does not cancel in-flight parent work.
- **Subagent events:** on `SubagentEvent`, show `SubagentEventBlock`; general kinds FIFO-auto-prompt `RespondToSubagentEvent`; `askQuestion` opens `AskQuestionPopover` (no parent LLM auto-Respond).
- **AskQuestion (root):** pending questions bind to composer popover; answers complete via `RespondToAskQuestion`.
- **New session:** `StartNewSessionAsync(agentMode, modelSlugId, workDirectoryId)` — workdir required → resolves provider kind → `OpenAiCompatibleAgentSession` or `DemoDysonAgentSession`
- **Switch agent mode (submit):** `PromptAsync(prompt, agentMode)` applies mode when the picker differs from `session.Mode` (via `SetSessionAgentModeAsync` / `ApplyAgentModeCoreAsync`) before the turn — rebuilds system prompt + available-models suffix, bumps `SystemPromptGeneration` (OpenAI `prompt_cache_key`), persists `AgentMode` + `SystemPromptSnapshot`; busy-gated; resume / focus syncs the composer picker from `session.Mode`; leaving Plan drains any buffered completion auto-turns
- **Switch model:** `SetSessionModelSlugAsync(modelSlugId)` — same provider kind only; rejects switching **to** a disabled slug; swaps live `session.Provider`, resets composer effort to the slug’s `DefaultReasoningEffort` (pending kept in sync for New Session), and persists `ModelSlugId` + `ReasoningEffort`; cross-kind (demo ↔ OpenAI) rejected (`LastError`: start a new session); blocked while busy; with no session, updates pending effort for the next New session
- **Session effort:** `SetSessionReasoningEffortAsync(effort)` — overrides session `ReasoningEffort` / live provider only (not the slug default); empty omits the request field; persists when a session is focused; blocked while busy; New Session seeds from the current composer effort (live session when focused, else pending)
- **Plan-ready sticky:** `PendingPlanReady` / `BuildPendingPlanAsync` — after latest `PlanResult` with a path, composer shows View / Build until a later `BeginBuildPlan` turn (layout-only: Recap + Agent actions + mandatory `CreateTodo` per action after `ReadFile`; no StartSubagent / WriteFile / shell / product work; buffered Explore reports fold into that Instruction); legacy `[BuildPlan]` user turns still dismiss sticky. After a successful BeginBuildPlan, `PromptOnSessionAsync` enqueues `DysonBeginBuildPlanFlow.ContinuationPrompt` as a Normal turn that implements from the Agent actions set
- **File viewer:** `OpenFileViewerAsync` stays on the Blazor sync context (no `ConfigureAwait(false)` on this path) so `Notify` paints `FileViewerOverlay`; overlay also listens to `Host.Changed`
- **Delete session:** `DeleteSessionAsync(sessionId)` — confirms in UI, then store delete (subtree + cascaded turns/logs); detaches if it was the active session
- **Resume:** `GetFullSessionAsync` → re-resolves provider from `ModelSlugId` + session `ReasoningEffort` (null → slug default) → same branch as new session; resume by id still works when the slug is disabled; restores todos into the live session; hydrates direct DB children into `SubSessions` / `SubagentsById` (quiet child loads skip `SessionResumed` logs) so Wait/Inspect/Stop and `SessionSubagentOverview` work after cold resume
- **Todos:** host subscribes `TodosChanged` → `Notify()` so `SessionTodoOverview` refreshes without a full reload; MCP todo tools mutate the focused session’s own list
- **Subagents overview:** `SessionSubagentOverview` binds to `Session.SubSessions` (session-owned roster); spawn-turn `SubagentCard`s remain under `TurnBlock`
- **Rename:** demo tool executor handles `RenameSession` → `RenameAsync` + persist `Title` + `SessionRenamed` log; host `SessionRenamed` notifies UI to refresh list/header
- **LastError toast (Home):** session errors on `Host.LastError` (e.g. busy model switch) render as a 20s auto-expiring toast with a depleting border and dismiss X (`icons/cancel.svg` → `ClearLastError`); Settings/Models form banners are unchanged
- **Cancel prompt:** `CancelPrompt()` cancels the linked CTS used by the in-flight `PromptAsync`; latest busy turn spinner hover shows a danger cancel cross (`icons/cancel.svg`) and click invokes it
- **Resubmit prompt:** idle user turns show a muted Retry control on `.turn-block__user` (`icons/retry.svg`); click re-sends that turn’s `Instruction` through `OnSubmit` / `PromptAsync` as a new turn (disabled while `SessionBusy`)
- **Provider:** resolved from selected/default slug; credentialed `demo` rows are treated as `OpenAICompatible`; picker chip syncs from focused `Session.Provider` on host change / resume / new session
- **Demo tools:** first turn is `InitializeSession` with the user prompt visible in chat; demo mocks `RenameSession` only on every-8 review turns (title from prompt) plus staged mocks (`read_file` / `grep` / `list_dir`); demo also implements real subagent Start/Wait/Inspect/Stop/Submit paths against `CreateChildAsync` — only when the session is actually on the demo provider (no credentials)
- **Turn display:** `TurnBlock` always shows the user prompt (`.turn-block__user`) for `Normal` and `InitializeSession` turns alongside the assistant reply; harness kinds (`ExpandThoughtProcess`, `BeginBuildPlan`, completion flow) show their instruction in a muted strip (`.turn-block__instruction`). Turns with `IsExcludedFromContext` use `.turn-block--dropped` (reduced opacity), a Dropped badge, and **Restore** → `Host.RestoreTurnContextAsync` (clears the flag + persists); chat still shows the full turn, only provider transcripts omit them. **`PlanResult`** turns render `PlanResultBlock` (title, path, Open plan / Build plan) instead of the instruction strip — continuity Instruction stays in model history only. **File viewer:** `Host.OpenFileViewerAsync` / `CloseFileViewer` + `FileViewerOverlay` on `Home` (does not dispose chat). **Turn timestamps:** in `.turn-block__header` opposite the title (`.turn-block__timestamp`) — local `dd/MM/yyyy HH:mm` only (no `# turn started at` / `# turn ended at` prefixes), from `DysonAgentTurn.StartedUtc` / `CompletedUtc` (UI chrome, not written to assistant text or API history). In progress: start only; when `CompletedUtc` is set: `{start} – {end}`. Kind/id stay under the title (`.turn-block__header-main`) so the clock does not crowd the H1. While a turn’s reply is still in flight (streaming, pending tools, or session busy on the latest turn with no `AssistantText` yet), a compact accent spinner sits on the **right** of the user prompt row (`.turn-block__spinner`); on hover it becomes a danger cancel control (`.turn-block__cancel` / `.turn-block__cancel-icon`) and hides once `AssistantText` is set and streaming has finished. When the spinner is not shown, a muted Retry control (`.turn-block__user-retry` / `icons/retry.svg`) appears instead and resubmits the instruction when idle. OpenAI-compatible first prompts use `InitializeSession`; the every-8 rename-review mandate is sent to the model via transcript append on the current turn only (never re-emitted in later history), and is not shown in the UI. Plan first-turn Explore mandate is likewise transcript-only.
- **Streaming text:** `DysonAgentTurn.StreamingPreview` + `IsStreaming` update live during SSE; optional `ReasoningStreamingPreview` + `IsReasoningStreaming` for provider reasoning tokens (Completions `reasoning_content` / Responses reasoning deltas). `DysonUiHost` throttles `AssistantTextChanged` → `Notify()` (~75ms) while either stream is active, and flushes immediately on finish/clear so Markdig replaces the plain-text preview without lag. While streaming, `TurnBlock` renders escaped plain text (raw, including mid-stream H1) with a blinking caret (`.turn-block__body--streaming`); title parse and Markdig run only when complete. Thinking history (`.turn-block__thinking-history`) renders ordered `ReasoningLog` segments as controlled `.turn-block__reasoning` panels (button header + conditional body; H1 via `TryParseAgentTitle` when present, else Thinking n / Note n): latest Thought/InterimText open until assistant body (`AssistantText` or `StreamingPreview`), priors collapsed; all collapse once the reply streams/finalizes (unless `IsReasoningStreaming`); live trailing Thought while reasoning streams; final `AssistantText` stays in the main body. Partial previews are not persisted; committed log + denormalized `ReasoningText` are stored on the turn for session reload (omitted from model transcripts).

Engine concepts: [docs/engine](../engine/README.md) · [orchestrator subagents](../engine/README.md)#orchestrator-subagents. Persistence: [docs/storage](../storage/sessions.md) · [work-directories](../storage/work-directories.md).
