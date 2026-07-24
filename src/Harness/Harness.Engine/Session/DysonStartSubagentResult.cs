namespace DysonHarness;

/// <summary>Return shape from <see cref="DysonAgentSession.CreateChildAsync"/> / StartSubagent tool.</summary>
public sealed class DysonStartSubagentResult
{
    public required int SubagentId { get; init; }
    public required Guid PersistenceId { get; init; }
    public required string AgentMode { get; init; }
    public required string Title { get; init; }
}
