using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: table-drive <see cref="DysonChildReportWatch.Evaluate"/> (each argument flipped).
/// </summary>
public class DysonChildReportWatchTests
{
    [Theory]
    [InlineData(false, DysonSessionStatus.Active, false, false, 0, DysonChildReportAction.None)]
    [InlineData(true, DysonSessionStatus.Active, true, false, 0, DysonChildReportAction.None)]
    [InlineData(true, DysonSessionStatus.Active, false, true, 0, DysonChildReportAction.None)]
    [InlineData(true, DysonSessionStatus.Active, false, false, 0, DysonChildReportAction.Remind)]
    [InlineData(true, DysonSessionStatus.Active, false, false, 1, DysonChildReportAction.Remind)]
    [InlineData(true, DysonSessionStatus.Active, false, false, 2, DysonChildReportAction.Synthesize)]
    [InlineData(true, DysonSessionStatus.Active, false, false, 3, DysonChildReportAction.Synthesize)]
    [InlineData(true, DysonSessionStatus.Stopped, false, false, 0, DysonChildReportAction.Synthesize)]
    [InlineData(true, DysonSessionStatus.Interrupted, false, false, 0, DysonChildReportAction.Synthesize)]
    [InlineData(true, DysonSessionStatus.Failed, false, false, 0, DysonChildReportAction.Synthesize)]
    [InlineData(true, DysonSessionStatus.Completed, false, false, 0, DysonChildReportAction.None)]
    [InlineData(true, DysonSessionStatus.Stopped, true, false, 0, DysonChildReportAction.None)]
    [InlineData(true, DysonSessionStatus.Stopped, false, true, 0, DysonChildReportAction.None)]
    [InlineData(false, DysonSessionStatus.Stopped, false, false, 0, DysonChildReportAction.None)]
    public void Evaluate_matrix(
        bool isChild,
        DysonSessionStatus status,
        bool hasReported,
        bool hasPendingWork,
        int remindersSent,
        DysonChildReportAction expected)
    {
        var actual = DysonChildReportWatch.Evaluate(
            isChild, status, hasReported, hasPendingWork, remindersSent);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Remind_stops_after_MaxReminders()
    {
        Assert.Equal(2, DysonChildReportWatch.MaxReminders);

        Assert.Equal(
            DysonChildReportAction.Remind,
            DysonChildReportWatch.Evaluate(true, DysonSessionStatus.Active, false, false, 0));
        Assert.Equal(
            DysonChildReportAction.Remind,
            DysonChildReportWatch.Evaluate(true, DysonSessionStatus.Active, false, false, 1));
        Assert.Equal(
            DysonChildReportAction.Synthesize,
            DysonChildReportWatch.Evaluate(
                true, DysonSessionStatus.Active, false, false, DysonChildReportWatch.MaxReminders));
    }

    [Fact]
    public void CreateReminderTurn_uses_ChildReportReminder_kind_and_instruction()
    {
        var turn = DysonChildReportWatch.CreateReminderTurn();
        Assert.Equal(DysonAgentTurnKind.ChildReportReminder, turn.Kind);
        Assert.Equal(DysonChildReportWatch.ReminderInstruction, turn.Instruction);
        Assert.Contains("SubmitSubagentReport", turn.Instruction, StringComparison.Ordinal);
    }
}
