namespace DysonHarness;

/// <summary>
/// Factory for FullSummarize turns: one agent-authored session summary that replaces
/// earlier turns in later provider transcripts (in-memory drop after completion).
/// </summary>
public static class DysonFullSummarizeFlow
{
    public const int MaxSummaryCharacters = 6_000;

    public const string Instruction = """
        Write a full session summary that can replace every earlier turn in later provider transcripts.

        This turn is the last chance to read the complete history. When this turn finishes, the harness will exclude every previous turn from future model context. Your reply becomes the only remaining context. Do not assume the user will re-state the task.

        Hard limits:
        - Stay under 6,000 characters. The harness will hard-trim anything over that cap.
        - Do not call tools. Do not call CompleteTask, ExpandThoughtProcess, StartNewTurn, SummarizeTurns, DropTurnContext, or RestoreTurnContext.
        - Do not start subagents. Do not edit files. Do not run shells.
        - Start the reply with a single Markdown H1 title that names the thread (e.g. "# Session summary: …").

        What to capture (use only what is evidenced in this session):
        - Goal and current mode / constraints (work vs plan vs ask, user-stated non-goals).
        - Decisions and why they were chosen; rejected alternatives that still matter.
        - Concrete artifacts: paths, types, APIs, commands, plan files under `.dyson/plans/`, migration names, setting keys.
        - What is done vs unfinished (todos, failing tests, known bugs, follow-ups).
        - Residual risks and the next concrete action.

        What to omit:
        - Tool-call chatter, raw logs, stack traces, and repeated file dumps.
        - Speculation presented as fact. If unknown, say unknown.
        - Secrets, API keys, tokens, cookie values, or full credential blobs.

        Structure:
        # {short title}
        ## Goal
        ## Decisions
        ## Artifacts
        ## Status
        ## Next

        Write as a durable handoff for a new agent that has never seen this chat. Prefer specific names over vague recap. If the session is empty or has no useful work, say so in a few lines and stop.
        """;

    /// <summary>
    /// Creates a turn with <see cref="DysonAgentTurnKind.FullSummarize"/> and the standard instruction.
    /// Does not append to session history.
    /// </summary>
    public static DysonAgentTurn CreateTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.FullSummarize,
            Instruction = Instruction,
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>
    /// Whether the host persist path should run <see cref="ApplyAfterCompletion"/> after this turn
    /// finishes. True only for <see cref="DysonAgentTurnKind.FullSummarize"/>.
    /// </summary>
    public static bool ShouldApplyAfterCompletion(DysonAgentTurnKind kind) =>
        kind == DysonAgentTurnKind.FullSummarize;

    /// <summary>
    /// Hard-trims <paramref name="completedTurn"/> assistant text to
    /// <see cref="MaxSummaryCharacters"/> and excludes every earlier in-context turn.
    /// Does not exclude the FullSummarize turn itself. In-memory only (no persist).
    /// </summary>
    /// <returns>Turns newly marked <see cref="DysonAgentTurn.IsExcludedFromContext"/> (for persist wiring).</returns>
    public static IReadOnlyList<DysonAgentTurn> ApplyAfterCompletion(
        DysonAgentSession session,
        DysonAgentTurn completedTurn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(completedTurn);

        if (completedTurn.AssistantText is { Length: > MaxSummaryCharacters } text)
            completedTurn.AssistantText = text[..MaxSummaryCharacters];

        var bound = session.Turns.Count;
        for (var i = 0; i < session.Turns.Count; i++)
        {
            if (session.Turns[i].Id != completedTurn.Id)
                continue;
            bound = i;
            break;
        }

        List<DysonAgentTurn>? dropped = null;
        for (var i = 0; i < bound; i++)
        {
            var turn = session.Turns[i];
            if (turn.IsExcludedFromContext)
                continue;

            turn.IsExcludedFromContext = true;
            session.BumpTranscriptGeneration();
            session.AppendLog($"Turn {turn.Id:D} dropped, reason: Full summarize");
            dropped ??= [];
            dropped.Add(turn);
        }

        return dropped ?? [];
    }
}
