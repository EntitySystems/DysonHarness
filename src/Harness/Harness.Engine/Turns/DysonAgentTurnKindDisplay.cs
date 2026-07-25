namespace DysonHarness;

/// <summary>Human-facing labels for <see cref="DysonAgentTurnKind"/> (UI titles, not debug).</summary>
public static class DysonAgentTurnKindDisplay
{
    public static string GetDisplayName(DysonAgentTurnKind kind) => kind switch
    {
        DysonAgentTurnKind.Normal => "Turn",
        DysonAgentTurnKind.ExpandThoughtProcess => "Expand thought",
        DysonAgentTurnKind.TaskCompletionConfirm => "Completion confirmed",
        DysonAgentTurnKind.Continuation => "Continuation",
        DysonAgentTurnKind.ReportSummary => "Final report summary",
        DysonAgentTurnKind.InitializeSession => "Initialize session",
        DysonAgentTurnKind.PlanResult => "Plan result",
        DysonAgentTurnKind.BeginBuildPlan => "Begin build plan",
        DysonAgentTurnKind.SubagentReportProcessing => "Subagent report",
        DysonAgentTurnKind.ShellExited => "Shell exited",
        _ => kind.ToString(),
    };

    /// <summary>
    /// ponytail: assert every <see cref="DysonAgentTurnKind"/> has the expected UI label
    /// (no test framework). Run from UI <c>Program</c> startup.
    /// </summary>
    public static void SelfCheck()
    {
        Assert(DysonAgentTurnKind.Normal, "Turn");
        Assert(DysonAgentTurnKind.ExpandThoughtProcess, "Expand thought");
        Assert(DysonAgentTurnKind.TaskCompletionConfirm, "Completion confirmed");
        Assert(DysonAgentTurnKind.Continuation, "Continuation");
        Assert(DysonAgentTurnKind.ReportSummary, "Final report summary");
        Assert(DysonAgentTurnKind.InitializeSession, "Initialize session");
        Assert(DysonAgentTurnKind.PlanResult, "Plan result");
        Assert(DysonAgentTurnKind.BeginBuildPlan, "Begin build plan");
        Assert(DysonAgentTurnKind.SubagentReportProcessing, "Subagent report");
        Assert(DysonAgentTurnKind.ShellExited, "Shell exited");

        foreach (var kind in Enum.GetValues<DysonAgentTurnKind>())
        {
            if (string.IsNullOrWhiteSpace(GetDisplayName(kind)))
                throw new InvalidOperationException($"Missing display name for {kind}.");
        }
    }

    private static void Assert(DysonAgentTurnKind kind, string expected)
    {
        var actual = GetDisplayName(kind);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Display name for {kind}: expected '{expected}', got '{actual}'.");
        }
    }
}
