# How to use these rules (Cursor)

Rules in this directory follow the openrules spec (see [../docs/openrules/README.md](../docs/openrules/README.md)) — each file is markdown with a YAML front matter header (`description`, `mode`, optional `providers`).

## Rule lifecycle for Cursor users

1. **Author/edit rules in `rules/` (repo root), not here.** Files under `.cursor/rules/` are *projections* of `rules/*.md` — create or update the `rules/*.md` source file, then regenerate the `.cursor/rules/` copies.
2. **Adding a rule:** write `rules/rules_<name>.md` with the front matter above, add it to the `[Rules]` array in the repo root `openrules.json` (Path, Mode, Description, Providers), and list it in the Root document (`AGENTS.md`) Rules section.
3. **Deleting a rule:** remove the `rules/*.md` file and its `openrules.json` + `AGENTS.md` entries, then remove the stale `.cursor/rules/*.mdc` projection.
4. **Keep rules focused** — one concern per file; prefer editing an existing rule file over adding overlapping ones.
5. `AGENTS.md` is the Root document (always loaded). Use it only for the high-level map of the repo; put detailed guidance in rule files.
