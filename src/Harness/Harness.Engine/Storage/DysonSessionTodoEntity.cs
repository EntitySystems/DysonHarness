namespace DysonHarness;

public enum DysonSessionTodoStatus
{
    Pending = 0,
    Ongoing = 1,
    Complete = 2,
}

/// <summary>EF row for table <c>session_todos</c>.</summary>
public sealed class DysonSessionTodoEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string TaskCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DysonSessionTodoStatus Status { get; set; }
    /// <summary>JSON <c>string[]</c>; default <c>[]</c>.</summary>
    public string CommentsJson { get; set; } = "[]";
    public int Sequence { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}

/// <summary>Runtime / UI / MCP mirror of a session todo (non-EF).</summary>
public sealed class DysonSessionTodo
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public required string TaskCode { get; init; }
    public required string DisplayName { get; init; }
    public DysonSessionTodoStatus Status { get; init; }
    public IReadOnlyList<string> Comments { get; init; } = [];
    public int Sequence { get; init; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; init; }
    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; init; }
}
