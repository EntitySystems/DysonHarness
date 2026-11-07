namespace DysonHarness;

/// <summary>
/// Brief for a Meta Agent Drone spawned or messaged by <c>BeginBuildPlan</c>.
/// Names the <c>planId</c> and tells the drone to call <c>ReadMetaPlan</c>; never inlines the body.
/// Unrelated to <see cref="DysonBeginBuildPlanFlow"/> (Plan-mode layout-only turn).
/// </summary>
public static class DysonMetaBuildBrief
{
    /// <summary>
    /// The one todo a new <c>BeginBuildPlan</c> spawn seeds. The drone splits it into slices after ReadMetaPlan.
    /// </summary>
    public static DysonSessionTodoReplaceItem SeedTodo(long planId, string title) =>
        new()
        {
            TaskCode = $"build-plan-{planId}",
            DisplayName = $"Build {Heading(planId, title)}: ReadMetaPlan, split it into todos, implement, verify, commit",
        };

    private static string Heading(long planId, string title) =>
        string.IsNullOrWhiteSpace(title) ? $"plan {planId}" : $"plan {planId} ({title.Trim()})";

    public static string Build(long planId, string title, string? extraInstructions)
    {
        var heading = Heading(planId, title);
        var brief =
            $"Build {heading}. Call ReadMetaPlan with planId {planId} before you start — " +
            "it is the authoritative brief and it is kept current. Do not rely on this message for the plan body. " +
            "If the plan turns out wrong or outdated while you build, correct it in place with EditMetaPlan (small targeted edits). " +
            "You own the whole plan in one worktree and one merge: if it has several slices, run Drone workers as your Coordinating workers rules say. " +
            "Do not ask for more drones.";

        return string.IsNullOrWhiteSpace(extraInstructions)
            ? brief
            : brief + "\n\n" + extraInstructions.Trim();
    }
}
