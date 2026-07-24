# UI

Landing project: [`src/Harness/Harness.UI`](../../src/Harness/Harness.UI) — Blazor Interactive Server (`net10.0`), references `Harness.Engine`.

## How to run

From repo root:

```bash
dotnet run --project src/Harness/Harness.UI --urls http://localhost:5180
```

Or open the solution and set **Harness.UI** as the startup project. The app uses Interactive Server rendering globally.

DI (scoped): `ThemeService`, `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonWorkDirectoryStore`, `DysonAppSettingsStore`, `HttpClient` (via `IHttpClientFactory`), `DysonUiHost`.

On first open, a default **Demo Mock** provider + slug is seeded if none exists. SQLite lives under the platform app-data folder for the current `DysonAppMode` (see [storage/models](../storage/models.md)).

`DysonUiHost` branches on effective `ProviderKind` (see below): `demo` (no credentials) → `DemoDysonAgentSession`; `OpenAICompatible` → `OpenAiCompatibleAgentSession` (engine). Anthropic is not wired yet. OpenAI-compatible sessions expose in-process web search MCP tools (`FreeSearch`, `WebFetch`, …) via the engine catalog — see [engine README](../engine/README.md)#web-search--fetch-in-process. Web tools summarize **inside** the executor (`DysonWebSearchSummarizer`); optional summarizer slug from Settings → General (empty ⇒ session model via `SummarizerProvider` null). On startup the UI runs `SearchSelfCheck.RunSsrfChecks()` and logs a warning if it fails.

**Provider routing:** session type follows the slug’s provider `ProviderKind`. Demo mode is for offline UI testing — `DemoDysonAgentSession` injects mock tools every turn (`read_file`, `grep`, `list_dir`) and mocks `RenameSession` only on rename-review turns (1, 9, 17, …), without calling an LLM. OpenAI-compatible providers call the real API and only run tools the model requests.

**Mis-tagged providers:** if a row has `ProviderKind = demo` but a Base URL or API key is set, create/save in **Settings → Models** coerces it to `OpenAICompatible`, and `ResolveProviderAsync` treats it as OpenAI-compatible at session start even before repair. Use **Repair mis-tagged providers** on the Models settings page for a one-shot DB fix of existing rows (does not run on startup; leaves **Demo Mock** unchanged).

## Routes

| Route | Layout | Role |
| ----- | ------ | ---- |
| `/` | `MainLayout` → `AppShell` | Agent shell: workdirs + sessions + chat |
| `/settings` | `SettingsLayout` | Redirects to `/settings/general` |
| `/settings/general` | `SettingsLayout` | Theme / accent (`ThemeSwitcher`) + web search summarizer model (`ModelSlugPicker`, optional) |
| `/settings/models` | `SettingsLayout` | Provider/slug CRUD (`ModelsPanel`) |

`SettingsLayout` nests under `MainLayout` (side nav: General, Models, Back to agent).

## Layout

| Path | Role |
| ---- | ---- |
| `Components/Pages/Home.razor` | Agent IDE shell |
| `Components/Pages/Settings/` | Settings pages (`Index`, `General`, `Models`) |
| `Components/Layout/SettingsLayout.razor` | Settings side-nav shell |
| `Components/Shell/` | `AppShell`, `Sidebar`, `SessionHeader` |
| `Components/Sessions/` | `WorkDirectorySwitcher`, `SessionList` |
| `Components/Chat/` | `ChatPanel`, `TurnBlock`, `SubagentCard`, `Composer`, `AgentModePicker` |
| `Components/Tools/` | `ToolCallPanel`, `ToolCallRow` |
| `Components/Models/` | `ModelsPanel` (settings CRUD), `ModelSlugPicker` (agent pick) |
| `Components/Theme/` | `ThemeSwitcher` |
| `Theme/ThemeService.cs` | Theme/accent state + JS interop |
| `Demo/` | `DemoDysonEngine`, `DemoDysonAgentSession`, `DemoDysonAgentProvider`, `DysonUiHost` |
| `wwwroot/app.css` | Charcoal IDE theme (CSS variables); markdown styles under `.turn-block__body` |
| `Markdown/MarkdownRenderer.cs` | Markdig pipeline for agent turn bodies (`DisableHtml` for XSS safety) |
| `wwwroot/theme.js` | `localStorage` theme + active workdir (`dyson-workdir`); `dysonChat` stick-to-bottom scroll for the transcript |

## Component map

| Component | Role |
| --------- | ---- |
| `AppShell` | Sidebar \| main \| right rail |
| `Sidebar` | Work directory switcher, sessions, Settings link, app-mode badge |
| `WorkDirectorySwitcher` | Register/switch/remove workdirs; native folder pick via `DysonNativeFolderPicker` |
| `SessionList` | Sessions for active workdir; click a row to resume/load it; hover (or focus) shows a trash icon that confirms then deletes via `DysonUiHost.DeleteSessionAsync`; **New** disabled until a workdir is selected |
| `ModelSlugPicker` | Compact chip + search modal for **New** session slug; also General summarizer (`AllowEmpty` → use session model) |
| `AgentModePicker` | Compact chip + search modal of `DysonAgentModes.BuiltIns` (always enabled for next New session) |
| `ChatPanel` | Transcript (`.chat-panel__turns` flex-scrolls inside the main column; stick-to-bottom via `dysonChat` in `theme.js` while near bottom / on new turns); forwards model / mode / git branch to Composer |
| `TurnBlock` | Single turn (title left; muted mono local `dd/MM/yyyy HH:mm` on header right from `StartedUtc`/`CompletedUtc` — start only while in progress, `{start} – {end}` when complete; not in model transcript; older turns collapse when a new turn starts — header click toggles expand/collapse; user prompt with right-side spinner while reply in flight; hover → danger cancel SVG click cancels; when idle, muted Retry (`icons/retry.svg`) resubmits the prompt; streaming plain-text preview while `IsStreaming`, Markdig assistant body when complete, tools; **`SubagentCard`** under each completed/working `StartSubagent` tool call) |
| `SubagentCard` | Compact parent-turn card: child title + latest child turn title + spinner while running; click → `NavigateToSessionAsync` |
| `Composer` | Prompt + left-aligned toolbar (model chip, mode, git branch chip) |
| `ToolCallPanel` / `ToolCallRow` | Live tool status |
| `ThemeSwitcher` | Light/Dark + Blue/Green/Red/Purple (settings → General) |
| `SessionHeader` | Title (`DisplayTitle`), mode, ids, MCP, git branch, app mode; when viewing a child (`ParentSessionId` set), **← Parent** → `NavigateToParentAsync` |
| `ModelsPanel` | Provider/slug CRUD — settings → Models; OpenAICompatible shows Completions/Responses API mode toggle; **Repair mis-tagged providers** fixes demo rows that have credentials |
| `SettingsLayout` | Settings side nav + content |

General also hosts **Web search summarizer**: optional slug stored in `app_settings` (`web_search_summarizer_model_slug_id`); cleared = use session model.

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
- **Subagent cards:** `GetSubagentCardState(persistenceId)` → title, latest turn `AgentTitle`, `IsRunning` / status for `SubagentCard`.
- **Auto-turn on report:** on parent `SubagentCompleted` / `SubagentFailed` interrupt, enqueue a harness report prompt for that parent; when parent `!IsBusy`, FIFO `PromptAsync` with the `SubmitSubagentReport` summary (does not cancel in-flight parent work).
- **New session:** `StartNewSessionAsync(agentMode, modelSlugId, workDirectoryId)` — workdir required → resolves provider kind → `OpenAiCompatibleAgentSession` or `DemoDysonAgentSession`
- **Delete session:** `DeleteSessionAsync(sessionId)` — confirms in UI, then store delete (subtree + cascaded turns/logs); detaches if it was the active session
- **Resume:** `GetFullSessionAsync` → re-resolves provider from `ModelSlugId` → same branch as new session
- **Rename:** demo tool executor handles `RenameSession` → `RenameAsync` + persist `Title` + `SessionRenamed` log; host `SessionRenamed` notifies UI to refresh list/header
- **Cancel prompt:** `CancelPrompt()` cancels the linked CTS used by the in-flight `PromptAsync`; latest busy turn spinner hover shows a danger cancel cross (`icons/cancel.svg`) and click invokes it
- **Resubmit prompt:** idle user turns show a muted Retry control on `.turn-block__user` (`icons/retry.svg`); click re-sends that turn’s `Instruction` through `OnSubmit` / `PromptAsync` as a new turn (disabled while `SessionBusy`)
- **Provider:** resolved from selected/default slug; credentialed `demo` rows are treated as `OpenAICompatible`
- **Demo tools:** first turn is `InitializeSession` with the user prompt visible in chat; demo mocks `RenameSession` only on every-8 review turns (title from prompt) plus staged mocks (`read_file` / `grep` / `list_dir`); demo also implements real subagent Start/Wait/Inspect/Stop/Submit paths against `CreateChildAsync` — only when the session is actually on the demo provider (no credentials)
- **Turn display:** `TurnBlock` always shows the user prompt (`.turn-block__user`) for `Normal` and `InitializeSession` turns alongside the assistant reply; harness kinds (`ExpandThoughtProcess`, completion flow) show their instruction in a muted strip (`.turn-block__instruction`). **Turn timestamps:** in `.turn-block__header` opposite the title (`.turn-block__timestamp`) — local `dd/MM/yyyy HH:mm` only (no `# turn started at` / `# turn ended at` prefixes), from `DysonAgentTurn.StartedUtc` / `CompletedUtc` (UI chrome, not written to assistant text or API history). In progress: start only; when `CompletedUtc` is set: `{start} – {end}`. Kind/id stay under the title (`.turn-block__header-main`) so the clock does not crowd the H1. While a turn’s reply is still in flight (streaming, pending tools, or session busy on the latest turn with no `AssistantText` yet), a compact accent spinner sits on the **right** of the user prompt row (`.turn-block__spinner`); on hover it becomes a danger cancel control (`.turn-block__cancel` / `.turn-block__cancel-icon`) and hides once `AssistantText` is set and streaming has finished. When the spinner is not shown, a muted Retry control (`.turn-block__user-retry` / `icons/retry.svg`) appears instead and resubmits the instruction when idle. OpenAI-compatible first prompts use `InitializeSession`; the every-8 rename-review mandate is sent to the model via transcript append on the current turn only (never re-emitted in later history), and is not shown in the UI.
- **Streaming text:** `DysonAgentTurn.StreamingPreview` + `IsStreaming` update live during SSE; `DysonUiHost` throttles `AssistantTextChanged` → `Notify()` (~75ms) while streaming, and flushes immediately on `FinishStreaming` / `ClearStreamingPreview` so Markdig replaces the plain-text preview without lag. While streaming, `TurnBlock` renders escaped plain text (raw, including mid-stream H1) with a blinking caret (`.turn-block__body--streaming`); title parse and Markdig run only when complete. Partial preview is not persisted.

Engine concepts: [docs/engine](../engine/README.md) · [orchestrator subagents](../engine/README.md)#orchestrator-subagents. Persistence: [docs/storage](../storage/sessions.md) · [work-directories](../storage/work-directories.md).
