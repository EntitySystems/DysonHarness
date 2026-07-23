namespace DysonHarness;

/// <summary>
/// Runs staged tool calls on a <see cref="DysonAgentTurn"/>.
/// Same-stage calls run concurrently; ascending stage order is a barrier between groups;
/// the turn ends after the last stage completes.
/// </summary>
public static class DysonToolCallScheduler
{
    private static int _nextCallId;

    /// <summary>
    /// Executes all tool calls on <paramref name="turn"/> by <see cref="DysonToolCall.Stage"/>.
    /// Lower stages run first; calls with the same stage run concurrently; after a stage finishes,
    /// the next stage runs. Status transitions Queued → Working → Completed|Failed raise
    /// <see cref="DysonAgentTurn.ToolCallStatusChanged"/>; each result is enqueued onto
    /// <see cref="DysonAgentTurn.ResponseLog"/> as soon as that call finishes.
    /// </summary>
    public static async Task<VoidResult<string>> RunStagedAsync(
        DysonAgentTurn turn,
        Func<DysonToolCall, CancellationToken, Task<DysonToolCallResult>> execute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(execute);

        EnsureCallIds(turn);

        if (turn.ToolCalls.Count == 0)
            return VoidResult<string>.Success;

        if (turn.TrackedToolCalls.Count == 0)
            turn.PrepareTrackedCalls();

        var stages = turn.TrackedToolCalls
            .GroupBy(t => t.Call.Stage)
            .OrderBy(g => g.Key);

        foreach (var stageGroup in stages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trackedInStage = stageGroup.ToArray();
            foreach (var tracked in trackedInStage)
                tracked.SetWorking();

            var tasks = new Task[trackedInStage.Length];
            for (var i = 0; i < trackedInStage.Length; i++)
            {
                var tracked = trackedInStage[i];
                tasks[i] = ExecuteOneAsync(turn, tracked, execute, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        return VoidResult<string>.Success;
    }

    private static async Task ExecuteOneAsync(
        DysonAgentTurn turn,
        DysonTrackedToolCall tracked,
        Func<DysonToolCall, CancellationToken, Task<DysonToolCallResult>> execute,
        CancellationToken cancellationToken)
    {
        var result = await execute(tracked.Call, cancellationToken).ConfigureAwait(false);

        if (result.IsError)
            tracked.SetFailed(result);
        else
            tracked.SetCompleted(result);

        turn.ResponseLog.Enqueue(result);
    }

    private static void EnsureCallIds(DysonAgentTurn turn)
    {
        for (var i = 0; i < turn.ToolCalls.Count; i++)
        {
            var call = turn.ToolCalls[i];
            if (!string.IsNullOrEmpty(call.CallId))
                continue;

            turn.ToolCalls[i] = new DysonToolCall
            {
                CallId = AllocateCallId(),
                ToolName = call.ToolName,
                Stage = call.Stage,
                ArgumentsJson = call.ArgumentsJson,
            };
        }
    }

    private static string AllocateCallId() =>
        Interlocked.Increment(ref _nextCallId).ToString();
}
