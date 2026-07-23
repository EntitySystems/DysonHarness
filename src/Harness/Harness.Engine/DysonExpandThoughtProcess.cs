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
        Stay concise and actionable. Do not call tools unless essential to clarify a factual gap; prefer writing the plan in your reply.
        """;

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
        };
    }
}
