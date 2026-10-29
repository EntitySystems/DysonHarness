namespace DysonHarness;

/// <summary>
/// Result of <see cref="DysonAgentSession.ChangeSubagentModelAsync"/> / the <c>ChangeSubagentAIModel</c> tool.
/// <paramref name="Applied"/> is <c>now</c>, <c>nextToolRound</c> (child busy), or <c>unchanged</c>.
/// </summary>
public sealed record DysonChangeSubagentModelOutcome(
    int SubagentId,
    Guid PersistenceId,
    string ModelSlug,
    string? ReasoningEffort,
    string? ModelLabel,
    string Applied);
