namespace DysonHarness;

public sealed class DysonSessionCreateRequest
{
    public int RuntimeId { get; init; }
    public Guid? ParentSessionId { get; init; }
    public required string AgentMode { get; init; }
    public Guid? ModelSlugId { get; init; }
    public Guid? WorkDirectoryId { get; init; }

    /// <summary>Copied from slug default on create; empty = omit; null = fall back later.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Session max target context. Null = inherit slug / harness default; 0 = Off.
    /// </summary>
    public int? MaxTargetContextTokens { get; init; }

    public DysonMcpAccessMode McpAccessMode { get; init; } = DysonMcpAccessMode.FullAccess;
    public string? Title { get; init; }
    public required string SystemPromptSnapshot { get; init; }
    public DysonSessionStatus Status { get; init; } = DysonSessionStatus.Active;
}

public sealed class DysonSessionMetaUpdate
{
    public Guid SessionId { get; init; }
    public DysonSessionStatus? Status { get; init; }
    public string? Title { get; init; }
    public Guid? ModelSlugId { get; init; }
    public bool ClearModelSlug { get; init; }

    /// <summary>When true, write <see cref="ReasoningEffort"/> (null/empty allowed).</summary>
    public bool UpdateReasoningEffort { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>When true, write <see cref="MaxTargetContextTokens"/> (null allowed = inherit).</summary>
    public bool UpdateMaxTargetContextTokens { get; init; }

    /// <summary>Session max target context; null = inherit; 0 = Off.</summary>
    public int? MaxTargetContextTokens { get; init; }

    /// <summary>When set, updates persisted <c>AgentMode</c> (mid-session mode switch).</summary>
    public string? AgentMode { get; init; }

    /// <summary>When set, updates persisted <c>SystemPromptSnapshot</c>.</summary>
    public string? SystemPromptSnapshot { get; init; }
}

public sealed class DysonSessionSummary
{
    public Guid Id { get; init; }
    public int RuntimeId { get; init; }
    public Guid? ParentSessionId { get; init; }
    public string AgentMode { get; init; } = "";
    public DysonSessionStatus Status { get; init; }
    public string? Title { get; init; }
    public Guid? ModelSlugId { get; init; }
    public Guid? WorkDirectoryId { get; init; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>UTC.</summary>
    public DateTime LastActivityUtc { get; init; }
}

public sealed class DysonPersistedSession
{
    public required DysonSessionEntity Session { get; init; }
    public required IReadOnlyList<DysonTurnEntity> Turns { get; init; }
    public required IReadOnlyList<DysonSessionLogEntry> Logs { get; init; }
    public required IReadOnlyList<DysonSessionTodo> Todos { get; init; }
}

public sealed class DysonSessionTodoCreateRequest
{
    public Guid SessionId { get; init; }
    public required string TaskCode { get; init; }
    public required string DisplayName { get; init; }
    public DysonSessionTodoStatus Status { get; init; } = DysonSessionTodoStatus.Pending;
    public IReadOnlyList<string>? Comments { get; init; }
}

public sealed class DysonSessionTodoUpdateRequest
{
    public Guid SessionId { get; init; }
    public required string TaskCode { get; init; }
    public string? DisplayName { get; init; }
    public DysonSessionTodoStatus? Status { get; init; }

    /// <summary>When set, replaces the full comments list.</summary>
    public IReadOnlyList<string>? Comments { get; init; }

    /// <summary>When set, appends one comment after any replace.</summary>
    public string? AppendComment { get; init; }
}

/// <summary>Seed/replace item (no SessionId; caller passes session separately).</summary>
public sealed class DysonSessionTodoReplaceItem
{
    public required string TaskCode { get; init; }
    public required string DisplayName { get; init; }
    public DysonSessionTodoStatus Status { get; init; } = DysonSessionTodoStatus.Pending;
    public IReadOnlyList<string>? Comments { get; init; }
}
