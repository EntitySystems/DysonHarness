namespace DysonHarness;

/// <summary>Pending plan sticky payload for the composer Plan-ready popover.</summary>
public sealed record DysonPlanReadyInfo(string Path, string Title);

/// <summary>
/// Derives Plan-ready sticky visibility from turn history (no DB columns).
/// Visible after the latest <see cref="DysonAgentTurnKind.PlanResult"/> with a path until a later
/// <see cref="DysonAgentTurnKind.BeginBuildPlan"/> turn (or legacy <see cref="DysonPlanResultFlow.BuildPlanMarker"/> user prompt).
/// </summary>
public static class DysonPlanReadyUi
{
    public static DysonPlanReadyInfo? TryGetPending(IReadOnlyList<DysonAgentTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var latestPlanIndex = -1;
        string? path = null;
        string? title = null;

        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (turn.Kind != DysonAgentTurnKind.PlanResult
                || string.IsNullOrWhiteSpace(turn.PlanRelativePath))
            {
                continue;
            }

            latestPlanIndex = i;
            path = turn.PlanRelativePath.Trim().Replace('\\', '/');
            title = string.IsNullOrWhiteSpace(turn.AgentTitle) ? "Plan" : turn.AgentTitle.Trim();
        }

        if (latestPlanIndex < 0 || path is null || title is null)
            return null;

        for (var i = latestPlanIndex + 1; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (turn.Kind == DysonAgentTurnKind.BeginBuildPlan)
                return null;

            if (turn.Kind is not (DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession))
                continue;

            if (IsLegacyBuildPlanInstruction(turn.Instruction))
                return null;
        }

        return new DysonPlanReadyInfo(path, title);
    }

    private static bool IsLegacyBuildPlanInstruction(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            return false;

        var trimmed = instruction.TrimStart();
        return trimmed.StartsWith(DysonPlanResultFlow.BuildPlanMarker, StringComparison.Ordinal);
    }
}
