namespace DysonHarness;

public enum DysonAgentInterruptKind
{
    SubagentCompleted = 1,
    SubagentStopped = 2,
    SubagentFailed = 3,
}

public sealed class DysonAgentInterrupt
{
    public required DysonAgentInterruptKind Kind { get; init; }
    public required int SubagentId { get; init; }
    /// <summary>Child session durable id when known (for host registry / auto-turn).</summary>
    public Guid? PersistenceId { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
