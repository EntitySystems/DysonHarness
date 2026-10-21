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
        Stay concise and actionable. Do not call tools unless essential to clarify a factual gap or to drop low-value context; prefer writing the plan in your reply.

        Context hygiene (optional DropTurnContext — also available on Normal turns):
        - Each prior turn in history is labeled with its turn id (see turn header in the transcript). Use those ids with DropTurnContext (requires turnIds, reason, and resume).
        - When several earlier turns are large and serve little purpose for the current task, drop at least a few of them; prefer those over small turns that still matter.
        - resume is required and must stay under 1000 tokens: your own account of what the dropped turns included (name each turn id and what it held). An empty or over-cap resume fails the whole call and drops nothing.
        - The harness appends the resume to this turn's reply, prefixed with the dropped turn ids. Later transcripts keep that reply; the dropped turns themselves are omitted.
        - RestoreTurnContext brings a dropped turn back and does not remove that reply; do not treat restore as required hygiene.
        - After dropping (if any), write your reformulation assuming dropped turns are omitted and the resume stays on this turn's reply.
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
