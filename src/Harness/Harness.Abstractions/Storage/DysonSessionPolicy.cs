namespace DysonHarness;

/// <summary>
/// Session rules shared by LocalDb and Engine. LocalDb does not reference Engine.
/// </summary>
public static class DysonSessionPolicy
{
    public const string MetaAgentMode = "Meta Agent";

    public const string CannotDeleteMessage = "Meta Agent sessions cannot be deleted.";

    public static bool IsMetaAgent(string? agentMode) =>
        string.Equals(agentMode, MetaAgentMode, StringComparison.OrdinalIgnoreCase);
}
