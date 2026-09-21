namespace DysonHarness;

public enum DysonPlanKind
{
    ClassicPlan = 0,
    MetaPlan = 1,
}

public enum DysonPlanStatus
{
    Draft = 0,
    Building = 1,
    Completed = 2,
    Stale = 3,
}

/// <summary>
/// EF row for table <c>plans</c>.
/// Every row written today is <see cref="DysonPlanKind.MetaPlan"/>.
/// <see cref="DysonPlanKind.ClassicPlan"/> (<c>0</c>) is reserved for a future
/// backfill of <c>.dyson/plans/*.md</c>; nothing in this feature writes a
/// ClassicPlan row, so missing <c>Kind == 0</c> rows are expected, not a bug.
/// </summary>
public sealed class DysonPlanEntity
{
    /// <summary>Auto-increment PK; the <c>planId</c> every tool uses. Assigned on save.</summary>
    public long Id { get; set; }

    public Guid WorkDirectoryId { get; set; }

    public DysonPlanKind Kind { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Plan body. Required for <see cref="DysonPlanKind.MetaPlan"/>; null for a future file-backed ClassicPlan.</summary>
    public string? Markdown { get; set; }

    /// <summary>
    /// Nullable, forward slashes. Null for <see cref="DysonPlanKind.MetaPlan"/>;
    /// the file pointer for a future ClassicPlan.
    /// </summary>
    public string? PlanRelativePath { get; set; }

    public DysonPlanStatus Status { get; set; }

    /// <summary>Last status note.</summary>
    public string? Note { get; set; }

    /// <summary>Session id of the drone building this plan, if any.</summary>
    public Guid? BuildAgentId { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; set; }

    public DysonWorkDirectoryEntity? WorkDirectory { get; set; }
}

/// <summary>Runtime / UI / MCP mirror of a plans row (non-EF).</summary>
public sealed class DysonPlan
{
    public long Id { get; init; }
    public Guid WorkDirectoryId { get; init; }
    public DysonPlanKind Kind { get; init; }
    public required string Title { get; init; }

    /// <summary>
    /// Plan body. Loaded by <see cref="IDysonPlanRepository.GetAsync"/>;
    /// always null from <see cref="IDysonPlanRepository.ListAsync"/>.
    /// </summary>
    public string? Markdown { get; init; }

    public string? PlanRelativePath { get; init; }
    public DysonPlanStatus Status { get; init; }
    public string? Note { get; init; }
    public Guid? BuildAgentId { get; init; }

    /// <summary>UTC.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedUtc { get; init; }
}
