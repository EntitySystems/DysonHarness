namespace DysonHarness;

/// <summary>
/// CI/CD status events to the live root Meta sessions (plan 380). The retained runtime owns the one
/// <see cref="DysonPipelineEventRelay"/> for the subject, so several circuits never double-deliver. Without a bus
/// (or in cloud mode, where no pipeline provider exists) nothing is ever leased or delivered.
/// </summary>
public sealed partial class DysonSessionRuntime
{
    private readonly object _pipelineRelayGate = new();
    private DysonPipelineEventRelay? _pipelineRelay;

    /// <summary>Re-syncs off the caller's thread: <see cref="RaiseChanged"/> can run on a Blazor circuit.</summary>
    private void SchedulePipelineRelaySync()
    {
        if (_bus is null || _disposed != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await SyncPipelineRelayAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Boundary: a background reconcile must never fault the session graph change that triggered it.
            }
        });
    }

    /// <summary>Makes the relay match the live root Meta sessions that have a work directory and a pipelines hook.</summary>
    internal Task SyncPipelineRelayAsync(CancellationToken ct = default)
    {
        if (_bus is null || _disposed != 0)
            return Task.CompletedTask;

        DysonPipelineEventRelay relay;
        lock (_pipelineRelayGate)
        {
            if (_disposed != 0)
                return Task.CompletedTask;
            relay = _pipelineRelay ??= new DysonPipelineEventRelay(_bus, DeliverPipelineBatchAsync);
        }

        var targets = new List<DysonPipelineRelayTarget>();
        foreach (var session in SnapshotLiveSessions())
        {
            if (session.Parent is not null
                || !DysonSessionPolicy.IsMetaAgent(session.Mode)
                || session.PersistenceId == Guid.Empty
                || session is not OpenAiCompatibleAgentSession { WorkDirectoryId: var workDirectoryId }
                || workDirectoryId == Guid.Empty)
            {
                continue;
            }

            targets.Add(new DysonPipelineRelayTarget(
                session.PersistenceId,
                workDirectoryId,
                token => session.Config.Pipelines is { } hook
                    ? hook(workDirectoryId, token)
                    : Task.FromResult(Result<DysonVersionControlService, string>.AsError("Pipelines are not available in this host"))));
        }

        return relay.SyncAsync(targets, ct);
    }

    /// <summary>
    /// Delivers one coalesced batch. Never interrupts: a running turn gets a queued comment (it drains after a
    /// <c>WaitForSubagent</c>, a tool call or the model step in progress); an idle session gets a Normal turn through
    /// the ordinary prompt queue. The drain is not awaited so a long Meta turn never holds up the next batch.
    /// </summary>
    internal Task<VoidResult<string>> DeliverPipelineBatchAsync(Guid sessionId, string text, CancellationToken ct)
    {
        _ = ct;
        if (_disposed != 0 || !_sessionsById.TryGetValue(sessionId, out var target))
            return Task.FromResult(VoidResult<string>.Success); // stale: the session went away

        // A summary or confirm turn leaves the model context, so a comment there would be lost: queue instead.
        var summarizing = target.IsSummaryInFlight || target.HasAnySummarizingTurn;
        // InFlightPromptTurns copies the stack under the model gate; Peek on the live stack can race a turn end.
        if (!summarizing && target.InFlightPromptTurns.FirstOrDefault() is { } inFlight && TryInjectPipelineComment(target, inFlight, text))
            return Task.FromResult(VoidResult<string>.Success);

        // Intake closed between the check and the inject, or nothing is running: queue a turn.
        // A batch still waiting (session stopped or busy) is superseded: the newer one carries the newer state.
        // ponytail: older queued batches are dropped, not merged; upgrade path = merge their lines into this one.
        foreach (var stale in GetQueuedPrompts(target.PersistenceId))
        {
            if (DysonInjectedUserComments.IsPipelineText(stale.Turn.Instruction))
                TryRemovePrompt(target.PersistenceId, stale.Id);
        }

        var turn = DysonAgentSession.CreateNormalTurn(
            DysonInjectedUserComments.FormatWithMarker(DysonInjectedUserComments.PipelineMarker, text));
        var queued = EnqueuePrompt(target.PersistenceId, turn);
        if (queued.IsError)
            return Task.FromResult(VoidResult<string>.AsError(queued.Error));

        _ = DrainPipelineTurnAsync(target);
        return Task.FromResult(VoidResult<string>.Success);
    }

    /// <summary>The turn may end between the stack copy and the inject (or the stack enumeration may race a push): fall back to queueing.</summary>
    private static bool TryInjectPipelineComment(DysonAgentSession target, DysonAgentTurn turn, string text)
    {
        try
        {
            return target.InjectTurnComment(turn.Id, text, DysonInjectedUserComments.PipelineMarker).IsSuccess;
        }
        catch (InvalidOperationException)
        {
            // Boundary: _inFlightPromptStack is enumerated without the model gate (push/pop race).
            return false;
        }
    }

    private async Task DrainPipelineTurnAsync(DysonAgentSession target)
    {
        try
        {
            var drained = await DrainRoutedAsync(target, toSidecar: false, CancellationToken.None).ConfigureAwait(false);
            if (drained.IsError
                && !IsPromptControl(drained)
                && !DysonChildReportWatch.IsHarnessNoReportError(drained.Error))
            {
                ReportError(drained.Error);
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
        }
    }

    private void DisposePipelineRelay()
    {
        DysonPipelineEventRelay? relay;
        lock (_pipelineRelayGate)
        {
            relay = _pipelineRelay;
            _pipelineRelay = null;
        }

        relay?.Dispose();
    }
}
