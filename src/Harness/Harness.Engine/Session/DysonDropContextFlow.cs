namespace DysonHarness;

/// <summary>
/// Factory for DropContext turns: prune irrelevant older turns when outgoing context
/// exceeds the session max target, then continue the original prompt.
/// </summary>
public static class DysonDropContextFlow
{
    public const int KeepRecentTurns = 4;

    /// <summary>
    /// Minimum <see cref="DysonAgentTurnKind.Normal"/> / <see cref="DysonAgentTurnKind.InitializeSession"/>
    /// turns after the latest DropContext before another inject is allowed.
    /// First inject (no prior DropContext) is not throttled.
    /// </summary>
    public const int MinUserTurnsBetweenInject = 5;

    public const string Instruction = """
        Context is over the session max target. Shrink it by dropping older turns with DropTurnContext.

        Rules:
        - Each prior turn in history is labeled with its turn id (see turn header in the transcript).
          Use those ids with DropTurnContext (requires turnIds, reason, and resume).
        - Only consider turns older than the last 4 (do not drop the most recent four turns).
        - Drop at least a few older turns when several are large and low-value for the current task;
          prefer the large ones over small turns that still matter. If fewer qualify, drop those and say so.
        - resume is required and must stay under 1000 tokens. Write it yourself to cover the whole batch:
          name each dropped turn id and what it held (facts, decisions, paths, constraints still worth knowing).
          An empty or over-cap resume fails the whole call and drops nothing.
        - The harness appends that resume to this turn's reply, prefixed with the dropped turn ids.
          Later transcripts keep that reply; the dropped turns themselves are omitted.
        - RestoreTurnContext brings a dropped turn back and does not remove this reply.
        - After dropping, briefly note what you did and stop; the harness will resume the original user prompt next.
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
    /// Evaluates whether <see cref="PromptWithTurnAsync"/> should prepend a DropContext turn.
    /// <see cref="SkipReason"/> is set when not injecting (including under-limit / Off).
    /// </summary>
    public static DropContextInjectDecision EvaluateInject(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var estimated = session.EstimateOutgoingContextTokens();
        var max = session.ResolveEffectiveMaxTargetContextTokens();

        if (max <= 0)
            return new DropContextInjectDecision(false, estimated, max, "off");

        if (session.IsInDropContextPhase)
            return new DropContextInjectDecision(false, estimated, max, "in-phase");

        if (!HasDroppableOlderTurn(session.Turns))
            return new DropContextInjectDecision(false, estimated, max, "no-droppable-older");

        if (!PassesThrottle(session.Turns))
            return new DropContextInjectDecision(false, estimated, max, "throttle");

        if (estimated <= max)
            return new DropContextInjectDecision(false, estimated, max, null);

        return new DropContextInjectDecision(true, estimated, max, null);
    }

    /// <summary>
    /// True when estimated outgoing tokens exceed the effective max, DropContext is not already
    /// in flight, droppable older history exists, and the 5-user-turn throttle allows inject.
    /// Effective max of 0 (Off) never injects.
    /// </summary>
    public static bool ShouldInjectDropContext(DysonAgentSession session) =>
        EvaluateInject(session).ShouldInject;

    /// <summary>
    /// Evaluates inject, appends a session log line for inject or for skip while over max,
    /// and returns whether the caller should run the DropContext turn.
    /// </summary>
    public static bool TryBeginInject(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var decision = EvaluateInject(session);
        AppendDecisionLog(session, decision);
        return decision.ShouldInject;
    }

    /// <summary>
    /// Logs inject, or skip while over a positive max with reason
    /// (<c>in-phase</c>, <c>no-droppable-older</c>, <c>throttle</c>).
    /// Silent when under max or Off (unlimited). <c>off</c> remains an
    /// <see cref="EvaluateInject"/> skip reason but is not session-logged each Send.
    /// </summary>
    public static void AppendDecisionLog(DysonAgentSession session, in DropContextInjectDecision decision)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (decision.ShouldInject)
        {
            session.AppendLog(
                $"drop-context: inject (estimated={decision.Estimated} max={decision.Max})");
            return;
        }

        // Positive max only: Off (max <= 0) is unlimited — no skip spam on every Send.
        if (decision.SkipReason is null
            || decision.Max <= 0
            || decision.Estimated <= decision.Max)
        {
            return;
        }

        session.AppendLog(
            $"drop-context: skip ({decision.SkipReason}; estimated={decision.Estimated} max={decision.Max})");
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
            if (!turn.IsExcludedFromContext
                && turn.Kind is not (DysonAgentTurnKind.DisplayInfo or DysonAgentTurnKind.WorktreeCreating))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Counts Normal/InitializeSession turns after the latest DropContext.
    /// Returns <see cref="int.MaxValue"/> when there is no prior DropContext (first inject allowed).
    /// </summary>
    public static int CountUserTurnsSinceLatestDropContext(IReadOnlyList<DysonAgentTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var lastDrop = -1;
        for (var i = 0; i < turns.Count; i++)
        {
            if (turns[i].Kind == DysonAgentTurnKind.DropContext)
                lastDrop = i;
        }

        if (lastDrop < 0)
            return int.MaxValue;

        var count = 0;
        for (var i = lastDrop + 1; i < turns.Count; i++)
        {
            if (IsUserTurnKind(turns[i].Kind))
                count++;
        }

        return count;
    }

    private static bool PassesThrottle(IReadOnlyList<DysonAgentTurn> turns) =>
        CountUserTurnsSinceLatestDropContext(turns) >= MinUserTurnsBetweenInject;

    private static bool IsUserTurnKind(DysonAgentTurnKind kind) =>
        kind is DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession;
}

/// <summary>Result of <see cref="DysonDropContextFlow.EvaluateInject"/>.</summary>
/// <param name="ShouldInject">True when a DropContext turn should be prepended.</param>
/// <param name="Estimated">Current estimated outgoing context tokens.</param>
/// <param name="Max">Effective max target (0 = Off).</param>
/// <param name="SkipReason">
/// When not injecting: <c>off</c>, <c>in-phase</c>, <c>no-droppable-older</c>, <c>throttle</c>,
/// or null when under a positive max.
/// </param>
public readonly record struct DropContextInjectDecision(
    bool ShouldInject,
    int Estimated,
    int Max,
    string? SkipReason);
