# openrules.json

Work-directory root manifest that controls which project docs are always injected into the agent **system prompt** versus available on demand via `LoadSkill` / composer `/skill-`.

## Schema

```json
{
  "Root": "AGENTS.md",
  "Rules": [
    {
      "Path": "./path/to/rule.md",
      "Mode": "AutoInclude",
      "Description": "…",
      "Providers": ["claude", "cursor"]
    },
    {
      "Path": "./path/to/other.md",
      "Mode": "AgentOptional",
      "Description": "…"
    }
  ],
  "Skills": [
    {
      "Path": "https://github.com/EntitySystems/openrules/blob/main/SKILL.md",
      "Mode": "AgentOptional",
      "Description": "OpenRules skill — how agents should load and interpret openrules.json"
    }
  ]
}
```

| Field | Meaning |
| ----- | ------- |
| `Root` | Work-relative master file (always AutoInclude). Default `AGENTS.md` when missing/empty. |
| `Rules` / `Skills` | Same entry shape; Skills are labeled as skills in the prompt / catalog. |
| `Path` | Work-relative text file **or** absolute `http://` / `https://` URL. Local paths load via workspace FS; URLs are GET-fetched (SSRF-guarded via `SearchHttp`) for AutoInclude / `LoadSkill`. |
| `Mode` | Exactly `AutoInclude` or `AgentOptional`. |
| `Description` | Optional; used as catalog display name for AgentOptional. |
| `Providers` | Optional string array. Omitted or `[]` → all agents (including Dyson). Non-empty → load only when the runtime provider id is listed (case-insensitive). Dyson’s id is `dyson` (`DysonOpenRulesProviders.Dyson`). |

Property names are PascalCase (as shown). Nested `AGENTS.md` files are **not** auto-discovered — only `Root` plus listed entries. Do not seed `Providers` on the EntitySystems openrules skill by default — agents/users add filtering when they want it.

## Defaults when `openrules.json` is missing

- If `AGENTS.md` exists at the work root → implicit `{ "Root": "AGENTS.md", "Rules": [], "Skills": [] }`.
- Otherwise → no open-rules system-prompt block.
- Prefer MCP **`InitializeOpenRules`** to create a default manifest (see below) rather than relying on the implicit Root-only behavior.

## System prompt (AutoInclude)

On session **create / load / child spawn / mode switch**, the harness appends an open-rules block after the available-models catalog:

1. `[OpenRules Root: …]` + file body
2. Each `AutoInclude` Rule/Skill that **applies to the runtime provider** (`dyson` by default), with path, optional description, and body (local FS or URL fetch)

Missing files / failed URL fetches become a short warning line (session create does not fail). Soft caps: **50 000** characters per file, **100 000** total for the block (`DysonOpenRules.MaxCharsPerFile` / `MaxTotalChars`). Content is a snapshot at create/load/mode change — not re-read every turn.

## AgentOptional + catalog

`AgentOptional` Rules and Skills are **not** injected into the system prompt. They extend (provider-filtered):

- MCP `LoadSkill` resolve order (after included → `.dyson/skills` → literal); URL Paths are fetched
- Composer `/skill-` via `DysonSkillLoader.ListCatalog` (`DysonSkillSource.OpenRules`)

Catalog / `/skill-` ids are **short names** (e.g. `csharp` for `skills/csharp/SKILL.md`, `openrules` for the EntitySystems GitHub `SKILL.md` URL, file stem for ordinary `.md` rules) — not full paths or URLs. Match by relative path, file stem, URL, short catalog id, or GitHub repo name when applicable. Single-file AgentOptional entries ignore `loadIndexOnly` (same as literal files).

Do not `LoadSkill` AutoInclude entries to re-inject them into the turn — they are already in the system prompt.

## MCP: `GetOpenRulesConfig`

No required args. Returns a JSON summary (no bodies) of **all** manifest rows (no provider filter): `Root` + `RootExists`, each Rule/Skill as `{ Path, Mode, Description, exists, isUrl, Providers }`, plus `manifestPresent` / `note` when the manifest is missing. Loading / catalog still filters by provider.

## MCP: `InitializeOpenRules`

No required args.

- If `openrules.json` **exists** → return `{ created: false, openrules: <file JSON> }` (no overwrite).
- If **missing** → write the default document, then return `{ created: true, openrules: <file JSON> }`.

Default document:

```json
{
  "Root": "AGENTS.md",
  "Rules": [],
  "Skills": [
    {
      "Path": "https://github.com/EntitySystems/openrules/blob/main/SKILL.md",
      "Mode": "AgentOptional",
      "Description": "OpenRules skill — how agents should load and interpret openrules.json"
    }
  ]
}
```

No `Providers` on the seeded skill.

## This repository

Repo-root [`openrules.json`](../../openrules.json) sets `Root: "AGENTS.md"`, lists `rules/*.md` + `skills/*/SKILL.md` as AgentOptional, and includes the EntitySystems openrules skill URL (no `Providers`) so `/skill-` and `LoadSkill` can reach them.

## Related

- Engine: [docs/engine/README.md](../engine/README.md) · [api-surface](../engine/api-surface.md)
- Work directories: [docs/storage/work-directories.md](../storage/work-directories.md)
- UI slash catalog: [docs/ui/README.md](../ui/README.md)
