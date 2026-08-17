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
        DysonAgentTurnKind.RethinkToolUsage => "Rethink tool usage",
        DysonAgentTurnKind.DisplayInfo => "Info",
        DysonAgentTurnKind.ModeSwitch => "Mode switch",
        DysonAgentTurnKind.DropContext => "Drop context",
        DysonAgentTurnKind.TaskEndReflect => "Task end reflection",
        DysonAgentTurnKind.BugReview => "Code review",
        _ => kind.ToString(),
    };
}
