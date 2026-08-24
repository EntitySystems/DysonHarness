namespace DysonHarness;

/// <summary>
/// Deterministic process-restart recovery for durable unfinished work on the
/// current subject. Does not replay model or tool calls, invent assistant text,
/// or synthesize a parent subagent report. Safe to invoke repeatedly.
/// </summary>
public sealed class DysonSessionRecoveryService
{
    /// <summary>
    /// Synthetic incomplete-tool result written for Queued/Working calls.
    /// Distinct from the persisted <see cref="DysonTurnInterruptionReasons.ApplicationRestart"/> code.
    /// </summary>
    public const string IncompleteToolReason =
        "Interrupted by application restart; no model/tool call was replayed.";

    private readonly IDysonSessionRepository _sessions;

    public DysonSessionRecoveryService(IDysonSessionRepository sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>
    /// Finalizes unfinished turns on Active sessions for the current subject and
    /// marks Active descendants <see cref="DysonSessionStatus.Interrupted"/>.
    /// Roots stay <see cref="DysonSessionStatus.Active"/>.
    /// </summary>
    public async Task<Result<DysonSessionRecoveryReport, string>> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var listed = await _sessions
            .ListActiveSessionsWithUnfinishedTurnsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsError)
            return Result<DysonSessionRecoveryReport, string>.AsError(listed.Error);

        var unfinished = listed.Value;
        var turnsRepaired = 0;

        foreach (var summary in unfinished)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var recovered = await RecoverSessionTurnsAsync(summary, cancellationToken).ConfigureAwait(false);
            if (recovered.IsError)
                return Result<DysonSessionRecoveryReport, string>.AsError(recovered.Error);

            turnsRepaired += recovered.Value;
        }

        var descendantsListed = await _sessions
            .ListActiveDescendantSessionsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (descendantsListed.IsError)
            return Result<DysonSessionRecoveryReport, string>.AsError(descendantsListed.Error);

        var descendantsInterrupted = 0;
        foreach (var descendant in descendantsListed.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var interrupted = await InterruptActiveDescendantAsync(descendant.Id, cancellationToken)
                .ConfigureAwait(false);
            if (interrupted.IsError)
                return Result<DysonSessionRecoveryReport, string>.AsError(interrupted.Error);

            if (interrupted.Value)
                descendantsInterrupted++;
        }

        return Result<DysonSessionRecoveryReport, string>.AsValue(
            new DysonSessionRecoveryReport
            {
                UnfinishedSessions = unfinished.Count,
                TurnsRepaired = turnsRepaired,
                DescendantsInterrupted = descendantsInterrupted,
            });
    }

    private async Task<Result<int, string>> RecoverSessionTurnsAsync(
        DysonSessionUnfinishedWorkSummary summary,
        CancellationToken cancellationToken)
    {
        var loaded = await _sessions
            .GetFullSessionAsync(summary.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsError)
            return Result<int, string>.AsError(loaded.Error);

        var state = loaded.Value;
        var turnsRepaired = 0;

        foreach (var row in state.Turns.OrderBy(t => t.Sequence))
        {
            if (row.CompletedUtc is not null)
                continue;

            var repaired = RepairUnfinishedTurn(row);
            if (!HasTurnInterruptedLog(state.Logs, row.Id))
            {
                var log = DysonTurnPersistence.CreateTurnInterruptedLog(
                    state.Session.Id,
                    repaired,
                    DysonTurnInterruptionReasons.ApplicationRestart);
                var appended = await _sessions.AppendLogAsync(log, cancellationToken).ConfigureAwait(false);
                if (appended.IsError)
                    return Result<int, string>.AsError(appended.Error);
            }

            var entity = DysonTurnPersistence.ToEntity(
                repaired,
                state.Session.Id,
                row.Sequence,
                createdUtc: row.CreatedUtc,
                completedUtc: repaired.CompletedUtc);
            var upserted = await _sessions.UpsertTurnAsync(entity, cancellationToken).ConfigureAwait(false);
            if (upserted.IsError)
                return Result<int, string>.AsError(upserted.Error);

            turnsRepaired++;
        }

        return Result<int, string>.AsValue(turnsRepaired);
    }

    private async Task<Result<bool, string>> InterruptActiveDescendantAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var loaded = await _sessions
            .GetFullSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsError)
            return Result<bool, string>.AsError(loaded.Error);

        var state = loaded.Value;
        if (state.Session.ParentSessionId is null
            || state.Session.Status != DysonSessionStatus.Active)
        {
            return Result<bool, string>.AsValue(false);
        }

        if (!HasInterruptedStatusLog(state.Logs))
        {
            var statusLog = DysonSessionLogPayload.CreateEntry(
                state.Session.Id,
                DysonSessionLogKind.SessionStatusChanged,
                new DysonSessionLogSessionStatusChanged(
                    DysonSessionStatus.Interrupted,
                    DysonTurnInterruptionReasons.ApplicationRestart));
            var appended = await _sessions.AppendLogAsync(statusLog, cancellationToken).ConfigureAwait(false);
            if (appended.IsError)
                return Result<bool, string>.AsError(appended.Error);
        }

        var meta = await _sessions.UpdateSessionMetaAsync(
            new DysonSessionMetaUpdate
            {
                SessionId = state.Session.Id,
                Status = DysonSessionStatus.Interrupted,
            },
            cancellationToken).ConfigureAwait(false);
        if (meta.IsError)
            return Result<bool, string>.AsError(meta.Error);

        return Result<bool, string>.AsValue(true);
    }

    private static DysonAgentTurn RepairUnfinishedTurn(DysonTurnEntity row)
    {
        var turn = RehydrateTurn(row);
        turn.FinalizeIncompleteTools(IncompleteToolReason);
        turn.InterruptionReason = DysonTurnInterruptionReasons.ApplicationRestart;
        turn.CompletedUtc = DateTime.UtcNow;
        return turn;
    }

    private static DysonAgentTurn RehydrateTurn(DysonTurnEntity row)
    {
        var turn = new DysonAgentTurn
        {
            Id = row.Id,
            Kind = row.Kind,
            Instruction = row.Instruction,
            AgentTitle = row.AgentTitle,
            PlanRelativePath = row.PlanRelativePath,
            AssistantText = row.AssistantText,
            ToolHistoryOptimized = row.ToolHistoryOptimized,
            CompactToolHistory = row.CompactToolHistory,
            IsExcludedFromContext = row.IsExcludedFromContext,
            ContextSummary = row.ContextSummary,
            InterruptionReason = row.InterruptionReason,
            StartedUtc = row.CreatedUtc,
            CompletedUtc = row.CompletedUtc,
        };
        turn.RestoreReasoningLog(
            DysonReasoningLogSerializer.DeserializeOrSynthesize(row.ReasoningLogJson, row.ReasoningText));
        turn.RestoreContextFiles(DysonContextFilesSerializer.Deserialize(row.SkillsUsedJson));
        turn.RestoreUserImages(DysonUserImagesSerializer.Deserialize(row.UserImagesJson));
        DysonTurnToolStateSerializer.ApplyToTurn(turn, row.ToolStateJson);
        return turn;
    }

    private static bool HasTurnInterruptedLog(IReadOnlyList<DysonSessionLogEntry> logs, Guid turnId)
    {
        foreach (var log in logs)
        {
            if (!DysonSessionLogPayload.TryParseKind(log.Kind, out var kind)
                || kind != DysonSessionLogKind.TurnInterrupted)
            {
                continue;
            }

            var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogTurnInterrupted>(log.PayloadJson);
            if (payload?.TurnId == turnId)
                return true;
        }

        return false;
    }

    private static bool HasInterruptedStatusLog(IReadOnlyList<DysonSessionLogEntry> logs)
    {
        foreach (var log in logs)
        {
            if (!DysonSessionLogPayload.TryParseKind(log.Kind, out var kind)
                || kind != DysonSessionLogKind.SessionStatusChanged)
            {
                continue;
            }

            var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogSessionStatusChanged>(log.PayloadJson);
            if (payload?.Status == DysonSessionStatus.Interrupted)
                return true;
        }

        return false;
    }
}

/// <summary>Counts from one <see cref="DysonSessionRecoveryService.RecoverAsync"/> sweep.</summary>
public sealed class DysonSessionRecoveryReport
{
    public int UnfinishedSessions { get; init; }
    public int TurnsRepaired { get; init; }
    public int DescendantsInterrupted { get; init; }
}
