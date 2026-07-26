using DysonHarness;

namespace Harness.Tests;

/// <summary>Every <see cref="DysonAgentTurnKind"/> has the expected UI label (Xunit).</summary>
public class DysonAgentTurnKindDisplayTests
{
    [Fact]
    public void GetDisplayName_MatchesExpectedLabels()
    {
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

        foreach (var kind in Enum.GetValues<DysonAgentTurnKind>())
        {
            if (string.IsNullOrWhiteSpace(DysonAgentTurnKindDisplay.GetDisplayName(kind)))
                throw new InvalidOperationException($"Missing display name for {kind}.");
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
