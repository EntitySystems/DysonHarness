namespace DysonHarness;

/// <summary>
/// Live context compaction: one inline <see cref="DysonAgentTurnKind.FullSummarize"/> turn that replaces the
/// history. Runs after a turn paused mid-work at a safe point (then queues a resuming
/// <see cref="DysonAgentTurnKind.Continuation"/>), after any turn that ended over the limit (no Continuation),
/// and before a prompt when an earlier trip is still pending (DB marker).
/// <see cref="DysonFullSummarizeFlow.ApplyAfterCompletion"/> excludes earlier turns, and a successful
/// summary then drops queued harness follow-ups (<see cref="DysonQueuedTurnAfterSummary"/>).
/// </summary>
public static class DysonCompactionFlow
{
    public const string PausedNote = """
        # Paused for context compaction

        Context passed the session limit mid-task. The harness is summarizing the session and will resume this task in a new turn.
        """;

    public const string SummaryInstruction = """
        Context compaction: the session passed its context limit, so the harness paused the previous turn mid-work. That task is unfinished. Your summary is the only context the resumed task will have.

        Also capture, under ## Status and ## Next:
        - The interrupted task's goal and how far it got.
        - Open tool state: running shells, subagents, pending waits, partially applied edits.
        - Files touched (created, edited, deleted) and their current state.
        - The exact next steps to resume.

        """ + DysonFullSummarizeFlow.Instruction;

    public const string ContinuationInstruction =
        "Resume the interrupted task from the summary above. Re-read any file you need before editing it.";

    /// <summary>Summarizer reason when the inline summary request is rejected for context length.</summary>
    public const string FallbackSummaryReason =
        "Context compaction. The session passed its context limit mid-task and this summary replaces the whole history. Capture the goal, progress, open tool state, files touched, and next steps.";

    /// <summary>
    /// Whether <paramref name="turn"/> may trip compaction: never for Explore or a FullSummarize turn.
    /// The Continuation queued by a previous compaction may trip only after it completed a tool round,
    /// so a summary that is itself over the limit is sent instead of looping compact, continue, compact
    /// with no progress.
    /// </summary>
    public static bool CanCompact(DysonAgentSession session, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        return !string.Equals(session.Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase)
               && turn.Kind != DysonAgentTurnKind.FullSummarize
               && (!IsCompactionContinuation(turn) || turn.ToolRoundCallCounts.Count > 0);
    }

    public static bool IsCompactionContinuation(DysonAgentTurn turn) =>
        turn.Kind == DysonAgentTurnKind.Continuation
        && string.Equals(turn.Instruction, ContinuationInstruction, StringComparison.Ordinal);

    public static DysonAgentTurn CreateSummaryTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.FullSummarize,
            Instruction = SummaryInstruction,
            StartedUtc = DateTime.UtcNow,
        };

    public static DysonAgentTurn CreateContinuationTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.Continuation,
            Instruction = ContinuationInstruction,
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>
    /// The one continuation a successful summary re-queues. <see cref="DysonAgentTurn.AfterSummaryResume"/>
    /// lets the same clear keep it while dropping every older resume.
    /// </summary>
    public static DysonAgentTurn CreateFreshContinuationTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.Continuation,
            Instruction = ContinuationInstruction,
            StartedUtc = DateTime.UtcNow,
            AfterSummaryResume = true,
        };

    public const string FailedTitle = "Context compaction failed";

    /// <summary>
    /// Runs the summary turn through <paramref name="runSummaryTurn"/> (provider-specific, tools not executed),
    /// excludes earlier turns, and clears the pending marker.
    /// <paramref name="endedTurn"/> is the turn that just finalized (null: before a prompt, for a trip that was
    /// not compacted yet). <paramref name="resume"/> is only for a turn paused mid-work with <see cref="PausedNote"/>.
    /// On success, queued harness follow-ups are dropped and one fresh Continuation is queued when anything was
    /// dropped or the turn was paused; parent injections, user prompts, and external events stay. On a failed
    /// summary nothing is excluded or queued, the summary turn is closed out of context, and the marker stays
    /// so the next prompt or activate retries.
    /// </summary>
    public static async Task<VoidResult<string>> RunAsync(
        DysonAgentSession session,
        DysonAgentTurn? endedTurn,
        bool resume,
        Func<DysonAgentTurn, CancellationToken, Task<VoidResult<string>>> runSummaryTurn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(runSummaryTurn);

        if (endedTurn is not null)
            session.RecordCompactionPending(endedTurn.Id);

        session.AppendLog(endedTurn is null
            ? "compaction: summarizing a pending trip before this prompt"
            : $"compaction: summarizing after turn {endedTurn.Id.ToString("N")[..8]} {(resume ? "paused" : "ended")}");
        var summary = CreateSummaryTurn();
        VoidResult<string> ran;
        try
        {
            ran = await runSummaryTurn(summary, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CloseFailedSummary(session, summary, "cancelled");
            throw;
        }

        if (ran.IsError)
        {
            CloseFailedSummary(session, summary, ran.Error);
            return new VoidResult<string>(
                $"{FailedTitle}: {ran.Error} Compaction stays pending and runs again before the next request.");
        }

        // Root hosts apply (and persist) after the prompt returns with FullSummarize last. Children
        // (KickOffChildPrompt has no host apply step) and a pre-prompt summary (not last) apply here
        // and persist through the hosts' TurnUpdated upsert.
        var appliedInline = session.Parent is not null || endedTurn is null;
        if (appliedInline)
        {
            foreach (var dropped in DysonFullSummarizeFlow.ApplyAfterCompletion(session, summary))
                session.RaiseTurnUpdated(dropped);
            session.RaiseTurnUpdated(summary);
        }

        session.ClearCompactionPending();
        // The fresh continuation is queued here for every session: it is what a paused turn resumes
        // with, and a root host drains it into the FIFO afterwards. The queue clear itself stays on the
        // apply path (children and a pre-prompt summary here; a root after the prompt returns), so a
        // failed summary, which returns above, never clears anything.
        if (resume || appliedInline)
            QueueFreshContinuationIfNeeded(session, resume);

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// After a successful summary: drop queued harness follow-ups, then queue one fresh Continuation
    /// when anything was dropped or the turn was paused mid-work. The old compaction Continuation is
    /// not kept. An empty queue that was not a pause queues nothing.
    /// </summary>
    public static void QueueFreshContinuationIfNeeded(
        DysonAgentSession session,
        bool resume,
        int alsoDropped = 0)
    {
        var dropped = DysonQueuedTurnAfterSummary.RetainPendingTurns(session) + alsoDropped;
        if (dropped == 0 && !resume)
            return;

        session.EnqueuePendingTurn(CreateFreshContinuationTurn());
        session.AppendLog("compaction: continuation queued");
    }

    /// <summary>A failed summary must not stay in context: its instruction would replay as a live request.</summary>
    private static void CloseFailedSummary(DysonAgentSession session, DysonAgentTurn summary, string error)
    {
        summary.AgentTitle = FailedTitle;
        summary.AssistantText =
            $"The summary request failed: {error}\n\nCompaction stays pending and runs again before the next request.";
        summary.IsExcludedFromContext = true;
        session.BumpTranscriptGeneration();
        session.AppendLog($"compaction: summary failed, still pending: {error}");
        session.RaiseTurnUpdated(summary);
    }

    /// <summary>
    /// Newest-first in-context turn bodies (oldest-first in the result) up to <paramref name="maxChars"/>,
    /// for the <see cref="DysonTurnSummarizer"/> fallback when the full summary request is too long.
    /// </summary>
    public static string FormatRecentHistory(DysonAgentSession session, Guid excludeTurnId, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(session);

        var parts = new List<string>();
        var used = 0;
        var turns = session.Turns;
        for (var i = turns.Count - 1; i >= 0 && used < maxChars; i--)
        {
            var turn = turns[i];
            if (turn.Id == excludeTurnId || turn.IsExcludedFromContext)
                continue;

            var body = DysonTurnSummarizer.HasSummary(turn)
                ? turn.ContextSummary!.Trim()
                : DysonTurnSummarizer.FormatTurnBody(turn);
            if (string.IsNullOrWhiteSpace(body))
                continue;

            var room = maxChars - used;
            if (body.Length > room)
                body = "…" + body[^room..];

            parts.Add($"[turn {turn.Id:D} · {turn.Kind}]\n{body}");
            used += body.Length;
        }

        parts.Reverse();
        return string.Join("\n\n---\n\n", parts);
    }
}
