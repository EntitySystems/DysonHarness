using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>Card + auto-turn helpers for <see cref="DysonUiHost"/> (pure; self-checkable).</summary>
public static class DysonSubagentHostLogic
{
    public static bool IsRunning(DysonSessionStatus status, DysonAgentTurn? latestTurn) =>
        status == DysonSessionStatus.Active
        && (latestTurn is null || latestTurn.CompletedUtc is null);

    public static string BuildSubagentReportContinuationPrompt(DysonAgentInterrupt interrupt, string? title)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        var outcome = interrupt.Kind switch
        {
            DysonAgentInterruptKind.SubagentCompleted => "completed",
            DysonAgentInterruptKind.SubagentFailed => "failed",
            DysonAgentInterruptKind.SubagentStopped => "stopped",
            _ => interrupt.Kind.ToString(),
        };

        var titleLine = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title.Trim();
        var summary = string.IsNullOrWhiteSpace(interrupt.Summary)
            ? "(no summary)"
            : interrupt.Summary.Trim();

        var persistence = interrupt.PersistenceId is Guid pid && pid != Guid.Empty
            ? pid.ToString("D")
            : "(unknown)";

        return
            $"""
            Harness continuation: a subagent finished and submitted a report. Incorporate it and continue the parent task.

            - subagentId: {interrupt.SubagentId}
            - persistenceId: {persistence}
            - title: {titleLine}
            - outcome: {outcome}

            ## Report
            {summary}
            """;
    }

    /// <summary>ponytail: assert-based check for IsRunning + prompt shape; no test framework.</summary>
    public static void RunSelfCheck()
    {
        var activeNoTurns = IsRunning(DysonSessionStatus.Active, latestTurn: null);
        if (!activeNoTurns)
            throw new InvalidOperationException("Active with no turns should be running.");

        var inFlight = IsRunning(
            DysonSessionStatus.Active,
            new DysonAgentTurn { StartedUtc = DateTime.UtcNow, CompletedUtc = null });
        if (!inFlight)
            throw new InvalidOperationException("Active turn without CompletedUtc should be running.");

        var doneTurn = IsRunning(
            DysonSessionStatus.Active,
            new DysonAgentTurn { StartedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow });
        if (doneTurn)
            throw new InvalidOperationException("Active with completed latest turn should not be running.");

        if (IsRunning(DysonSessionStatus.Completed, latestTurn: null))
            throw new InvalidOperationException("Completed status should not be running.");

        var prompt = BuildSubagentReportContinuationPrompt(
            new DysonAgentInterrupt
            {
                Kind = DysonAgentInterruptKind.SubagentCompleted,
                SubagentId = 2,
                PersistenceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Summary = "Found 3 files.",
            },
            title: "Explore README");

        if (!prompt.Contains("subagentId: 2", StringComparison.Ordinal)
            || !prompt.Contains("outcome: completed", StringComparison.Ordinal)
            || !prompt.Contains("Found 3 files.", StringComparison.Ordinal)
            || !prompt.Contains("Explore README", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Continuation prompt missing expected fields.");
        }
    }
}

/// <summary>Live snapshot for parent <c>SubagentCard</c> UI.</summary>
public sealed class DysonSubagentCardState
{
    public required Guid PersistenceId { get; init; }
    public string? Title { get; init; }
    public string? LatestTurnAgentTitle { get; init; }
    public bool IsRunning { get; init; }
    public DysonSessionStatus Status { get; init; }
}
