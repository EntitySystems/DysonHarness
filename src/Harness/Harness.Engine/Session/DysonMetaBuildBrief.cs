namespace DysonHarness;

/// <summary>
/// Brief for a Meta Agent Drone spawned or messaged by <c>BeginBuildPlan</c>.
/// Names the <c>planId</c> and tells the drone to call <c>ReadMetaPlan</c>; never inlines the body.
/// Unrelated to <see cref="DysonBeginBuildPlanFlow"/> (Plan-mode layout-only turn).
/// </summary>
public static class DysonMetaBuildBrief
{
    public static string Build(long planId, string title, string? extraInstructions)
    {
        var heading = string.IsNullOrWhiteSpace(title)
            ? $"plan {planId}"
            : $"plan {planId} ({title.Trim()})";
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
