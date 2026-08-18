using DysonHarness;

namespace Harness.Tests;

/// <summary>Every <see cref="DysonAgentTurnKind"/> has the expected UI label (Xunit).</summary>
public class DysonAgentTurnKindDisplayTests
{
    [Fact]
    public void GetDisplayName_MatchesExpectedLabels()
    {
        // Persisted ints are append-only. New kinds must continue after DropContext=13.
        AssertNumeric(DysonAgentTurnKind.Normal, 0);
        AssertNumeric(DysonAgentTurnKind.ExpandThoughtProcess, 1);
        AssertNumeric(DysonAgentTurnKind.TaskCompletionConfirm, 2);
        AssertNumeric(DysonAgentTurnKind.Continuation, 3);
        AssertNumeric(DysonAgentTurnKind.ReportSummary, 4);
        AssertNumeric(DysonAgentTurnKind.InitializeSession, 5);
        AssertNumeric(DysonAgentTurnKind.PlanResult, 6);
        AssertNumeric(DysonAgentTurnKind.BeginBuildPlan, 7);
        AssertNumeric(DysonAgentTurnKind.SubagentReportProcessing, 8);
        AssertNumeric(DysonAgentTurnKind.ShellExited, 9);
        AssertNumeric(DysonAgentTurnKind.RethinkToolUsage, 10);
        AssertNumeric(DysonAgentTurnKind.DisplayInfo, 11);
        AssertNumeric(DysonAgentTurnKind.ModeSwitch, 12);
        AssertNumeric(DysonAgentTurnKind.DropContext, 13);
        AssertNumeric(DysonAgentTurnKind.TaskEndReflect, 14);
        AssertNumeric(DysonAgentTurnKind.BugReview, 15);
        AssertNumeric(DysonAgentTurnKind.FullSummarize, 16);

        AssertLabel(DysonAgentTurnKind.Normal, "Turn");
        AssertLabel(DysonAgentTurnKind.ExpandThoughtProcess, "Expand thought");
        AssertLabel(DysonAgentTurnKind.TaskCompletionConfirm, "Completion confirmed");
        AssertLabel(DysonAgentTurnKind.Continuation, "Continuation");
        AssertLabel(DysonAgentTurnKind.ReportSummary, "Final report summary");
        AssertLabel(DysonAgentTurnKind.InitializeSession, "Initialize session");
        AssertLabel(DysonAgentTurnKind.PlanResult, "Plan result");
        AssertLabel(DysonAgentTurnKind.BeginBuildPlan, "Begin build plan");
        AssertLabel(DysonAgentTurnKind.SubagentReportProcessing, "Subagent report");
        AssertLabel(DysonAgentTurnKind.ShellExited, "Shell exited");
        AssertLabel(DysonAgentTurnKind.RethinkToolUsage, "Rethink tool usage");
        AssertLabel(DysonAgentTurnKind.DisplayInfo, "Info");
        AssertLabel(DysonAgentTurnKind.ModeSwitch, "Mode switch");
        AssertLabel(DysonAgentTurnKind.DropContext, "Drop context");
        AssertLabel(DysonAgentTurnKind.TaskEndReflect, "Task end reflection");
        AssertLabel(DysonAgentTurnKind.BugReview, "Code review");
        AssertLabel(DysonAgentTurnKind.FullSummarize, "Full summary");

        if ((int)DysonAgentTurnKind.DropContext != 13)
            throw new InvalidOperationException("DysonAgentTurnKind.DropContext must stay 13 (append-only).");

        // Append-only after DropContext=13: TaskEndReflect=14, BugReview=15, FullSummarize=16.

        foreach (var kind in Enum.GetValues<DysonAgentTurnKind>())
        {
            if (string.IsNullOrWhiteSpace(DysonAgentTurnKindDisplay.GetDisplayName(kind)))
                throw new InvalidOperationException($"Missing display name for {kind}.");
        }
    }

    [Fact]
    public void AllowEnqueue_is_false_only_for_task_end_reflect()
    {
        Assert.False(new DysonAgentTurn { Kind = DysonAgentTurnKind.TaskEndReflect }.AllowEnqueue);
        Assert.True(new DysonAgentTurn { Kind = DysonAgentTurnKind.Normal }.AllowEnqueue);
        Assert.True(new DysonAgentTurn { Kind = DysonAgentTurnKind.BugReview }.AllowEnqueue);

        Assert.False(DysonAgentTurnKindRules.AllowsEnqueue(DysonAgentTurnKind.TaskEndReflect));
        Assert.True(DysonAgentTurnKindRules.AllowsEnqueue(DysonAgentTurnKind.Normal));
        Assert.True(DysonAgentTurnKindRules.AllowsEnqueue(DysonAgentTurnKind.BugReview));
    }

    private static void AssertNumeric(DysonAgentTurnKind kind, int expected)
    {
        if ((int)kind != expected)
        {
            throw new InvalidOperationException(
                $"{kind} must remain persisted value {expected}; got {(int)kind}.");
        }
    }

    private static void AssertLabel(DysonAgentTurnKind kind, string expected)
    {
        var actual = DysonAgentTurnKindDisplay.GetDisplayName(kind);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Display name for {kind}: expected '{expected}', got '{actual}'.");
        }
    }
}
