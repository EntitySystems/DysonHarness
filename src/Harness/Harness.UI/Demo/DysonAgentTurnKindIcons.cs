using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>wwwroot icon paths for <see cref="DysonAgentTurnKind"/> turn-header glyphs.</summary>
public static class DysonAgentTurnKindIcons
{
    public static string GetIconUrl(DysonAgentTurnKind kind) => kind switch
    {
        DysonAgentTurnKind.Normal => "icons/turns/normal.svg",
        DysonAgentTurnKind.InitializeSession => "icons/turns/initialize.svg",
        DysonAgentTurnKind.ExpandThoughtProcess => "icons/turns/expand.svg",
        DysonAgentTurnKind.Continuation => "icons/play.svg",
        DysonAgentTurnKind.TaskCompletionConfirm => "icons/turns/confirm.svg",
        DysonAgentTurnKind.ReportSummary => "icons/turns/report.svg",
        DysonAgentTurnKind.PlanResult => "icons/agent-modes/plan.svg",
        DysonAgentTurnKind.BeginBuildPlan => "icons/turns/build.svg",
        DysonAgentTurnKind.SubagentReportProcessing => "icons/turns/subagent.svg",
        DysonAgentTurnKind.ShellExited => "icons/turns/shell.svg",
        DysonAgentTurnKind.RethinkToolUsage => "icons/refresh.svg",
        DysonAgentTurnKind.DisplayInfo => "icons/turns/info.svg",
        DysonAgentTurnKind.ModeSwitch => "icons/turns/mode-switch.svg",
        DysonAgentTurnKind.DropContext => "icons/turns/drop-context.svg",
        DysonAgentTurnKind.TaskEndReflect => "icons/turns/expand.svg",
        DysonAgentTurnKind.BugReview => "icons/agent-modes/bug-review.svg",
        DysonAgentTurnKind.FullSummarize => "icons/turns/report.svg",
        _ => "icons/turns/normal.svg",
    };
}
