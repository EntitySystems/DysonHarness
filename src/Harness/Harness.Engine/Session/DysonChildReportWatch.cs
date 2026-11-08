namespace DysonHarness;

/// <summary>
/// Pure, host-free decision for the child-report watch: nudge an idle child to
/// <c>SubmitSubagentReport</c> before synthesizing a failed report. No timers,
/// no polling, not gated to Meta Agent.
/// </summary>
/// <remarks>
/// <c>remindersSent</c> is per-process (in-memory on the session). After a
/// restart, recovery marks Active descendants <see cref="DysonSessionStatus.Interrupted"/>
/// and synthesizes immediately, so a persisted counter would never be read.
/// </remarks>
public static class DysonChildReportWatch
{
    public const int MaxReminders = 2;

    public const string ReminderInstruction = """
        Your turn ended without submitting a report, and nothing is scheduled. If the task is unfinished, continue it now. If it is finished, call SubmitSubagentReport with what you did and how you verified it. If you are blocked, call SubmitSubagentReport with status failed and the exact decision you need.
        """;

    public const string IdleWithoutReportReason =
        "Harness: child session ended without SubmitSubagentReport after 2 reminders.";

    /// <summary>
    /// Provider-loop failure when a child turn ends without a successful
    /// <c>SubmitSubagentReport</c>. A harness condition: it becomes a synthetic failed
    /// report (roster), never a host or runtime error banner.
    /// </summary>
    public const string PromptEndedWithoutReportReason =
        "Child PromptAsync ended without SubmitSubagentReport.";

    /// <summary>
    /// True for a missing-report condition the harness already turns into a synthetic
    /// failed report. Hosts and the runtime must not copy these onto a UI error banner.
    /// </summary>
    public static bool IsHarnessNoReportError(string? error) =>
        !string.IsNullOrWhiteSpace(error)
        && (string.Equals(error.Trim(), PromptEndedWithoutReportReason, StringComparison.Ordinal)
            || string.Equals(error.Trim(), IdleWithoutReportReason, StringComparison.Ordinal)
            || string.Equals(
                error.Trim(),
                "Child finished without SubmitSubagentReport (no assistant output).",
                StringComparison.Ordinal));

    public const string ApplicationRestartReason = "application restart";

    public static DysonChildReportAction Evaluate(
        bool isChild,
        DysonSessionStatus status,
        bool hasReported,
        bool hasPendingWork,
        int remindersSent,
        bool hasActiveDescendant = false)
    {
        if (!isChild || hasReported || hasPendingWork)
            return DysonChildReportAction.None;

        if (status == DysonSessionStatus.Active)
        {
            // A worker is still running or its report has not been delivered: the report wakes this child.
            if (hasActiveDescendant)
                return DysonChildReportAction.Wait;

            return remindersSent < MaxReminders
                ? DysonChildReportAction.Remind
                : DysonChildReportAction.Synthesize;
        }

        if (status is DysonSessionStatus.Stopped
            or DysonSessionStatus.Interrupted
            or DysonSessionStatus.Failed)
        {
            return DysonChildReportAction.Synthesize;
        }

        return DysonChildReportAction.None;
    }

    public static DysonAgentTurn CreateReminderTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.ChildReportReminder,
            Instruction = ReminderInstruction,
            StartedUtc = DateTime.UtcNow,
        };
}

public enum DysonChildReportAction
{
    None = 0,
    Remind = 1,
    Synthesize = 2,
    /// <summary>Hold: a descendant is Active or its report is undelivered. No reminder, no synthesis.</summary>
    Wait = 3,
}
