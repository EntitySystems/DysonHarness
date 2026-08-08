namespace DysonHarness;

/// <summary>
/// Factory for ExpandThoughtProcess turns: reformulate the problem before continuing heavy work.
/// </summary>
public static class DysonExpandThoughtProcess
{
    public const string Instruction = """
        The system requires you to lay out a small plan for the agent to use as reference in the next turns.
        Express the localized problem, formulate an understanding, and give guidance for the next turns.
        Use this turn to reformulate the problem so you do not get confused as context grows.
        Stay concise and actionable. Do not call tools unless essential to clarify a factual gap or to drop truly irrelevant context; prefer writing the plan in your reply.

        Context hygiene (optional SummarizeTurns / DropTurnContext — also available on Normal turns):
        - Each prior turn in history is labeled with its turn id (see turn header in the transcript). Use those ids with SummarizeTurns or DropTurnContext (requires reason).
        - Prefer SummarizeTurns when earlier turns still have useful facts but are too verbose for later prompts.
        - If earlier turns in this session are entirely irrelevant to the current problem and would cause major confusion if kept in the model context, call DropTurnContext with those turn ids to exclude them from future provider transcripts.
        - Only drop turns that are true noise with no remaining purpose for this task (wrong rabbit hole, obsolete exploration, superseded dead end).
        - When in doubt, do not drop anything. Prefer summarize over drop; prefer keeping context over aggressive pruning.
        - RestoreTurnContext is available to undo a drop if you need those turns back; do not treat restore as required hygiene.
        - After summarizing/dropping (if any), write your reformulation assuming excluded turns will not appear in later prompts.
        """;

    /// <summary>
    /// Normal-turn prompt the host enqueues after a successful ExpandThoughtProcess so work
    /// continues without a manual user message.
    /// </summary>
    public const string ContinuationPrompt =
        "Continue from the expanded thought process above.";

    /// <summary>
    /// Whether the host should enqueue <see cref="ContinuationPrompt"/> after this turn
    /// completes successfully. True only for <see cref="DysonAgentTurnKind.ExpandThoughtProcess"/>.
    /// </summary>
    public static bool ShouldEnqueueContinuation(DysonAgentTurnKind kind) =>
        kind == DysonAgentTurnKind.ExpandThoughtProcess;

    /// <summary>
    /// Creates a turn with <see cref="DysonAgentTurnKind.ExpandThoughtProcess"/> and the standard instruction
    /// (plus optional focus appendix). No tool calls are pre-seeded.
    /// </summary>
    public static DysonAgentTurn CreateTurn(string? focus = null)
    {
        var instruction = string.IsNullOrWhiteSpace(focus)
            ? Instruction
            : $"{Instruction}\n\nFocus: {focus.Trim()}";

        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.ExpandThoughtProcess,
            Instruction = instruction,
            StartedUtc = DateTime.UtcNow,
        };
    }
}
