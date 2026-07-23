# UI

Landing project: [`src/Harness/Harness.UI`](../../src/Harness/Harness.UI) — Blazor Interactive Server (`net10.0`), references `Harness.Engine`.

## How to run

From repo root:

```bash
dotnet run --project src/Harness/Harness.UI
```

Or open the solution and set **Harness.UI** as the startup project. The app uses Interactive Server rendering globally.

DI (scoped): `ThemeService`, `DysonDbContext`, `DysonModelStore`, `DysonSessionStore`, `DysonUiHost`.

On first open, a default **Demo Mock** provider + slug is seeded if none exists. SQLite lives under the platform app-data folder for the current `DysonAppMode` (see [storage/models](../storage/models.md)).

## Layout

| Path | Role |
| ---- | ---- |
| `Components/Pages/Home.razor` | Main IDE shell page |
| `Components/Shell/` | `AppShell`, `Sidebar`, `SessionHeader` |
| `Components/Sessions/` | `SessionList` |
| `Components/Chat/` | `ChatPanel`, `TurnBlock`, `Composer` |
| `Components/Tools/` | `ToolCallPanel`, `ToolCallRow` |
| `Components/Models/` | `ModelsPanel` |
| `Components/Theme/` | `ThemeSwitcher` |
| `Theme/ThemeService.cs` | Theme/accent state + JS interop |
| `Demo/` | `DemoDysonEngine`, `DemoDysonAgentSession`, `DemoDysonAgentProvider`, `DysonUiHost` |
| `wwwroot/app.css` | Charcoal IDE theme (CSS variables) |
| `wwwroot/theme.js` | `localStorage` theme persistence |

## Component map

| Component | Role |
| --------- | ---- |
| `AppShell` | Sidebar \| main \| right rail |
| `Sidebar` | Session list entry, models, theme, app-mode badge |
| `SessionList` | List persisted sessions; **New** / **Resume** |
| `ChatPanel` | Transcript container |
| `TurnBlock` | Single turn (title, body, tools) |
| `Composer` | Prompt input (Send / Ctrl+Enter) |
| `ToolCallPanel` / `ToolCallRow` | Live tool status |
| `ThemeSwitcher` | Light/Dark + Blue/Green/Red/Purple |
| `SessionHeader` | Agent mode, runtime id, persistence id, MCP, app mode |
| `ModelsPanel` | Two-level CRUD: providers (kind, base URL, API key) with nested slugs (alias, slug, default) |

## Theming

- CSS variables with `data-theme` (light/dark) and `data-accent` (Blue / Green / Red / Purple)
- Persist preference via JS interop (`theme.js`) + `localStorage`
- Visual direction: Cursor/Factory charcoal IDE look — dense, functional, not marketing

`ThemeService` + `ThemeSwitcher` own the applied attributes.

## Demo host (`DysonUiHost`)

- **New session:** `CreateSessionAsync` → `DemoDysonAgentSession` → persist turns/logs as work runs
- **Resume:** `GetFullSessionAsync` → `RestoreFromPersisted` → continue `PromptAsync`
- **Provider:** ephemeral `DemoDysonAgentProvider` from selected/default model slug (+ parent provider credentials)
- **Models UI:** `ModelsPanel` — provider cards with nested slug list; add/remove slugs under a provider without duplicating credentials
- **Session header:** shows slug `DisplayAlias` · provider name / API slug
- **Tools:** staged mock calls via `DysonToolCallScheduler` (`read_file` + `grep` stage 0, then `list_dir` stage 1)

Engine concepts: [docs/engine](../engine/README.md). Persistence: [docs/storage](../storage/sessions.md).
