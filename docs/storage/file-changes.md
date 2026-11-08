# File changes (`file_changes`) — Change Stream

Every file an agent creates or edits through the harness file tools gets one row, keyed to the **top-level (root) session** of its agent tree. The Meta page **Changes** button reads it ([ui/README.md](../ui/README.md)). Plan 259.

Contracts: entity `DysonFileChangeEntity`, enum `DysonFileChangeKind`, repository `IDysonFileChangeRepository` (`Harness.Abstractions/Storage/`). SQLite impl `DysonFileChangeRepository` (`Harness.LocalDb`), registered scoped by `AddDysonLocalDb`. Migration `AddFileChanges`. Engine access through `DysonAgentSessionConfig.FileChanges` (children share the config); null means nothing is logged.

## Columns

| Column | Notes |
| ------ | ----- |
| `Id` | `ulong`, stored as signed `INTEGER` autoincrement (same unchecked converter as `pending_user_questions`). Monotonic; newest-first paging orders by it |
| `SubjectId` | Owning subject; every query filters it |
| `RootSessionId` | Guid FK → `sessions`, **cascade**. Deleting the root session deletes its stream |
| `SessionId` | Guid? FK → `sessions`, **set null**. The session that wrote the file |
| `WorkDirectoryId` | Guid? — the executor's work directory |
| `RootPath` | Native root the path is relative to (worktree or checkout); used by **Open file** |
| `RelativePath` | Forward slashes |
| `Kind` | int: `Created` 0, `Edited` 1, `Overwritten` 2, `Binary` 3 (no `Deleted` yet; no tool deletes files) |
| `StartLine` | 1-based first changed line |
| `ContextBefore`, `ContextAfter` | One unchanged line each, ≤ 500 chars |
| `RemovedText`, `AddedText` | ≤ 64 KiB each, cut with `\n… [truncated N chars]` |
| `IsTruncated` | A text field hit the cap |
| `ByteLength` | Binary rows only |
| `WorktreeName`, `BranchName` | Nearest worktree up the `Parent` chain (`WorktreeAbsolutePath` leaf, `WorktreeBranch`), else the checkout branch from `DysonGitInfo.TryGetBranchAsync` (cached once per executor) |
| `AgentChain` | Runtime ids from the root's child down to the writer, e.g. `#3->#1`; empty when the root wrote it. Snapshot at write time, so it survives deletes and restarts |
| `ToolName` | `WriteFile`, `CreateFile`, ... |
| `CreatedUtc` | `DateTime` UTC |

Indexes: (`RootSessionId`, `Id`) and (`SubjectId`, `RootSessionId`).

## Diff

`DysonFileChangeDiff` (Engine, pure): normalizes EOL, trims the common prefix and suffix lines, and keeps one context line on each side. One hunk only: two distant edits in one `edits[]` call show as one block (ponytail; upgrade path is an LCS diff). Identical before/after logs nothing. Paths that look like secrets (`.env*`, `*.pem`, `*.key`, `*.pfx`, `secrets.*`, `*.secrets.json`) are logged with the text replaced by `[content not logged]`.

## Logged tools

| Tool | Kind |
| ---- | ---- |
| `CreateFile` (not `isTempFile`) | `Created`, or `Overwritten` when the file existed |
| `WriteFile` `content` | `Overwritten` (diff against the prior text), or `Created` |
| `WriteFile` `old_text`/`new_text`/`edits[]` | `Edited` |
| `ConvertImage`, `GenerateImage` | `Binary` (byte length only) |
| `SubmitPlan` | `Created` (`.dyson/plans/*.md`) |
| `InitializeOpenRules` | `Created`, only when it created the file |

Not logged: `CreateFile isTempFile` / `WriteTempFile` (`.dyson/temp/`), `CreateDirectory`, Meta notes, `WriteSecureDataToFile` (never), DB-backed tools (plans, scratch, memory), custom/plugin MCP tools, and **`ShellExecute` / long-running shells** (arbitrary process writes cannot be attributed or diffed; a follow-up could diff `git status --porcelain` around each call).

Logging never fails a tool: errors are swallowed into the session log. Unpersisted sessions (`PersistenceId` empty) log nothing. Non-Meta roots log too.

## Bus

After a successful append the executor publishes `DysonFileChangedEvent(RootSessionId, ChangeId)` on `DysonBusScopes.Session(root)`. It carries no diff; subscribers read the row with `GetAsync`.

## Retention

Newest **2,000 rows per root**. `AppendAsync` prunes older rows of that root when the new id is a multiple of 50 (ponytail: amortized, fixed cap; upgrade path is a settings key). Worst case about 130 KiB per row.
