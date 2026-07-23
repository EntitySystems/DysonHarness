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

`DysonGitInfo.TryGetBranch(absolutePath)` runs `git -C path rev-parse --abbrev-ref HEAD` (≈2s timeout). Used for the composer branch chip; unrelated to build-time `DysonBuildInfo.BranchName`.

## UI

Sidebar `WorkDirectorySwitcher` lists registered dirs, persists active id in `localStorage` (`dyson-workdir`), filters `SessionList` by that id. See [docs/ui/README.md](../ui/README.md).
