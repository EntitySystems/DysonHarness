namespace DysonHarness;

/// <summary>
/// ponytail: assert-only self-check for soft spawn gates (no test framework).
/// Run: <c>DysonSubagentSpawnGateSelfCheck.Run()</c> from a scratch console if needed.
/// </summary>
public static class DysonSubagentSpawnGateSelfCheck
{
    public static void Run()
    {
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Work, DysonAgentModes.Explore));
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Work, DysonAgentModes.Drone));
        AssertOk(DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Drone, DysonAgentModes.Explore));

        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Explore, DysonAgentModes.Drone),
            "Explore");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Work, DysonAgentModes.Plan),
            "Plan");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Drone, DysonAgentModes.Drone),
            "Drone");
        AssertErr(
            DysonAgentSession.ValidateSubagentSpawn(DysonAgentModes.Drone, DysonAgentModes.Ask),
            "Explore");
    }

    private static void AssertOk(VoidResult<string> result)
    {
        if (result.IsError)
            throw new InvalidOperationException($"Expected ok, got: {result.Error}");
    }

    private static void AssertErr(VoidResult<string> result, string mustContain)
    {
        if (!result.IsError)
            throw new InvalidOperationException("Expected error.");
        if (result.Error.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException($"Expected error containing '{mustContain}', got: {result.Error}");
    }
}
