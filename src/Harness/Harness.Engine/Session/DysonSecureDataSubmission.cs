namespace DysonHarness;

/// <summary>
/// Engine side of the RequestSecureData card submit. Values go to <c>session_secrets</c> only; the
/// outcome, the answer turn, and every error string carry keys, never values.
/// </summary>
public static class DysonSecureDataSubmission
{
    /// <summary>
    /// Locks the card, then upserts each non-blank value into the root Meta store. A failed upsert unlocks
    /// the card so the user can retry. Cancel stores nothing.
    /// </summary>
    public static async Task<Result<DysonSecureDataOutcome, string>> RecordAsync(
        DysonAgentSession session,
        Guid requestId,
        IReadOnlyDictionary<string, string>? values,
        bool cancelled,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var request = session.FindSecureDataRequest(requestId);
        if (request is null)
            return Result<DysonSecureDataOutcome, string>.AsError("RequestSecureData: not found.");
        if (request.Outcome is not null)
            return Result<DysonSecureDataOutcome, string>.AsError("RequestSecureData: already submitted.");

        var built = DysonSecureDataRequest.BuildOutcome(request, values, cancelled, DateTime.UtcNow);
        if (built.IsError)
            return built;
        var outcome = built.Value;

        var repo = session.Config.SessionSecrets;
        var root = session;
        while (root.Parent is not null)
            root = root.Parent;
        if (outcome.Stored.Count > 0
            && (repo is null
                || root.PersistenceId == Guid.Empty
                || !string.Equals(root.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase)))
            return Result<DysonSecureDataOutcome, string>.AsError("RequestSecureData: secure storage is not available.");

        var locked = session.TryRecordSecureDataOutcome(requestId, outcome);
        if (locked.IsError)
            return Result<DysonSecureDataOutcome, string>.AsError(locked.Error);

        foreach (var key in outcome.Stored)
        {
            var field = request.Fields.First(f => string.Equals(f.Key, key, StringComparison.Ordinal));
            var upserted = await repo!.UpsertAsync(root.PersistenceId, key, field.Description, values![key], ct)
                .ConfigureAwait(false);
            if (upserted.IsError)
            {
                session.TryClearSecureDataOutcome(requestId, outcome);
                return Result<DysonSecureDataOutcome, string>.AsError(upserted.Error);
            }
        }

        return Result<DysonSecureDataOutcome, string>.AsValue(outcome);
    }

    /// <summary>Normal turn that tells the model which keys were stored. No value can reach it.</summary>
    public static DysonAgentTurn CreateAnswerTurn(Guid requestId, DysonSecureDataOutcome outcome)
    {
        var turn = DysonAgentSession.CreateNormalTurn(DysonSecureDataRequest.FormatAnswerPrompt(outcome));
        turn.HiddenInstruction = DysonSecureDataRequest.FormatHiddenInstruction(requestId, outcome);
        return turn;
    }
}
