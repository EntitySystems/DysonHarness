namespace DysonHarness;

public enum DysonAgentInterruptKind
{
    SubagentCompleted = 1,
    SubagentStopped = 2,
    SubagentFailed = 3,
    /// <summary>Child triggered a parent event (general or askQuestion); host shows UI / may auto-turn.</summary>
    SubagentEvent = 4,
}

public sealed class DysonAgentInterrupt
{
    public required DysonAgentInterruptKind Kind { get; init; }
    public required int SubagentId { get; init; }
    /// <summary>Child session durable id when known (for host registry / auto-turn).</summary>
    public Guid? PersistenceId { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Set when <see cref="Kind"/> is <see cref="DysonAgentInterruptKind.SubagentEvent"/>.</summary>
    public Guid? EventId { get; init; }

    /// <summary>Event kind string (e.g. askQuestion) when <see cref="Kind"/> is SubagentEvent.</summary>
    public string? EventKind { get; init; }

    /// <summary>Raw event payload when <see cref="Kind"/> is SubagentEvent.</summary>
    public string? Payload { get; init; }
}
