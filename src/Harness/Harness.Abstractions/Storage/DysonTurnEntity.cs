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

    /// <summary>
    /// Context-file JSON for this turn (column name kept; includes <c>kind</c>).
    /// Injected into provider transcripts on restore.
    /// </summary>
    public string? SkillsUsedJson { get; set; }

    /// <summary>
    /// User-attached images this turn (JSON). Re-emitted in provider multimodal transcripts on restore.
    /// </summary>
    public string? UserImagesJson { get; set; }

    public string ToolStateJson { get; set; } = "{}";
    public bool ToolHistoryOptimized { get; set; }
    public string? CompactToolHistory { get; set; }

    /// <summary>When true, omitted from provider transcripts (UI may still show + restore).</summary>
    public bool IsExcludedFromContext { get; set; }

    /// <summary>
    /// Compact turn summary for provider transcripts (replaces full body when non-empty).
    /// </summary>
    public string? ContextSummary { get; set; }

    /// <summary>
    /// Nullable interruption/recovery reason (stable code such as
    /// <see cref="DysonTurnInterruptionReasons.ApplicationRestart"/>).
    /// Presentation-only; omitted from provider transcripts.
    /// </summary>
    public string? InterruptionReason { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime? CompletedUtc { get; set; }

    public DysonSessionEntity? Session { get; set; }
}
