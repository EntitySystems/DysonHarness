namespace DysonHarness;

/// <summary>
/// Which already-queued turns still run after a successful summary. One classifier for the session
/// pending queue and the host/runtime prompt FIFO. A failed summary never calls this.
/// </summary>
public static class DysonQueuedTurnAfterSummary
{
    /// <summary>
    /// Parent-inject prefix. <see cref="DysonAgentSession"/> builds every
    /// <c>TriggerSubagentEvent</c> / <c>MessageMetaAgentDrone</c> instruction with it, including a
    /// future timeout path that uses the same injected turn.
    /// </summary>
    public const string InjectedSubagentPromptPrefix =
        "Harness injection: the parent sent instructions via TriggerSubagentEvent.";

    /// <summary>
    /// Keep parent injections, composer user prompts, and external harness events. Drop every
    /// other harness follow-up. A <see cref="DysonAgentTurnKind.Normal"/> stays unless its text is a
    /// known harness continuation or a StartNewTurn follow-up. A compaction
    /// <see cref="DysonAgentTurnKind.Continuation"/> is dropped so the caller re-queues one fresh copy.
    /// </summary>
    public static bool KeepAfterSummary(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.Instruction?.StartsWith(InjectedSubagentPromptPrefix, StringComparison.Ordinal) == true)
            return true;

        // The continuation this summary just re-queued. An older compaction Continuation has no flag.
        if (turn.AfterSummaryResume)
            return true;

        return turn.Kind switch
        {
            DysonAgentTurnKind.SubagentReportProcessing
                or DysonAgentTurnKind.ShellExited
                or DysonAgentTurnKind.ParentEvent
                or DysonAgentTurnKind.InitializeSession
                or DysonAgentTurnKind.PlanResult
                or DysonAgentTurnKind.DisplayInfo
                or DysonAgentTurnKind.ModeSwitch
                or DysonAgentTurnKind.WorktreeCreating => true,
            DysonAgentTurnKind.Normal => !turn.FromStartNewTurn && !IsHarnessFollowUp(turn.Instruction),
            _ => false,
        };
    }

    /// <summary>
    /// Classify-and-rebuild the session pending queue in order. Returns how many turns were dropped.
    /// </summary>
    public static int RetainPendingTurns(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var kept = new List<DysonAgentTurn>();
        var dropped = 0;
        while (session.TryDequeuePendingTurn(out var queued))
        {
            if (KeepAfterSummary(queued))
                kept.Add(queued);
            else
                dropped++;
        }

        foreach (var queued in kept)
            session.EnqueuePendingTurn(queued);

        if (dropped > 0)
            session.AppendLog($"summary: dropped {dropped} queued harness turn(s)");

        return dropped;
    }

    /// <summary>A Normal turn the harness authored against the pre-summary transcript.</summary>
    private static bool IsHarnessFollowUp(string? instruction) =>
        instruction is not null
        && (instruction.StartsWith(DysonRethinkToolUsageFlow.ResumeInstruction, StringComparison.Ordinal)
            || string.Equals(instruction, DysonExpandThoughtProcess.ContinuationPrompt, StringComparison.Ordinal)
            || string.Equals(instruction, DysonBeginBuildPlanFlow.ContinuationPrompt, StringComparison.Ordinal));
}
