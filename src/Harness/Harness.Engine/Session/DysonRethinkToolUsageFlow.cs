namespace DysonHarness;

/// <summary>
/// Factories and instruction text for tool-round budget soft-pause rethink turns.
/// </summary>
/// <remarks>
/// Wired loop:
/// <list type="number">
/// <item>Tool loop hits session MaxToolRounds → soft-end current turn → enqueue
/// <see cref="DysonAgentTurnKind.RethinkToolUsage"/> (non-Explore only).</item>
/// <item><c>ResumeCurrentTask</c> → enqueue <see cref="DysonAgentTurnKind.Normal"/> with fresh budget.</item>
/// <item>Text-only reply on rethink (no resume) ends the pause without a continuation turn.</item>
/// <item>Explore budget hit → one no-tools recap reply (no <see cref="DysonAgentTurnKind.RethinkToolUsage"/>).</item>
/// </list>
/// </remarks>
public static class DysonRethinkToolUsageFlow
{
    public const string RethinkInstruction = """
        Rethink tool usage: the prior turn hit the tool-round budget without a final reply.
        Analyze recent tool calls for recursive / non-progressing patterns versus justified progress.
        This turn: use readonly tools only when a peek is needed (e.g. ReadFile, Grep, ListDirectory, LoadBinary, list/inspect helpers, readonly web/browser reads). Do not call writes, shells, or other mutating/work tools (aside from the Explore exception below).
        Explore exception: if the problem is complex and direct searching would consume a lot of context, you may StartSubagent an Explore this turn. If you spawn Explore, you must WaitForSubagent until completion this turn (no fire-and-forget; incorporate the report before resume vs stop). Do not spawn Drones/other modes on rethink; do not use Explore instead of ResumeCurrentTask when work should simply continue.
        - If continuing is justified, call ResumeCurrentTask with a brief rationale and/or continuationInstructions.
        - If stuck (doom loop, no useful next step), explain briefly in your reply and do not call ResumeCurrentTask.
        Prefer one decisive ResumeCurrentTask, Explore spawn+wait when justified, or a short concluding reply this turn.
        """;

    public const string ResumeInstruction = """
        Continuation after rethink: resume the unfinished work described below.
        Avoid repeating the recursive / non-progressing tool pattern that triggered the pause.
        Use a fresh tool-round budget for this turn.
        """;

    /// <summary>
    /// Harness follow-up when Explore hits its tool-round budget: one final no-tools reply.
    /// </summary>
    public const string ExploreBudgetRecapInstruction = """
        Harness: the Explore tool-round budget was hit. Recap your findings from tools already used this turn in a normal assistant reply. Explicitly note that results may be incomplete because the Explore tool-round budget was hit. Do not call tools — none are available on this reply.
        """;

    /// <summary>Fallback H1 note when the Explore budget-recap call returns empty content.</summary>
    public const string ExploreBudgetExhaustedFallback = """
        # Explore budget exhausted

        The Explore tool-round budget was hit. Findings from this turn may be incomplete.
        """;

    /// <summary>System turn after tool-round soft-pause: model must ResumeCurrentTask or stop with text.</summary>
    public static DysonAgentTurn CreateTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.RethinkToolUsage,
            Instruction = RethinkInstruction,
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>Normal turn after ResumeCurrentTask: continue work with a fresh tool-round budget.</summary>
    public static DysonAgentTurn CreateResumeTurn(
        string? rationale = null,
        string? continuationInstructions = null)
    {
        var instruction = ResumeInstruction;
        if (!string.IsNullOrWhiteSpace(rationale))
            instruction = $"{instruction}\n\nRationale: {rationale.Trim()}";
        if (!string.IsNullOrWhiteSpace(continuationInstructions))
            instruction = $"{instruction}\n\nContinuation instructions: {continuationInstructions.Trim()}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = instruction,
            StartedUtc = DateTime.UtcNow,
        };
    }
}
