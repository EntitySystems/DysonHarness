# DysonHarness

Early-stage AI model harness from [EntitySystems](https://github.com/EntitySystems).

Scaffolding and agent notes live in [AGENTS.md](AGENTS.md).

## Docs

- [Engine](docs/engine/README.md) — session loop, modes, MCP, tools, completion, optimizer
- [Engine API surface](docs/engine/api-surface.md) — public bindable types
- [Model profiles & app data](docs/storage/models.md) — app mode, SQLite paths, ephemeral providers
- [Sessions & resume](docs/storage/sessions.md) — turns, session log, `GetFullSession`
- [UI](docs/ui/README.md) — Blazor Interactive Server (`Harness.UI`)
- [WebView2 packaging](docs/packaging/webview.md) — future Windows host

## Rules

- [C#](rules/rules_csharp.md) · [Skills](rules/rules_skills.md) · [Docs](rules/rules_docs.md)

Copyright (C) 2026 EntitySystems. Licensed under [AGPL-3.0](LICENSE).
