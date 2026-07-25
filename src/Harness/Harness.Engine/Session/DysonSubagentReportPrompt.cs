namespace DysonHarness;

/// <summary>
/// Shared formatting for subagent completion report blocks (host auto-turn + BeginBuildPlan).
/// </summary>
public static class DysonSubagentReportPrompt
{
    public static bool IsCompletionInterrupt(DysonAgentInterruptKind kind) =>
        kind is DysonAgentInterruptKind.SubagentCompleted
            or DysonAgentInterruptKind.SubagentFailed
            or DysonAgentInterruptKind.SubagentStopped;

    /// <summary>
    /// Plan mode buffers completion reports for BeginBuildPlan (or flush on mode leave);
    /// Work/Ask/etc. still drain immediately.
    /// </summary>
    public static bool ShouldDrainCompletionAutoTurn(string? parentMode) =>
        !string.Equals(parentMode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shared report shape: subagentId / persistenceId / title / outcome + bold Report body
    /// (no <c>##</c> heading — summaries can be long prose).
    /// </summary>
    public static string FormatReportBlock(DysonAgentInterrupt interrupt, string? title)
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
            - subagentId: {interrupt.SubagentId}
            - persistenceId: {persistence}
            - title: {titleLine}
            - outcome: {outcome}

            **Report**

            {summary}
            """;
    }

    public static string BuildContinuationPrompt(DysonAgentInterrupt interrupt, string? title)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        return
            $"""
            Harness continuation: a subagent finished and submitted a report. Incorporate it and continue the parent task.

            {FormatReportBlock(interrupt, title)}
            """;
    }
}
