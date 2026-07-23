namespace DysonHarness;

public sealed class DysonTurnEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int Sequence { get; set; }
    public DysonAgentTurnKind Kind { get; set; }
    public string? AgentTitle { get; set; }
    public string? Instruction { get; set; }
    public string? AssistantText { get; set; }
    public string ToolStateJson { get; set; } = "{}";
    public bool ToolHistoryOptimized { get; set; }
    public string? CompactToolHistory { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}
