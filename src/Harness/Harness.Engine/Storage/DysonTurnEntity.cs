namespace DysonHarness;

public sealed class DysonTurnEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int Sequence { get; set; }
    public DysonAgentTurnKind Kind { get; set; }
    public string? AgentTitle { get; set; }
    /// <summary>Workspace-relative plan path for PlanResult turns.</summary>
    public string? PlanRelativePath { get; set; }
    public string? Instruction { get; set; }
    public string? AssistantText { get; set; }
    /// <summary>
    /// Denormalized join of Thought segments only (UI / reload / search; not replayed into transcripts).
    /// </summary>
    public string? ReasoningText { get; set; }
    /// <summary>
    /// Ordered Thought + InterimText JSON for thinking history (UI + DB only; omitted from transcripts).
    /// </summary>
    public string? ReasoningLogJson { get; set; }
    public string ToolStateJson { get; set; } = "{}";
    public bool ToolHistoryOptimized { get; set; }
    public string? CompactToolHistory { get; set; }
    /// <summary>When true, omitted from provider transcripts (UI may still show + restore).</summary>
    public bool IsExcludedFromContext { get; set; }
    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }
    /// <summary>UTC.</summary>
    public DateTime? CompletedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}
