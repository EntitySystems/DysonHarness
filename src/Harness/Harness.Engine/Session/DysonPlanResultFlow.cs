namespace DysonHarness;

/// <summary>
/// Factories and instruction text for PlanResult harness turns (after <c>SubmitPlan</c>).
/// </summary>
public static class DysonPlanResultFlow
{
    /// <summary>
    /// First-line marker on the legacy Build-plan user prompt. Kept for sticky dismissal of
    /// old sessions — prefer <see cref="DysonAgentTurnKind.BeginBuildPlan"/> going forward
    /// (see <see cref="DysonPlanReadyUi.TryGetPending"/>).
    /// </summary>
    public const string BuildPlanMarker = "[BuildPlan]";

    /// <summary>
    /// Turn instruction mandate: single-file continuity for the published plan.
    /// Lives on the PlanResult turn (transcript history), not only in the system prompt.
    /// </summary>
    public static string BuildInstruction(string planRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planRelativePath);
        var path = planRelativePath.Trim().Replace('\\', '/');

        return $"""
            Plan published. Continuity mandate:
            - The active plan file is `{path}`.
            - Update that file via WriteFile (or equivalent) for all plan revisions.
            - Do not call SubmitPlan again / create another plan file unless the user explicitly asks for a new plan.
            """;
    }

    /// <summary>
    /// Legacy user prompt for Build plan (starts with <see cref="BuildPlanMarker"/>).
    /// New builds use <see cref="DysonBeginBuildPlanFlow"/> / <c>PromptBeginBuildPlanAsync</c>.
    /// </summary>
    public static string BuildPlanUserPrompt(string planRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planRelativePath);
        var path = planRelativePath.Trim().Replace('\\', '/');

        return $"""
            {BuildPlanMarker}
            Implement the plan at `{path}`.
            Read that plan file, then orchestrate implementation via one or more Drone subagents (Explore first only if context is still thin). Do not invent a new plan file.
            """;
    }

    /// <summary>
    /// Creates a completed harness PlanResult turn (no auto LLM call).
    /// Does not append to history — call <see cref="DysonAgentSession.AppendPlanResultTurn"/>.
    /// </summary>
    public static DysonAgentTurn CreateTurn(string planRelativePath, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planRelativePath);
        var path = planRelativePath.Trim().Replace('\\', '/');
        var titleText = string.IsNullOrWhiteSpace(title) ? "Plan" : title.Trim();
        var now = DateTime.UtcNow;

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.PlanResult,
            Instruction = BuildInstruction(path),
            AgentTitle = titleText,
            PlanRelativePath = path,
            AssistantText = $"Plan ready: `{path}`",
            StartedUtc = now,
            CompletedUtc = now,
        };
    }
}
