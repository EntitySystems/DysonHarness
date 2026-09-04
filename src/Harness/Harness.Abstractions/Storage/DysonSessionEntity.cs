namespace DysonHarness;

public enum DysonSessionStatus
{
    Active = 0,
    Completed = 1,
    Stopped = 2,
    Failed = 3,

    /// <summary>
    /// Terminal child/descendant state after durable process-restart recovery.
    /// Appended after existing values; do not renumber.
    /// </summary>
    Interrupted = 4,
}

public sealed class DysonSessionEntity
{
    public Guid Id { get; set; }

    /// <summary>Owning subject (<see cref="DysonSubjects.Local"/> or cloud subject id).</summary>
    public string SubjectId { get; set; } = "";

    public int RuntimeId { get; set; }
    public Guid? ParentSessionId { get; set; }
    public string AgentMode { get; set; } = "";
    public Guid? ModelSlugId { get; set; }
    public Guid? WorkDirectoryId { get; set; }

    /// <summary>
    /// Session-scoped reasoning_effort override. Null = fall back to slug default on resolve;
    /// empty = omit from request.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Session max target context tokens. Null = inherit slug / harness default (100K);
    /// 0 = Off (no DropContext inject).
    /// </summary>
    public int? MaxTargetContextTokens { get; set; }

    public DysonMcpAccessMode McpAccessMode { get; set; }
    public DysonSessionStatus Status { get; set; }
    public string? Title { get; set; }
    public string SystemPromptSnapshot { get; set; } = "";

    /// <summary>Composer worktree checkbox. Default false; existing rows stay false.</summary>
    public bool WorktreeEnabled { get; set; }

    /// <summary>Absolute path of the session git worktree; null until created.</summary>
    public string? WorktreeAbsolutePath { get; set; }

    /// <summary>Worktree branch name (e.g. <c>dyson/{8-hex}</c>); null until created.</summary>
    public string? WorktreeBranch { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime LastActivityUtc { get; set; }

    public DysonSessionEntity? ParentSession { get; set; }
    public DysonModelSlugEntity? ModelSlug { get; set; }
    public DysonWorkDirectoryEntity? WorkDirectory { get; set; }
    public List<DysonTurnEntity> Turns { get; set; } = [];
    public List<DysonSessionLogEntry> Logs { get; set; } = [];
    public List<DysonSessionTodoEntity> Todos { get; set; } = [];
}
