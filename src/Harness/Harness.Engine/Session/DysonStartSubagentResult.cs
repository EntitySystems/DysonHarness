namespace DysonHarness;

/// <summary>Return shape from <see cref="DysonAgentSession.CreateChildAsync"/> / StartSubagent tool.</summary>
public sealed class DysonStartSubagentResult
{
    public required int SubagentId { get; init; }
    public required Guid PersistenceId { get; init; }
    public required string AgentMode { get; init; }
    public required string Title { get; init; }

    /// <summary>Child model slug string when known (inherited or resolved from <c>modelSlug</c>).</summary>
    public string? ModelSlug { get; init; }

    /// <summary>Display label for the child model (e.g. <c>Alias · Provider / slug</c>).</summary>
    public string? ModelLabel { get; init; }
}
