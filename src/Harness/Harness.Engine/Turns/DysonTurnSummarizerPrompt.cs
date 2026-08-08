namespace DysonHarness;

/// <summary>
/// Editable constants for the turn-context summarizer (SummarizeTurns worker).
/// Summarizer system/user text never enters the main agent transcript.
/// </summary>
public static class DysonTurnSummarizerPrompt
{
    /// <summary>System instruction for the one-shot turn summarizer completion.</summary>
    public const string System = """
        You distill a prior agent-session turn for an AI coding agent that will keep only your summary in later prompts.

        Goals:
        - Keep concrete facts: decisions, file paths, APIs, commands, constraints, errors, and outcomes.
        - Drop chatter, repetition, and tool-call boilerplate that no longer matters.
        - Prefer short bullets. Stay well under 2000 tokens; never exceed that budget.
        - Do not invent facts. If the turn is empty or unusable, say so briefly.
        """;

    /// <summary>
    /// Builds the summarizer user message from a turn’s instruction / assistant / tool log.
    /// </summary>
    public static string FormatUserMessage(string turnBody, string? reason = null)
    {
        var body = turnBody ?? "";
        if (body.Length > MaxInputChars)
            body = body[..MaxInputChars] + "\n…[truncated for summarizer input]";

        var reasonBlock = string.IsNullOrWhiteSpace(reason)
            ? ""
            : $"""

                Summarize reason:
                {reason.Trim()}
                """;

        return $"""
            Distill this session turn into a compact context stub for later prompts.
            {reasonBlock}

            Turn content:
            {body}
            """;
    }

    /// <summary>Max characters of turn body fed into the summarizer (not the agent).</summary>
    public const int MaxInputChars = 80_000;
}
