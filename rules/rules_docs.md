# Documentation rules

When code changes, **update the matching docs in the same change** (or immediately after). Do not leave docs stale relative to behavior you just shipped.

## Where docs live

| Area | Path | Update when you change… |
| ---- | ---- | ------------------------ |
| Engine | [`docs/engine/`](../docs/engine/) | Session loop, modes, MCP, staging, interrupts, completion, optimizer, Result usage, public bindable types → `README.md` / `api-surface.md` |
| Storage | [`docs/storage/`](../docs/storage/) | App mode, paths, SQLite, model profiles → `models.md`; sessions, turns, log kinds, `GetFullSession` / resume → `sessions.md` |
| UI | [`docs/ui/`](../docs/ui/) | Blazor project layout, components, theme, how to run → `README.md` |
| Packaging | [`docs/packaging/`](../docs/packaging/) | Desktop / WebView2 (or other) host packaging → e.g. `webview.md` |

Index links: [`AGENTS.md`](../AGENTS.md), [`README.md`](../README.md).

## Expectations

- Prefer editing existing pages over adding parallel docs for the same topic.
- If you add a public type or persistence field the UI/hosts bind to, reflect it in `docs/engine/api-surface.md` and/or `docs/storage/`.
- If behavior is still planned (not implemented), say so briefly; when it lands, rewrite the “planned” note to match reality.
- Do not invent a second docs tree outside `/docs`.
