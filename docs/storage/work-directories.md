# Work directories

Registered local folders that own agent sessions (Cursor-style workspace roots). Same SQLite DB as models/sessions ([models.md](models.md)). Work directories are **subject-owned**; see [cloud-hosting.md](cloud-hosting.md).

Contracts: `IDysonWorkDirectoryRepository` in `Harness.Abstractions`. Implementation: `Harness.LocalDb`.

## Schema

### `work_directories`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `SubjectId` | Owning subject |
| `Name` | Display name (defaults to folder name) |
| `AbsolutePath` | Normalized full path; **unique** per `(SubjectId, AbsolutePath)` |
| `CreatedUtc`, `LastOpenedUtc` | `DateTime` UTC |
| `GitOrigin` | Raw `git remote get-url origin`, or null if not a git repo / no origin |
| `GitProvider` | Classified slug (`github` / `gitlab` / `azure-devops` / `cursor-origin` / `other`), or null |

### Sessions link

`sessions.WorkDirectoryId` → `work_directories.Id` (`OnDelete(SetNull)`). Existing rows may be null; **new sessions require** a selected work directory. Both session and workdir must belong to the same subject.

### `work_directory_configurations`

Per-workdir JSON config document (extensible `JsonNode`), cascade-deleted with the work directory.

| Property | Notes |
| -------- | ----- |
| `WorkDirectoryId` | Guid PK / FK → `work_directories.Id` (`OnDelete(Cascade)`) |
| `SubjectId` | Owning subject (same as workdir); indexed |
| `ConfigJson` | TEXT — serialized `JsonNode` |
| `UpdatedUtc` | `DateTime` UTC |

v1 keys:

```json
{ "mcpActive": true }
```

Missing row ⇒ treat as `{ "mcpActive": true }` (opt-out via settings). Helpers: `DysonWorkDirectoryConfig.TryGetMcpActive` / `WithMcpActive`.

### `IDysonWorkDirectoryConfigurationRepository`

- `GetAsync(workDirectoryId)` — stored doc or default (does not materialize)
- `UpsertAsync(workDirectoryId, JsonNode config)` — insert/replace

## Custom MCP (`.dyson/mcp`)

One JSON file per server: `{workRoot}/.dyson/mcp/{serverId}.json` (Cursor/Claude-shaped: `type`/`command`/`args`/`env`/`cwd` or `url`/`headers`; optional `disabled`, `envFile`; `${env:VAR}` expansion).

- **Master switch:** workdir `mcpActive` (DB)
- **Per-server:** `disabled` in the JSON file
- Engine: `DysonCustomMcpHost` (workdir refcount) + `DysonCustomMcpPromptUpdater` (FileSystemWatcher + debounce) merge `{serverId}__{toolName}` into session catalogs when active
- UI: cog on each workdir row → `WorkDirectorySettingsModal` (toggle, add/edit/restart/delete servers)

## `IDysonWorkDirectoryRepository`

Result-pattern functional repository (current subject only; cross-subject get-by-id → error). SQLite busy/locked contention on **every** method returns an error Result (never throws) after `DysonDbAccessor.SaveChangesAsync` retries are exhausted; other failures (not found, generic errors) still return error Results as before. This is defensive hardening for concurrent harness processes — measured boot reads/writes here are ~10-60ms and were never the app-boot bottleneck (see [ui/README.md](../ui/README.md#work-directories)).

- `CreateAsync(absolutePath, name?)` — normalize path, require directory exists, unique path within subject
- `GetAsync` / `ListAsync` (ordered by `LastOpenedUtc` desc)
- `TouchOpenedAsync` — bump `LastOpenedUtc` when switching active. `LastOpenedUtc` is bookkeeping and a failed bump is tolerable.
- `UpdateGitMetadataAsync(id, gitOrigin, gitProvider)` — persist or clear classified origin (both values may be null). Boot/activation (`RefreshGitOriginAsync`) is a write on the startup path; busy/locked must not throw.
- `DeleteAsync` — removes registration only (not disk folder); **blocked** if any sessions still reference the id

## Git origin refresh (`DysonWorkDirectoryService`)

Concrete Engine type (no extra interface). `RefreshGitOriginAsync` runs `DysonGitInfo.TryGetOrigin` on the registered `AbsolutePath`, classifies with `ClassifyProvider` / `ToStoredSlug`, and writes both columns via `UpdateGitMetadataAsync`. Detection failure (no git, timeout, no origin) writes `null`/`null` so a removed remote does not stay `github`. `GetAsync` failure is returned as-is (no invented row).

Refresh is **activation-only**, not every `GetAsync` (file tree, git rail, and settings stay hot reads):

1. `WorkDirectorySwitcher.AddAsync` after successful `CreateAsync`
2. `WorkDirectorySwitcher.SelectAsync` after successful `TouchOpenedAsync`
3. `Home.CompleteBootMetadataAsync` — stored-id boot defers `TouchOpenedAsync`, `RefreshGitOriginAsync`, and `RefreshGitBranchAsync` until after the splash dismisses, so none of them are on the awaited boot path. The fallback `ListAsync` activation path still refreshes inline.
4. `DysonUiAgentSessionRuntimeFactory.CreateRootAsync` / `LoadAsync` after a successful workdir `GetAsync`

`GetGitProvider(entity | stored, origin)` maps the stored slug (`cursor-origin` → `CursorOrigin`); empty stored classifies from origin; else `None`.

## Native folder pick

`DysonNativeFolderPicker.PickFolderAsync()` opens a host-process OS dialog (Windows `IFileOpenDialog` folders, macOS `osascript`, Linux `zenity`/`kdialog`). Blazor Interactive Server calls this from C# on the server machine — requires an interactive desktop session. Same API is intended for a future WebView2 host ([packaging/webview.md](../packaging/webview.md)).

## Git branch (UI)

`DysonGitInfo.TryGetBranch` accepts a native absolute path or an initialized `IDysonWorkspaceFileSystem` (uses `NativeRootPath`). Runs `git -C path rev-parse --abbrev-ref HEAD` (≈2s timeout). Used for the composer branch chip; unrelated to build-time `DysonBuildInfo.BranchName`.

## Workspace filesystem

Sandboxed IO for tools, FileManager, file tree, and viewers goes through `IDysonWorkspaceFileSystem` (contracts in `Harness.Abstractions`):

- Call `InitializeAsync(subjectId)` before any IO or watcher creation. Local subject is fixed: `DysonWorkspaceSubjects.LocalFs` (`"local_fs"`). This is **not** the same as persistence `DysonSubjects.Local` (`"local"`) — see [cloud-hosting.md](cloud-hosting.md). Wrong subjects are rejected; IO before init fails.
- `NativeRootPath` is always the host-visible root for shells, `git -C`, and `Process.WorkingDirectory` (local path, mapped drive, or UNC/SMB mount — including Azure Files mounts).
- Prefer `DysonWorkspaceFileSystems.CreateLocalAsync(absolutePath)` — validates the directory exists, constructs `DysonLocalWorkspaceFileSystem`, and initializes with `"local_fs"`.
- Live updates: `CreateWatcher()` → `IDysonWorkspaceChangeWatcher` (`FileSystemWatcher` on the native root today).
- I/O members are TAP (`FileExistsAsync`, `ReadAllTextAsync`, `WriteAllTextAsync`, `EnumerateEntriesAsync`, …). Path math (`ResolvePath` / `GetRelativePath`) and `CreateWatcher()` stay sync.
- `MoveAsync(sourceRelativePath, destinationRelativePath)` renames/moves files or directories inside the sandbox (rejects escape, destination collision, and moving a directory into itself). The rail file tree uses this for folder rename from the folder context menu; watcher `Renamed` events refresh the tree.

For Azure Files (and similar) on this product path: mount the share (credentials/lifecycle owned by the host), then use `CreateLocalAsync` over the mount path — not a separate byte-API backend. `work_directories.AbsolutePath` remains the native root string. Custom `IDysonWorkspaceFileSystem` implementations are for cloud hosts that need different storage semantics (see below).

### Cloud hosting / custom implementations

Persistence subjects, shared providers, cookies, and RBAC: [cloud-hosting.md](cloud-hosting.md).

Workspace FS note (not a shipped cloud host): Dyson ships **only** `DysonLocalWorkspaceFileSystem` — local disk, SMB, and UNC mounts (including Azure Files mounts). That is the desktop / single-host path.

Cloud or multi-tenant hosts **must implement** their own `IDysonWorkspaceFileSystem` (and usually `IDysonWorkspaceChangeWatcher`) to match their storage, auth, and isolation model. Wire it through the same session, tool, and UI call sites that today use `CreateLocalAsync`.

- Use `InitializeAsync(subjectId)` for auth and partitioning. Local uses `"local_fs"`; cloud subjects are host-defined.
- `NativeRootPath` remains required for shells and `git`. Cloud hosts that keep `ShellExecute` must still expose a host-visible path (e.g. per-session mount or sandbox cwd). If a host cannot provide that, it must gate or replace shell tools itself.
- Other host concerns cloud consumers typically own (not provided by the local desktop app): identity binding to persistence subjects, per-user/session shell isolation, folder-pick UX, and process-wide UI singletons (file tree / long-running shells). Shared/multi-tenant persistence uses the Abstred repository interfaces (LocalDb or an external provider) — not a second ad-hoc SQLite layout.

## Workspace artifacts (`.dyson`)

Plan mode publishes markdown under `{workRoot}/.dyson/plans/{slug}-{hash}.md` via `DysonFileManager` (constructed from an initialized workspace FS) / `SubmitPlan`. Paths stay sandboxed under the work root. See [engine README](../engine/README.md) (Plan artifacts).

`CreateFile(isTempFile: true)` creates visualization source artifacts under `{workRoot}/.dyson/temp/`. Temp mode accepts only a requested leaf name with an extension, sanitizes it, adds a cryptographically random 24-hex-character suffix before that extension, and returns the resulting workspace-relative path (for example, `.dyson/temp/chart-<random>.html`). The directory is git-ignored; files are bounded to 512 KiB UTF-8 each and are **not automatically cleaned up**. A later `RenderHtmlVisualization` call must use the exact returned path verbatim as its matching `tempFile`; it cannot infer or construct a temp path. There is no `CreateTempFile` MCP tool or automatic cleanup service.

Agent skills may live under `{workRoot}/.dyson/skills/{name}/` (entry `SKILL.md` or first `*.md`). `LoadSkill` / composer `/skill-` resolve **included** embedded `Resources/Skills` first, then `.dyson/skills`, then a literal work-relative path, then **openrules.json `AgentOptional`** Rules/Skills (local or http(s) `Path`; optional `Providers` filter). See [docs/openrules/README.md](../openrules/README.md). Work-root `openrules.json` (or implicit `AGENTS.md`) injects the raw `openrules.json` file (or a `(missing: openrules.json)` warning when implicit Root still applies) **before** Root and provider-filtered `AutoInclude` bodies into the session system prompt on create/load/mode change. MCP **`InitializeOpenRules`** creates a default manifest (EntitySystems openrules `SKILL.md` URL, no `Providers`) when the file is missing. Workdir settings (`WorkDirectorySettingsModal`) can flip Mode on existing Rules/Skills rows (`AgentOptional` ↔ `AutoInclude`) via `DysonOpenRules.SetEntryModeAsync`.

Composer **`/skill-search`** (Skills Directory and other explorer providers) installs registry packages into the same `.dyson/skills/{slug}/` tree **and** appends an openrules.json AgentOptional Skills reference to the installed `SKILL.md` (creates the default manifest if missing) — see [docs/ui](../ui/README.md)#skill-search.

### Project plugins

Composer **`/plugins`** can explicitly install a validated package for the active work directory:

- immutable package payload: `{workRoot}/.dyson/plugins/{normalized-plugin-id}/{version-or-content-id}/`
- client-managed persistent data: `{workRoot}/.dyson/plugin-data/{normalized-plugin-id}/`

A project install requires the active registered work-directory id plus its initialized `IDysonWorkspaceFileSystem`; the host never falls back to a global root or another work directory. The persisted installation row records that owning work-directory id, and repository/catalog reads expose project rows only for the requested subject-owned active work directory. For a duplicate normalized id, the project record shadows the global record for that workspace; components from the two scopes are never merged.

Preview is scope-independent and inert. The modal shows the exact project destination only after validation, leaves both project/global actions unselected, and disables project installation when there is no active work directory. Local folders are copied into staging rather than used in place. Package import does not mutate `.dyson/skills`, `.dyson/mcp`, or `openrules.json`, and does not execute package content.

Current limitation: project package inspection/enablement/uninstall APIs exist in the engine, but the UI currently exposes only the import flow; there is no installed-plugin work-directory settings panel yet.

## UI

Sidebar `WorkDirectorySwitcher` lists registered dirs, persists active id in `localStorage` (`dyson-workdir`), filters `SessionList` by that id. Right-rail **Files** tree: right-click a **folder** for Rename (inline; `await` workspace `MoveAsync`) or Open in Explorer / Finder / file manager (`DysonUiHost.OpenFolderInFileManager`). See [docs/ui/README.md](../ui/README.md).
