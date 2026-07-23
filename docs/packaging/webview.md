# Windows WebView2 host (future)

Out of scope for the current Blazor Interactive Server phase. This note records the intended packaging direction so agents and humans do not invent a conflicting host story.

## Intent

Ship a **Windows desktop shell** that hosts the same Dyson UI (or a thin host page) inside **WebView2**, talking to the existing `Harness.Engine` and SQLite app-data store.

| Piece | Role |
| ----- | ---- |
| Native host | WinForms / WPF / WinUI window + WebView2 control |
| Web content | Blazor UI (or static host that bootstraps the same components) |
| Engine | In-process `Harness.Engine` (same `DysonAppPaths` / `dyson.db`) |
| IPC | Prefer in-process DI over a separate HTTP stack when co-hosted |

## Constraints (when implemented)

- Reuse app mode + platform paths from [docs/storage/models.md](../storage/models.md) — one `dyson.db` per mode folder
- Do not fork session/resume semantics; use `DysonSessionStore.GetFullSessionAsync` / restore
- Keep providers ephemeral; model profiles remain SQLite rows
- WebView2 evergreen runtime is a deployment dependency on Windows

## Non-goals (for now)

- macOS/Linux native WebView shells
- Multi-user / remote auth
- Replacing Interactive Server for local dev — desktop packaging is additive

Update this page when a host project is added under `src/Harness/` (or similar).
