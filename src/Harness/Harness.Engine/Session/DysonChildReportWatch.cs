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

    public const string ApplicationRestartReason = "application restart";

    public static DysonChildReportAction Evaluate(
        bool isChild,
        DysonSessionStatus status,
        bool hasReported,
        bool hasPendingWork,
        int remindersSent)
    {
        if (!isChild || hasReported || hasPendingWork)
            return DysonChildReportAction.None;

        if (status == DysonSessionStatus.Active)
        {
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
}
