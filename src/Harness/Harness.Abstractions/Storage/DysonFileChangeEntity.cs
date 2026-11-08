namespace DysonHarness;

/// <summary>
/// EF row for table <c>file_changes</c>: one file write an agent made through the harness file tools,
/// keyed to the top-level (root) session. Holds the changed lines with one line of context on each side.
/// Read by the Meta page Change Stream modal; see <c>docs/storage/file-changes.md</c>.
/// </summary>
public sealed class DysonFileChangeEntity
{
    /// <summary>
    /// Auto-increment id (monotonic; newest-first paging orders by it). Stored as signed <c>INTEGER</c>
    /// through a converter in <c>DysonDbContext</c>; leave 0 on insert so SQLite assigns it.
    /// </summary>
    public ulong Id { get; set; }

    /// <summary>Owning subject (<see cref="DysonSubjects.Local"/> or cloud subject id). Set by the repository.</summary>
    public string SubjectId { get; set; } = "";

    /// <summary>Top-level session of the agent tree. Cascade-deleted with that session.</summary>
    public Guid RootSessionId { get; set; }

    /// <summary>The session that made the change. Set to null when that session is deleted.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>The executor's work directory id, when known.</summary>
    public Guid? WorkDirectoryId { get; set; }

    /// <summary>Native root the path is relative to (worktree or checkout). Used by Open file.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>Path relative to <see cref="RootPath"/>, forward slashes.</summary>
    public string RelativePath { get; set; } = "";

    public DysonFileChangeKind Kind { get; set; }

    /// <summary>1-based first changed line.</summary>
    public int StartLine { get; set; }

    /// <summary>One unchanged line before the change (≤ 500 chars), or null.</summary>
    public string? ContextBefore { get; set; }

    /// <summary>Removed lines (≤ 64 KiB), or null.</summary>
    public string? RemovedText { get; set; }

    /// <summary>Added lines (≤ 64 KiB), or null.</summary>
    public string? AddedText { get; set; }

    /// <summary>One unchanged line after the change (≤ 500 chars), or null.</summary>
    public string? ContextAfter { get; set; }

    /// <summary>True when a text field was cut at the cap.</summary>
    public bool IsTruncated { get; set; }

    /// <summary>Byte length for <see cref="DysonFileChangeKind.Binary"/> rows.</summary>
    public long? ByteLength { get; set; }

    /// <summary>Worktree folder name (leaf of the worktree path), or null outside a worktree.</summary>
    public string? WorktreeName { get; set; }

    /// <summary>Worktree branch, else the checkout branch, or null when unknown.</summary>
    public string? BranchName { get; set; }

    /// <summary>Runtime id chain from the root's child down to the writer, e.g. <c>#3-&gt;#1</c>. Empty for the root.</summary>
    public string AgentChain { get; set; } = "";

    /// <summary>Tool that wrote the file (<c>WriteFile</c>, <c>CreateFile</c>, ...).</summary>
    public string ToolName { get; set; } = "";

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    public DysonSessionEntity? RootSession { get; set; }
}

/// <summary>Kind of file change (<c>file_changes.Kind</c>). Keep values stable; a <c>Deleted</c> kind may follow.</summary>
public enum DysonFileChangeKind
{
    Created = 0,
    Edited = 1,
    Overwritten = 2,
    Binary = 3,
}
