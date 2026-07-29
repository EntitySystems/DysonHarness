# Work directories

Registered local folders that own agent sessions (Cursor-style workspace roots). Same SQLite DB as models/sessions ([models.md](models.md)).

## Schema

### `work_directories`

| Property | Notes |
| -------- | ----- |
| `Id` | Guid PK |
| `Name` | Display name (defaults to folder name) |
| `AbsolutePath` | Normalized full path; **unique** index |
| `CreatedUtc`, `LastOpenedUtc` | `DateTime` UTC |

### Sessions link

`sessions.WorkDirectoryId` → `work_directories.Id` (`OnDelete(SetNull)`). Existing rows may be null; **new sessions require** a selected work directory.

## `DysonWorkDirectoryStore`

Result-pattern concrete store:

- `CreateAsync(absolutePath, name?)` — normalize path, require directory exists, unique path
- `GetAsync` / `ListAsync` (ordered by `LastOpenedUtc` desc)
- `TouchOpenedAsync` — bump `LastOpenedUtc` when switching active
- `DeleteAsync` — removes registration only (not disk folder); **blocked** if any sessions still reference the id

## Native folder pick

`DysonNativeFolderPicker.PickFolderAsync()` opens a host-process OS dialog (Windows `IFileOpenDialog` folders, macOS `osascript`, Linux `zenity`/`kdialog`). Blazor Interactive Server calls this from C# on the server machine — requires an interactive desktop session. Same API is intended for a future WebView2 host ([packaging/webview.md](../packaging/webview.md)).

## Git branch (UI)

`DysonGitInfo.TryGetBranch` accepts a native absolute path or an initialized `IDysonWorkspaceFileSystem` (uses `NativeRootPath`). Runs `git -C path rev-parse --abbrev-ref HEAD` (≈2s timeout). Used for the composer branch chip; unrelated to build-time `DysonBuildInfo.BranchName`.

## Workspace filesystem

Sandboxed IO for tools, FileManager, file tree, and viewers goes through `IDysonWorkspaceFileSystem` (contracts in `Harness.Abstractions`):

- Call `InitializeAsync(subjectId)` before any IO or watcher creation. Local subject is fixed: `DysonWorkspaceSubjects.LocalFs` (`"local_fs"`). Wrong subjects are rejected; IO before init fails.
- `NativeRootPath` is always the host-visible root for shells, `git -C`, and `Process.WorkingDirectory` (local path, mapped drive, or UNC/SMB mount — including Azure Files mounts).
- Prefer `DysonWorkspaceFileSystems.CreateLocalAsync(absolutePath)` — validates the directory exists, constructs `DysonLocalWorkspaceFileSystem`, and initializes with `"local_fs"`.
- Live updates: `CreateWatcher()` → `IDysonWorkspaceChangeWatcher` (`FileSystemWatcher` on the native root today).
- `Move(sourceRelativePath, destinationRelativePath)` renames/moves files or directories inside the sandbox (rejects escape, destination collision, and moving a directory into itself). The rail file tree uses this for folder rename from the folder context menu; watcher `Renamed` events refresh the tree.

For Azure Files (and similar) on this product path: mount the share (credentials/lifecycle owned by the host), then use `CreateLocalAsync` over the mount path — not a separate byte-API backend. `work_directories.AbsolutePath` remains the native root string. Custom `IDysonWorkspaceFileSystem` implementations are for cloud hosts that need different storage semantics (see below).

### Cloud hosting / custom implementations

Planned guidance (not a shipped cloud host): Dyson ships **only** `DysonLocalWorkspaceFileSystem` — local disk, SMB, and UNC mounts (including Azure Files mounts). That is the desktop / single-host path.

Cloud or multi-tenant hosts **must implement** their own `IDysonWorkspaceFileSystem` (and usually `IDysonWorkspaceChangeWatcher`) to match their storage, auth, and isolation model. Wire it through the same session, tool, and UI call sites that today use `CreateLocalAsync`.

- Use `InitializeAsync(subjectId)` for auth and partitioning. Local uses `"local_fs"`; cloud subjects are host-defined.
- `NativeRootPath` remains required for shells and `git`. Cloud hosts that keep `ShellExecute` must still expose a host-visible path (e.g. per-session mount or sandbox cwd). If a host cannot provide that, it must gate or replace shell tools itself.
- Other host concerns cloud consumers typically own (not provided by the local desktop app): shared/multi-tenant persistence instead of per-user LocalAppData SQLite, identity, per-user/session shell isolation, folder-pick UX, and process-wide UI singletons (file tree / long-running shells). See existing engine and packaging docs for current single-host limits — this subsection does not define a full multi-tenant design.

## Workspace artifacts (`.dyson`)

Plan mode publishes markdown under `{workRoot}/.dyson/plans/{slug}-{hash}.md` via `DysonFileManager` (constructed from an initialized workspace FS) / `SubmitPlan`. Paths stay sandboxed under the work root. See [engine README](../engine/README.md) (Plan artifacts).

Agent skills may live under `{workRoot}/.dyson/skills/{name}/` (entry `SKILL.md` or first `*.md`). `LoadSkill` / composer `/skill-` resolve **included** embedded `Resources/Skills` first, then `.dyson/skills`, then a literal work-relative path, then **openrules.json `AgentOptional`** Rules/Skills (local or http(s) `Path`; optional `Providers` filter). See [docs/openrules/README.md](../openrules/README.md). Work-root `openrules.json` (or implicit `AGENTS.md`) also injects Root + provider-filtered `AutoInclude` entries into the session system prompt on create/load/mode change. MCP **`InitializeOpenRules`** creates a default manifest (EntitySystems openrules `SKILL.md` URL, no `Providers`) when the file is missing.

## UI

Sidebar `WorkDirectorySwitcher` lists registered dirs, persists active id in `localStorage` (`dyson-workdir`), filters `SessionList` by that id. Right-rail **Files** tree: right-click a **folder** for Rename (inline; calls workspace `Move`) or Open in Explorer / Finder / file manager (`DysonUiHost.OpenFolderInFileManager`). See [docs/ui/README.md](../ui/README.md).
