namespace DysonHarness;

/// <summary>
/// Pure, host-free Meta Agent maintenance tick: every
/// <see cref="IntervalTurns"/> completed turns, hard-delete older than the
/// newest <see cref="MaxTurns"/> and enqueue one <see cref="DysonAgentTurnKind.MetaMaintenance"/>
/// turn. Batching is load-bearing — evicting per turn past 40 would miss the
/// prompt-cache prefix on every subsequent turn.
/// </summary>
/// <remarks>
/// <see cref="DysonAgentSession.TurnsSinceMetaMaintenance"/> is in-memory
/// (like child-report reminders). A restart re-counts from zero, which at
/// worst delays one tick. A stored counter would add a column for no
/// behavioural gain.
/// </remarks>
public static class DysonMetaMaintenanceTick
{
    /// <summary>
    /// Floor after a tick, not a hard ceiling at all times. Between ticks the
    /// transcript can reach ~54 completed turns (40 + 15, minus in-flight).
    /// </summary>
    public const int MaxTurns = 40;

    public const int IntervalTurns = 15;

    public const int StaleAgentThreshold = 20;

    public const string Instruction = """
        Maintenance tick. Older turns may have been deleted permanently; ListMetaAgentDrones and ListPlans are the reliable rosters.

        Review the roster and plans, then prune what is no longer relevant:
        - DeleteMetaAgent on finished agents whose result is already in a todo or a posted message. Oldest first, until at most 20 finished agents remain.
        - DeletePlan when work is abandoned or the plan is superseded.
        - SetPlanStatus on any plan still building whose drone is already terminal (completed if the work is merged, stale if the plan no longer describes the work).
        - RemoveTodos for work that is no longer going to happen.

        Do not spawn new agents this turn unless a listed plan needs a follow-up drone. End the turn after pruning.
        """;

    public static bool AppliesTo(string agentMode) =>
        string.Equals(agentMode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase);

    public static bool ShouldTick(int completedTurnsSinceLastTick) =>
        completedTurnsSinceLastTick >= IntervalTurns;

    public static DysonAgentTurn CreateTurn(string? instruction = null) =>
        new()
        {
            Kind = DysonAgentTurnKind.MetaMaintenance,
            Instruction = instruction ?? Instruction,
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>
    /// Completed turns older than the newest <see cref="MaxTurns"/>. Never the
    /// in-flight turn, never an incomplete turn, never the newest
    /// <see cref="DysonAgentTurnKind.FullSummarize"/>, never an unprocessed
    /// <see cref="DysonAgentTurnKind.MetaMaintenance"/>.
    /// </summary>
    public static IReadOnlyList<DysonAgentTurn> SelectEvicted(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!AppliesTo(session.Mode))
            return [];

        var inFlight = session.InFlightPromptTurn;
        DysonAgentTurn? newestSummarize = null;
        for (var i = session.Turns.Count - 1; i >= 0; i--)
        {
            if (session.Turns[i].Kind != DysonAgentTurnKind.FullSummarize)
                continue;
            newestSummarize = session.Turns[i];
            break;
        }

        List<DysonAgentTurn>? completed = null;
        foreach (var turn in session.Turns)
        {
            if (turn.CompletedUtc is null)
                continue;
            if (inFlight is not null && ReferenceEquals(turn, inFlight))
                continue;

            completed ??= [];
            completed.Add(turn);
        }

        if (completed is null || completed.Count <= MaxTurns)
            return [];

        var excess = completed.Count - MaxTurns;
        List<DysonAgentTurn>? evicted = null;
        for (var i = 0; i < excess; i++)
        {
            var turn = completed[i];
            if (newestSummarize is not null && ReferenceEquals(turn, newestSummarize))
                continue;

            evicted ??= [];
            evicted.Add(turn);
        }

        return evicted ?? [];
    }

    public static void Evict(DysonAgentSession session, IReadOnlyList<DysonAgentTurn> evicted)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(evicted);
        if (evicted.Count == 0)
            return;

        session.RemoveTurnsFromHistory(evicted);
        foreach (var turn in evicted)
            session.AppendLog($"Turn {turn.Id:D} deleted, reason: meta maintenance");
    }

    public static IReadOnlyList<DysonMetaMaintenanceFinishedAgent> CollectFinishedAgents(
        DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        List<DysonMetaMaintenanceFinishedAgent>? finished = null;
        foreach (var child in session.SubSessions)
        {
            if (!child.IsTerminal)
                continue;

            finished ??= [];
            finished.Add(new DysonMetaMaintenanceFinishedAgent(
                child.Id,
                child.DisplayTitle,
                LatestCompletedUtc(child)));
        }

        if (finished is null)
            return [];

        finished.Sort(static (a, b) =>
        {
            var byTime = (a.FinishedAt ?? DateTime.MinValue).CompareTo(b.FinishedAt ?? DateTime.MinValue);
            return byTime != 0 ? byTime : a.AgentId.CompareTo(b.AgentId);
        });
        return finished;
    }

    /// <summary>
    /// Extra listing when finished agents exceed <see cref="StaleAgentThreshold"/>;
    /// null otherwise so the base instruction stays stable.
    /// </summary>
    public static string? BuildStaleAgentListing(
        IReadOnlyList<DysonMetaMaintenanceFinishedAgent> finishedAgents)
    {
        ArgumentNullException.ThrowIfNull(finishedAgents);
        if (finishedAgents.Count <= StaleAgentThreshold)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.Append("Finished agents over the ");
        sb.Append(StaleAgentThreshold);
        sb.Append(" keep limit (oldest first). Delete the ones you no longer need:");
        foreach (var agent in finishedAgents)
        {
            sb.AppendLine();
            sb.Append("- agentId=");
            sb.Append(agent.AgentId);
            if (!string.IsNullOrWhiteSpace(agent.Title))
            {
                sb.Append(" title=\"");
                sb.Append(agent.Title);
                sb.Append('"');
            }

            if (agent.FinishedAt is { } finishedAt)
            {
                sb.Append(" finishedAt=");
                sb.Append(finishedAt.ToUniversalTime().ToString("o"));
            }
        }

        return sb.ToString();
    }

    public static string BuildMaintenanceInstruction(
        IReadOnlyList<DysonMetaMaintenanceFinishedAgent> finishedAgents)
    {
        var listing = BuildStaleAgentListing(finishedAgents);
        return listing is null ? Instruction : Instruction + Environment.NewLine + Environment.NewLine + listing;
    }

    /// <summary>
    /// After a completed turn is persisted: increment the in-memory counter
    /// (except <see cref="DysonAgentTurnKind.MetaMaintenance"/>), and on the
    /// 15-turn boundary evict then enqueue one maintenance turn. No-op outside
    /// Meta Agent mode.
    /// </summary>
    public static async Task<VoidResult<string>> ApplyAfterCompletedTurnAsync(
        DysonAgentSession session,
        DysonAgentTurn completedTurn,
        IDysonSessionRepository? store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(completedTurn);
        cancellationToken.ThrowIfCancellationRequested();

        if (!AppliesTo(session.Mode))
            return VoidResult<string>.Success;

        if (completedTurn.Kind != DysonAgentTurnKind.MetaMaintenance)
            session.TurnsSinceMetaMaintenance++;

        if (!ShouldTick(session.TurnsSinceMetaMaintenance))
            return VoidResult<string>.Success;

        var evicted = SelectEvicted(session);
        if (evicted.Count > 0
            && store is not null
            && session.PersistenceId != Guid.Empty)
        {
            var ids = new Guid[evicted.Count];
            for (var i = 0; i < evicted.Count; i++)
                ids[i] = evicted[i].Id;

            var deleted = await store.DeleteTurnsAsync(session.PersistenceId, ids, cancellationToken)
                .ConfigureAwait(false);
            if (deleted.IsError)
                return deleted;
        }

        Evict(session, evicted);

        var instruction = BuildMaintenanceInstruction(CollectFinishedAgents(session));
        session.EnqueuePendingTurn(CreateTurn(instruction));
        session.TurnsSinceMetaMaintenance = 0;
        return VoidResult<string>.Success;
    }

    private static DateTime? LatestCompletedUtc(DysonAgentSession child)
    {
        DateTime? latest = null;
        foreach (var turn in child.Turns)
        {
            if (turn.CompletedUtc is { } completed && (latest is null || completed > latest))
                latest = completed;
        }

        return latest;
    }
}

public readonly record struct DysonMetaMaintenanceFinishedAgent(
    int AgentId,
    string? Title,
    DateTime? FinishedAt);
