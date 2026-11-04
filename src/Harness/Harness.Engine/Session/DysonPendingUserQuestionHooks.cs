namespace DysonHarness;

/// <summary>
/// Pending-question index for both hosts. The card stays on the DisplayInfo turn;
/// this row is what the header and modal read. A failed insert does not hide the card.
/// </summary>
public static class DysonPendingUserQuestionHooks
{
    public static bool IsPendingQuestionTurn(DysonAgentTurn turn) =>
        turn.Kind == DysonAgentTurnKind.DisplayInfo
        && (turn.UserQuestion is { Answer: null } || turn.SecureDataRequest is { Outcome: null });

    /// <summary>
    /// Inserts a <see cref="DysonPendingUserQuestionKind.SecureData"/> row: title, bubble text, and
    /// <c>{key, description}</c> entries. No value exists yet and none is ever written here.
    /// </summary>
    public static async Task<Result<ulong, string>> InsertSecureDataAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        var repo = session.Config.PendingUserQuestions;
        if (repo is null)
            return Result<ulong, string>.AsError("Pending user questions are not configured.");
        if (session.PersistenceId == Guid.Empty)
            return Result<ulong, string>.AsError("Session is not persisted.");
        if (turn.Kind != DysonAgentTurnKind.DisplayInfo || turn.SecureDataRequest is not { Outcome: null } request)
            return Result<ulong, string>.AsError("Turn is not an open secure data request.");

        var created = await repo.CreateAsync(
            new DysonPendingUserQuestion(
                0,
                session.PersistenceId,
                turn.Id,
                request.Id,
                turn.AssistantText ?? DysonSecureDataRequest.PendingTitle,
                DysonSecureDataRequest.PendingTitle,
                request.Fields.Select(DysonSecureDataRequest.EncodePendingChoice).ToList(),
                MultiSelect: false,
                AllowCustomAnswer: false,
                SubagentId: null,
                EventId: null,
                DateTime.UtcNow,
                DysonPendingUserQuestionKind.SecureData),
            ct).ConfigureAwait(false);

        if (!created.IsError)
            session.Config.Bus?.Publish(
                DysonBusScopes.Session(session.PersistenceId),
                new DysonPendingUserQuestionsChangedEvent(session.PersistenceId));

        return created;
    }

    public static async Task<Result<ulong, string>> InsertAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        if (turn.SecureDataRequest is not null)
            return await InsertSecureDataAsync(session, turn, ct).ConfigureAwait(false);

        var repo = session.Config.PendingUserQuestions;
        if (repo is null)
            return Result<ulong, string>.AsError("Pending user questions are not configured.");
        if (session.PersistenceId == Guid.Empty)
            return Result<ulong, string>.AsError("Session is not persisted.");
        if (!IsPendingQuestionTurn(turn) || turn.UserQuestion is not { Answer: null } question)
            return Result<ulong, string>.AsError("Turn is not an unanswered question.");

        int? subagentId = null;
        Guid? eventId = null;
        DysonParentEvent? newest = null;
        foreach (var evt in session.PendingOrRecentParentEvents)
        {
            if (evt.Status != DysonParentEventStatus.Pending || !evt.AwaitingUserAnswer)
                continue;
            if (newest is null || evt.Timestamp > newest.Timestamp)
                newest = evt;
        }

        if (newest is not null)
        {
            subagentId = newest.SubagentId;
            eventId = newest.EventId;
        }

        var created = await repo.CreateAsync(
            new DysonPendingUserQuestion(
                0,
                session.PersistenceId,
                turn.Id,
                question.Id,
                turn.AssistantText ?? "",
                question.Question,
                question.Choices,
                question.MultiSelect,
                question.AllowCustomAnswer,
                subagentId,
                eventId,
                DateTime.UtcNow),
            ct).ConfigureAwait(false);

        if (!created.IsError)
            session.Config.Bus?.Publish(
                DysonBusScopes.Session(session.PersistenceId),
                new DysonPendingUserQuestionsChangedEvent(session.PersistenceId));

        return created;
    }

    /// <summary>
    /// Rewrites the pending question row and publishes <see cref="DysonPendingUserQuestionsChangedEvent"/>
    /// on success. A null repository or an unpersisted session is success and does not publish.
    /// A repository error is returned and does not publish.
    /// </summary>
    public static async Task<VoidResult<string>> UpdateAsync(
        DysonAgentSession session,
        Guid questionGuid,
        string message,
        string question,
        IReadOnlyList<string> choices,
        bool multiSelect,
        bool allowCustomAnswer,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var repo = session.Config.PendingUserQuestions;
        if (repo is null || session.PersistenceId == Guid.Empty)
            return VoidResult<string>.Success;

        var updated = await repo.UpdateByQuestionGuidAsync(
            questionGuid,
            session.PersistenceId,
            message,
            question,
            choices,
            multiSelect,
            allowCustomAnswer,
            ct).ConfigureAwait(false);
        if (updated.IsError)
            return updated;

        session.Config.Bus?.Publish(
            DysonBusScopes.Session(session.PersistenceId),
            new DysonPendingUserQuestionsChangedEvent(session.PersistenceId));
        return VoidResult<string>.Success;
    }

    public static async Task<ulong?> FindPendingIdAsync(
        DysonAgentSession session,
        Guid questionGuid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var repo = session.Config.PendingUserQuestions;
        if (repo is null || session.PersistenceId == Guid.Empty || questionGuid == Guid.Empty)
            return null;

        var listed = await repo.ListAsync(session.PersistenceId, ct).ConfigureAwait(false);
        if (listed.IsError)
            return null;

        foreach (var row in listed.Value)
        {
            if (row.QuestionGuid == questionGuid)
                return row.Id;
        }

        return null;
    }

    public static async Task<VoidResult<string>> DeleteAnsweredAsync(
        DysonAgentSession session,
        Guid questionGuid,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var repo = session.Config.PendingUserQuestions;
        if (repo is null)
            return VoidResult<string>.Success;

        var deleted = await repo.DeleteByQuestionGuidAsync(questionGuid, session.PersistenceId, ct)
            .ConfigureAwait(false);
        if (!deleted.IsError)
            session.Config.Bus?.Publish(
                DysonBusScopes.Session(session.PersistenceId),
                new DysonPendingUserQuestionsChangedEvent(session.PersistenceId));

        return deleted;
    }
}
