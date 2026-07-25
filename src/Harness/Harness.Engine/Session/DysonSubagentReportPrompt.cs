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

    /// <summary>
    /// Harness mandate for a SubagentReportProcessing turn: analyze the report, write concrete
    /// continuation instructions, then proceed with parent work in this same turn (tools allowed).
    /// </summary>
    public static string BuildContinuationPrompt(DysonAgentInterrupt interrupt, string? title)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        // Chat renders Instruction as markdown: one # title, bold labels — like BeginBuildPlan.
        return
            $"""
            # Subagent report

            A subagent finished and submitted a report. This turn is for analyzing that report.

            - **Analyze** the attached report (outcome, findings, gaps).
            - **Write** concrete technical continuation instructions in your reply (what to do next, constraints, open questions).
            - **Then proceed** with parent work from those instructions this turn — tools allowed. Do not wait for another harness turn.

            {FormatReportBlock(interrupt, title)}
            """;
    }

    /// <summary>
    /// Creates a <see cref="DysonAgentTurnKind.SubagentReportProcessing"/> turn
    /// (LLM runs via <c>PromptSubagentReportProcessingAsync</c>).
    /// Does not append to history — the prompt entry point calls <see cref="DysonAgentSession"/> AddTurn.
    /// </summary>
    public static DysonAgentTurn CreateTurn(DysonAgentInterrupt interrupt, string? title)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        return CreateTurn(BuildContinuationPrompt(interrupt, title));
    }

    /// <summary>
    /// Creates a SubagentReportProcessing turn from a pre-built Instruction (prompt-queue drain).
    /// </summary>
    public static DysonAgentTurn CreateTurn(string instruction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.SubagentReportProcessing,
            Instruction = instruction.Trim(),
            StartedUtc = DateTime.UtcNow,
        };
    }
}
