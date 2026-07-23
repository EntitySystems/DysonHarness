# UI

Landing project: [`src/Harness/Harness.UI`](../../src/Harness/Harness.UI) — Blazor Interactive Server (`net10.0`), references `Harness.Engine`.

## How to run

From repo root:

```bash
dotnet run --project src/Harness/Harness.UI --urls http://localhost:5180
```

Or open the solution and set **Harness.UI** as the startup project. The app uses Interactive Server rendering globally.

DI (scoped): `ThemeService`, `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonWorkDirectoryStore`, `DysonUiHost`.

On first open, a default **Demo Mock** provider + slug is seeded if none exists. SQLite lives under the platform app-data folder for the current `DysonAppMode` (see [storage/models](../storage/models.md)).

## Routes

| Route | Layout | Role |
| ----- | ------ | ---- |
| `/` | `MainLayout` → `AppShell` | Agent shell: workdirs + sessions + chat |
| `/settings` | `SettingsLayout` | Redirects to `/settings/general` |
| `/settings/general` | `SettingsLayout` | Theme / accent (`ThemeSwitcher`) |
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
| `Components/Chat/` | `ChatPanel`, `TurnBlock`, `Composer`, `AgentModePicker` |
| `Components/Tools/` | `ToolCallPanel`, `ToolCallRow` |
| `Components/Models/` | `ModelsPanel` (settings CRUD), `ModelSlugPicker` (agent pick) |
| `Components/Theme/` | `ThemeSwitcher` |
| `Theme/ThemeService.cs` | Theme/accent state + JS interop |
| `Demo/` | `DemoDysonEngine`, `DemoDysonAgentSession`, `DemoDysonAgentProvider`, `DysonUiHost` |
| `wwwroot/app.css` | Charcoal IDE theme (CSS variables) |
| `wwwroot/theme.js` | `localStorage` theme + active workdir (`dyson-workdir`) |

## Component map

| Component | Role |
| --------- | ---- |
| `AppShell` | Sidebar \| main \| right rail |
| `Sidebar` | Work directory switcher, sessions, Settings link, app-mode badge |
| `WorkDirectorySwitcher` | Register/switch/remove workdirs; native folder pick via `DysonNativeFolderPicker` |
| `SessionList` | Sessions for active workdir; **New** disabled until a workdir is selected |
| `ModelSlugPicker` | Compact chip + search modal for **New** session slug |
| `AgentModePicker` | Compact chip + search modal of `DysonAgentModes.BuiltIns` (always enabled for next New session) |
| `ChatPanel` | Transcript; forwards model / mode / git branch to Composer |
| `TurnBlock` | Single turn (title, body, tools) |
| `Composer` | Prompt + left-aligned toolbar (model chip, mode, git branch chip) |
| `ToolCallPanel` / `ToolCallRow` | Live tool status |
| `ThemeSwitcher` | Light/Dark + Blue/Green/Red/Purple (settings → General) |
| `SessionHeader` | Title (`DisplayTitle`), mode, ids, MCP, git branch, app mode |
| `ModelsPanel` | Provider/slug CRUD — settings → Models |
| `SettingsLayout` | Settings side nav + content |

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

- **New session:** `StartNewSessionAsync(agentMode, modelSlugId, workDirectoryId)` — workdir required → `CreateSessionAsync` → `DemoDysonAgentSession`
- **Resume:** `GetFullSessionAsync` → `RestoreFromPersisted` (sets `DisplayTitle` from `Title`)
- **Rename:** demo tool executor handles `RenameSession` → `RenameAsync` + persist `Title` + `SessionRenamed` log; host `SessionRenamed` notifies UI to refresh list/header
- **Provider:** ephemeral `DemoDysonAgentProvider` from selected/default slug
- **Tools:** first turn includes `RenameSession` (title from prompt) plus staged mocks (`read_file` / `grep` / `list_dir`)

Engine concepts: [docs/engine](../engine/README.md). Persistence: [docs/storage](../storage/sessions.md) · [work-directories](../storage/work-directories.md).
