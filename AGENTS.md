# DysonHarness

AI model harness.

## Todos

- [x] Define harness architecture
- [x] Add model provider interface
- [x] Wire up first provider (demo + OpenAI-compatible)
- [ ] Add evaluation / run loop

## Docs

- Engine: [docs/engine/README.md](docs/engine/README.md) · [api-surface](docs/engine/api-surface.md)
- Storage: [docs/storage/models.md](docs/storage/models.md) · [sessions](docs/storage/sessions.md) · [work-directories](docs/storage/work-directories.md)
- UI: [docs/ui/README.md](docs/ui/README.md)
- Packaging: [docs/packaging/webview.md](docs/packaging/webview.md)

## Rules

- C#: [rules/rules_csharp.md](rules/rules_csharp.md)
- Skills: [rules/rules_skills.md](rules/rules_skills.md)
- Docs: [rules/rules_docs.md](rules/rules_docs.md)
- UI restart: [rules/rules_ui_restart.md](rules/rules_ui_restart.md)
- Ponytail (lazy senior, mandatory): [rules/rules_ponytail.md](rules/rules_ponytail.md)

## Skills

All skills must live under [`skills/`](skills/).

- C# / .NET: [skills/csharp/SKILL.md](skills/csharp/SKILL.md)
