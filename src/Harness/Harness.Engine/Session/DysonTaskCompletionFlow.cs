namespace DysonHarness;

/// <summary>
/// Factories and instruction text for CompleteTask confirmation, continuation, and ReportSummary turns.
/// </summary>
/// <remarks>
/// Future loop wiring:
/// <list type="number">
/// <item><c>CompleteTask</c> → enqueue <see cref="DysonAgentTurnKind.TaskCompletionConfirm"/>.</item>
/// <item><c>ConfirmTaskComplete</c> → enqueue <see cref="DysonAgentTurnKind.ReportSummary"/> (last); after that reply, mark session task complete.</item>
/// <item><c>ContinueWork</c> → enqueue <see cref="DysonAgentTurnKind.Continuation"/> (then normal work until <c>CompleteTask</c> again).</item>
/// </list>
/// </remarks>
public static class DysonTaskCompletionFlow
{
    public const string ConfirmInstruction = """
        You previously called CompleteTask. Before the harness accepts completion, confirm carefully.
        Re-check: was the user request fully satisfied? Were required verifications run? Are residual blockers unresolved?
        - If truly complete, call ConfirmTaskComplete (optionally with a short rationale).
        - If anything remains, call ContinueWork with what is left; do not claim done.
        Prefer a single decisive tool call this turn.
        """;

    public const string ContinuationInstruction = """
        Continuation turn: prior completion was withdrawn. Resume the unfinished work described in the ContinueWork reason / remainingWork.
        Do not re-litigate completed steps. When finished again, call CompleteTask (you will be asked to confirm once more).
        """;

    public const string ReportSummaryInstruction = """
        Report summary turn (final): Briefly explain the work done so a parent agent has enough context to continue without re-deriving your steps.
        Cover: outcome, key files/changes, how you verified, and any residual risks or follow-ups.
        Stay concise and factual. Prefer writing the summary in your reply; avoid further tool calls unless essential to cite a path.
        """;

    /// <summary>System turn after CompleteTask: model must ConfirmTaskComplete or ContinueWork.</summary>
    public static DysonAgentTurn CreateCompletionConfirmTurn(string? completeTaskSummary = null)
    {
        var instruction = string.IsNullOrWhiteSpace(completeTaskSummary)
            ? ConfirmInstruction
            : $"{ConfirmInstruction}\n\nCompleteTask summary: {completeTaskSummary.Trim()}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.TaskCompletionConfirm,
            Instruction = instruction,
        };
    }

    /// <summary>Turn after ContinueWork: resume unfinished work.</summary>
    public static DysonAgentTurn CreateContinuationTurn(string? reason = null, string? remainingWork = null)
    {
        var instruction = ContinuationInstruction;
        if (!string.IsNullOrWhiteSpace(reason))
            instruction = $"{instruction}\n\nReason: {reason.Trim()}";
        if (!string.IsNullOrWhiteSpace(remainingWork))
            instruction = $"{instruction}\n\nRemaining work: {remainingWork.Trim()}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Continuation,
            Instruction = instruction,
        };
    }

    /// <summary>Final turn after ConfirmTaskComplete: brief parent-agent handoff summary.</summary>
    public static DysonAgentTurn CreateReportSummaryTurn(string? confirmRationale = null)
    {
        var instruction = string.IsNullOrWhiteSpace(confirmRationale)
            ? ReportSummaryInstruction
            : $"{ReportSummaryInstruction}\n\nConfirm rationale: {confirmRationale.Trim()}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.ReportSummary,
            Instruction = instruction,
        };
    }
}
