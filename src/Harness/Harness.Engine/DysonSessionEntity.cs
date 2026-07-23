namespace DysonHarness;

public enum DysonSessionStatus
{
    Active = 0,
    Completed = 1,
    Stopped = 2,
    Failed = 3,
}

public sealed class DysonSessionEntity
{
    public Guid Id { get; set; }
    public int RuntimeId { get; set; }
    public Guid? ParentSessionId { get; set; }
    public string AgentMode { get; set; } = "";
    public Guid? ModelSlugId { get; set; }
    public DysonMcpAccessMode McpAccessMode { get; set; }
    public DysonSessionStatus Status { get; set; }
    public string? Title { get; set; }
    public string SystemPromptSnapshot { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset LastActivityUtc { get; set; }

    public DysonSessionEntity? ParentSession { get; set; }
    public DysonModelSlugEntity? ModelSlug { get; set; }
    public List<DysonTurnEntity> Turns { get; set; } = [];
    public List<DysonSessionLogEntry> Logs { get; set; } = [];
}
