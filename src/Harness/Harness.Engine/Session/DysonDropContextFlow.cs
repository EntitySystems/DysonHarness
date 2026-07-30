namespace DysonHarness;

/// <summary>
/// Factory for DropContext turns: prune irrelevant older turns when outgoing context
/// exceeds the session max target, then continue the original prompt.
/// </summary>
public static class DysonDropContextFlow
{
    public const int KeepRecentTurns = 4;

    public const string Instruction = """
        Context is over the session max target. Review turns older than the last 4 and drop
        only true noise vs the current topic.

        Rules:
        - Each prior turn in history is labeled with its turn id (see turn header in the transcript).
          Use those ids with DropTurnContext (requires reason).
        - Only consider turns older than the last 4 (do not drop the most recent four turns).
        - Call DropTurnContext only for turns that are entirely irrelevant to the current problem
          and would cause major confusion if kept (wrong rabbit hole, obsolete exploration,
          superseded dead end).
        - If a turn still contains any useful facts, decisions, paths, or constraints — do not drop it.
        - When in doubt, keep the turn. Prefer keeping context over aggressive pruning.
        - RestoreTurnContext can undo a drop if you need those turns back.
        - After dropping (if any), briefly note what you dropped (or that nothing qualified) and stop;
          the harness will resume the original user prompt next.
        """;

    /// <summary>
    /// Creates a turn with <see cref="DysonAgentTurnKind.DropContext"/> and the standard instruction.
    /// Does not append to session history.
    /// </summary>
    public static DysonAgentTurn CreateTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.DropContext,
            Instruction = Instruction,
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>
    /// True when estimated outgoing tokens exceed the effective max, DropContext is not already
    /// in flight, and at least one non-excluded turn sits before the keep-recent window.
    /// Effective max of 0 (Off) never injects.
    /// </summary>
    public static bool ShouldInjectDropContext(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.IsInDropContextPhase)
            return false;

        var max = session.ResolveEffectiveMaxTargetContextTokens();
        if (max <= 0)
            return false;

        if (!HasDroppableOlderTurn(session.Turns))
            return false;

        var estimated = session.EstimateOutgoingContextTokens();
        return estimated > max;
    }

    /// <summary>
    /// True when history has a non-excluded turn at index &lt; Count − <see cref="KeepRecentTurns"/>.
    /// </summary>
    public static bool HasDroppableOlderTurn(IReadOnlyList<DysonAgentTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count <= KeepRecentTurns)
            return false;

        var cutoff = turns.Count - KeepRecentTurns;
        for (var i = 0; i < cutoff; i++)
        {
            var turn = turns[i];
            if (!turn.IsExcludedFromContext && turn.Kind != DysonAgentTurnKind.DisplayInfo)
                return true;
        }

        return false;
    }
}
