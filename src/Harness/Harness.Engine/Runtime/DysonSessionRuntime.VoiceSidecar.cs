using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DysonHarness;

public sealed partial class DysonSessionRuntime
{
    /// <summary>
    /// Host drain for a Meta session. Return true when the host accepted the drain.
    /// False (or null) falls through to the engine drain.
    /// </summary>
    public Func<Guid, bool>? MetaPromptDrainHook { get; set; }

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _voiceSidecarEnsureGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _voiceSidecarDrainGates = new();
    private readonly ConcurrentDictionary<Guid, Channel<DysonVoiceSidecarMessageEvent>> _voiceSidecarInboxes = new();

    // Per-utterance results keyed by turn id, so a caller that joins another drain gets its own turn's outcome.
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<VoidResult<string>>> _voiceTurnResults = new();

    // Meta subscription stays for the Meta id. The sidecar subscription is replaced when that sidecar's id changes.
    private readonly ConcurrentDictionary<Guid, (Guid SidecarId, List<IDisposable> Tokens)> _voiceSidecarSubscriptions = new();

    // Bumped on voice-off so an inbox reader that already dequeued a message does not deliver it.
    private int _voiceDeliveryGeneration;

    /// <summary>
    /// Returns the live sidecar for a root Meta session, loading the oldest persisted one or creating it.
    /// Single-flight per Meta persistence id.
    /// </summary>
    public async Task<Result<DysonAgentSession, string>> EnsureVoiceSidecarAsync(
        DysonAgentSession meta,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        if (_disposed != 0)
            return Result<DysonAgentSession, string>.AsError("Session runtime has been disposed.");
        if (meta.PersistenceId == Guid.Empty)
            return Result<DysonAgentSession, string>.AsError("Session is not persisted.");
        if (meta.Parent is not null || !DysonSessionPolicy.IsMetaAgent(meta.Mode))
            return Result<DysonAgentSession, string>.AsError("Voice sidecar is only for a root Meta session.");

        var gate = _voiceSidecarEnsureGates.GetOrAdd(meta.PersistenceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return Result<DysonAgentSession, string>.AsError("Session runtime has been disposed.");

            if (meta.VoiceSidecar is { PersistenceId: var liveId } live
                && liveId != Guid.Empty
                && _sessionsById.ContainsKey(liveId))
            {
                return Result<DysonAgentSession, string>.AsValue(live);
            }

            var listed = await Sessions.ListChildSessionsAsync(meta.PersistenceId, ct).ConfigureAwait(false);
            if (listed.IsError)
                return Result<DysonAgentSession, string>.AsError(listed.Error);

            var sidecars = listed.Value
                .Where(s => DysonSessionPolicy.IsVoiceSidecar(s.AgentMode))
                .OrderBy(s => s.CreatedUtc)
                .ToArray();

            DysonAgentSession sidecar;
            if (sidecars.Length > 0)
            {
                if (sidecars.Length > 1)
                {
                    meta.AppendLog(
                        "voice sidecar: keeping oldest, extra ids "
                        + string.Join(", ", sidecars.Skip(1).Select(s => s.Id)));
                }

                var loaded = await LoadSessionAsync(sidecars[0].Id, ct).ConfigureAwait(false);
                if (loaded.IsError)
                    return loaded;
                sidecar = loaded.Value;
                var model = await ApplyVoiceSidecarDefaultAsync(sidecar, ct).ConfigureAwait(false);
                if (model.IsError)
                    meta.AppendLog("voice sidecar model: " + model.Error);
            }
            else
            {
                var created = await CreateVoiceSidecarAsync(meta, ct).ConfigureAwait(false);
                if (created.IsError)
                    return created;
                sidecar = created.Value;
            }

            var memory = await LoadVoiceSidecarMemoryAsync(sidecar, ct).ConfigureAwait(false);
            if (memory.IsError)
                meta.AppendLog("voice sidecar memory: " + memory.Error);
            else
                sidecar.SetVoiceSidecarMemory(memory.Value);

            meta.VoiceSidecar = sidecar;
            sidecar.VoiceSidecarOwner = meta;
            SubscribePair(meta, sidecar);
            return Result<DysonAgentSession, string>.AsValue(sidecar);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Posts one accepted utterance onto the Meta session's sidecar and drains that sidecar.
    /// A provider error other than prompt-superseded / prompt-deferred is returned so the host
    /// can fall back to Meta.
    /// </summary>
    public async Task<VoidResult<string>> RouteVoiceToSidecarAsync(
        DysonAgentSession meta,
        DysonVoiceMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var ensured = await EnsureVoiceSidecarAsync(meta, ct).ConfigureAwait(false);
        if (ensured.IsError)
            return VoidResult<string>.AsError(ensured.Error);

        var sidecar = ensured.Value;
        var turn = DysonVoiceMode.CreateTurn(message);
        var own = new TaskCompletionSource<VoidResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _voiceTurnResults[turn.Id] = own;
        // The bubble posts once the sidecar holds the turn (injected, interrupted or queued) and before the
        // drain runs it, so it lands above the sidecar's [spoken] lines. A route that fails before that
        // posts nothing and the host fallback shows the utterance once, as Meta's prompt.
        // ponytail: a sidecar provider error after acceptance still falls back, so that rare path shows the
        // utterance twice (bubble + Meta prompt); removing a posted display turn would need a new session API.
        var posted = 0;
        void PostBubble()
        {
            if (Interlocked.Exchange(ref posted, 1) == 0)
                meta.AppendDisplayInfoTurn(message.Text);
        }

        try
        {
            var routed = await RouteVoiceTurnAsync(sidecar, message, turn, PostBubble, ct).ConfigureAwait(false);
            if (routed.IsError && !IsPromptControl(routed))
                return routed;
            // Not completed = still queued behind a background run; the redrain owns it now.
            var accepted = own.Task.IsCompleted ? await own.Task.ConfigureAwait(false) : VoidResult<string>.Success;
            if (accepted.IsError && !IsPromptControl(accepted))
                return accepted;
            PostBubble();
            return accepted;
        }
        finally
        {
            _voiceTurnResults.TryRemove(turn.Id, out _);
        }
    }

    private async Task<VoidResult<string>> RouteVoiceTurnAsync(
        DysonAgentSession sidecar,
        DysonVoiceMessage message,
        DysonAgentTurn turn,
        Action accepted,
        CancellationToken ct)
    {
        var route = DysonVoiceMode.RouteFor(
            message.Mode,
            sidecar.InFlightPromptTurn is not null,
            sidecar.HasActiveBackgroundRun,
            sidecar.IsSummaryInFlight || sidecar.HasAnySummarizingTurn);

        VoidResult<string> routed;
        switch (route)
        {
            case DysonVoiceRoute.Inject:
            {
                var inFlight = sidecar.InFlightPromptTurn;
                var injected = inFlight is null
                    ? VoidResult<string>.AsError("Turn is not currently running.")
                    : sidecar.InjectTurnComment(inFlight.Id, message.Text);
                if (injected.IsSuccess)
                {
                    accepted();
                    CompleteVoiceTurn(turn.Id, VoidResult<string>.Success);
                }

                routed = injected.IsSuccess
                    ? VoidResult<string>.Success
                    : await EnqueueAndDrainAsync(sidecar, turn, accepted, ct).ConfigureAwait(false);
                break;
            }
            case DysonVoiceRoute.Interrupt:
                Voice?.DropPendingSpeech();
                var interrupted = InterruptWithPrompt(sidecar.PersistenceId, turn);
                if (interrupted.IsError)
                {
                    routed = VoidResult<string>.AsError(interrupted.Error);
                    break;
                }

                accepted();
                routed = await DrainVoiceSidecarPromptsAsync(sidecar, ct).ConfigureAwait(false);
                break;
            default:
                routed = await EnqueueAndDrainAsync(sidecar, turn, accepted, ct).ConfigureAwait(false);
                break;
        }

        return routed;
    }

    /// <summary>
    /// Applies the Agent Behavior Meta Sidecar model to every live sidecar. An empty setting is a no-op.
    /// One sidecar failing does not stop the rest; every failure is one line of the returned error.
    /// </summary>
    public async Task<VoidResult<string>> ApplyVoiceSidecarModelAsync(CancellationToken ct = default)
    {
        if (_disposed != 0)
            return VoidResult<string>.AsError("Session runtime has been disposed.");

        var failures = new List<string>();
        foreach (var meta in SnapshotLiveSessions())
        {
            if (meta.Parent is not null || !DysonSessionPolicy.IsMetaAgent(meta.Mode))
                continue;
            if (meta.VoiceSidecar is not { } sidecar)
                continue;

            var applied = await ApplyVoiceSidecarDefaultAsync(sidecar, ct).ConfigureAwait(false);
            if (applied.IsError)
                failures.Add(applied.Error);
        }

        return failures.Count == 0
            ? VoidResult<string>.Success
            : VoidResult<string>.AsError(string.Join("\n", failures));
    }

    /// <summary>
    /// Voice turned off. Drops sidecar↔Meta turns already queued (both directions), voice turns queued on
    /// each sidecar, and anything still in the pair's inboxes. A typed prompt on Meta stays queued.
    /// Also makes an inbox delivery already in flight a no-op.
    /// </summary>
    public VoidResult<string> DropVoiceTurnsOnVoiceOff()
    {
        if (_disposed != 0)
            return VoidResult<string>.AsError("Session runtime has been disposed.");

        Interlocked.Increment(ref _voiceDeliveryGeneration);
        var changed = new HashSet<Guid>();
        lock (_promptQueueGate)
        {
            foreach (var sessionId in _promptQueues.Keys.ToArray())
            {
                if (!_sessionsById.TryGetValue(sessionId, out var session))
                    continue;

                var meta = session.Parent is null && DysonSessionPolicy.IsMetaAgent(session.Mode);
                var sidecar = session.IsVoiceSidecar;
                if (!meta && !sidecar)
                    continue;
                if (!_promptQueues.TryGetValue(sessionId, out var queue))
                    continue;

                var kept = new Queue<DysonQueuedPrompt>();
                var dropped = false;
                while (queue.TryDequeue(out var entry))
                {
                    if (IsVoiceOffDrop(entry.Turn, sidecar))
                    {
                        dropped = true;
                        CompleteVoiceTurn(entry.Turn.Id, VoidResult<string>.Success);
                        continue;
                    }

                    kept.Enqueue(entry);
                }

                if (kept.Count == 0)
                    _promptQueues.Remove(sessionId);
                else
                    _promptQueues[sessionId] = kept;
                if (dropped)
                    changed.Add(sessionId);
            }
        }

        foreach (var channel in _voiceSidecarInboxes.Values)
        {
            while (channel.Reader.TryRead(out _))
            {
            }
        }

        foreach (var sessionId in changed)
            RaiseChanged(DysonRuntimeChangeKind.Queue, sessionId);

        return VoidResult<string>.Success;
    }

    /// <summary>A sidecar queue is entirely voice traffic. On Meta only the framed sidecar messages go.</summary>
    private static bool IsVoiceOffDrop(DysonAgentTurn turn, bool sidecarQueue) =>
        sidecarQueue || IsSidecarToMetaTurn(turn);

    private static bool IsSidecarToMetaTurn(DysonAgentTurn turn) =>
        turn.Instruction.StartsWith(
            DysonInjectedUserComments.SidecarMarker + "\n\n",
            StringComparison.Ordinal);

    /// <summary>
    /// Pushes the Agent Behavior Meta Sidecar setting onto one sidecar. Unset setting = keep the
    /// current model. Also run on load, so a sidecar that was not live when the setting changed picks it up.
    /// </summary>
    private static async Task<VoidResult<string>> ApplyVoiceSidecarDefaultAsync(
        DysonAgentSession sidecar,
        CancellationToken ct)
    {
        var provider = await sidecar.ResolveOwnModeDefaultProviderAsync(ct).ConfigureAwait(false);
        if (provider is null)
            return VoidResult<string>.Success;
        if (sidecar.SelectedProvider is OpenAiCompatibleAgentProvider current
            && provider is OpenAiCompatibleAgentProvider next
            && next.SlugId == current.SlugId
            && string.Equals(next.ReasoningEffort, current.ReasoningEffort, StringComparison.Ordinal))
        {
            return VoidResult<string>.Success;
        }

        return await sidecar.ApplyProviderAsync(provider, ct).ConfigureAwait(false);
    }

    private async Task<Result<DysonAgentSession, string>> CreateVoiceSidecarAsync(
        DysonAgentSession meta,
        CancellationToken ct)
    {
        var full = await Sessions.GetFullSessionAsync(meta.PersistenceId, ct).ConfigureAwait(false);
        if (full.IsError)
            return Result<DysonAgentSession, string>.AsError(full.Error);

        Guid? modelSlugId = full.Value.Session.ModelSlugId;
        string? effort = null;
        if (meta.Config.AppSettings is { } settings)
        {
            var slug = await settings
                .GetSettingAsync(DysonAppSettingKeys.MetaSidecarModelSlugId, ct)
                .ConfigureAwait(false);
            if (slug.IsSuccess && Guid.TryParse(slug.Value, out var parsed) && parsed != Guid.Empty)
            {
                modelSlugId = parsed;
                var effortSetting = await settings
                    .GetSettingAsync(DysonAppSettingKeys.MetaSidecarReasoningEffort, ct)
                    .ConfigureAwait(false);
                effort = effortSetting.IsSuccess ? effortSetting.Value : null;
            }
            else if (meta.Provider is OpenAiCompatibleAgentProvider parent)
            {
                modelSlugId = parent.SlugId ?? modelSlugId;
                effort = parent.ReasoningEffort;
            }
        }
        else if (meta.Provider is OpenAiCompatibleAgentProvider parent)
        {
            modelSlugId = parent.SlugId ?? modelSlugId;
            effort = parent.ReasoningEffort;
        }

        var created = await Sessions.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                ParentSessionId = meta.PersistenceId,
                AgentMode = DysonAgentModes.VoiceSidecar,
                ModelSlugId = modelSlugId,
                WorkDirectoryId = full.Value.Session.WorkDirectoryId,
                ReasoningEffort = effort,
                MaxTargetContextTokens = DysonMaxTargetContextTokens.VoiceSidecarFixed,
                SystemPromptSnapshot = DysonAgentSystemPrompts.VoiceModeMetaSidecarDirective,
                Title = "Voice sidecar",
            },
            ct).ConfigureAwait(false);
        if (created.IsError)
            return Result<DysonAgentSession, string>.AsError(created.Error);

        return await LoadSessionAsync(created.Value, ct).ConfigureAwait(false);
    }

    private static async Task<Result<string, string>> LoadVoiceSidecarMemoryAsync(
        DysonAgentSession sidecar,
        CancellationToken ct)
    {
        var repo = sidecar.Config.VoiceSidecarMemory;
        if (repo is null)
            return Result<string, string>.AsError("repository not available");

        var loaded = await repo.GetAsync(sidecar.PersistenceId, ct).ConfigureAwait(false);
        return loaded.IsError
            ? Result<string, string>.AsError(loaded.Error)
            : Result<string, string>.AsValue(loaded.Value.Content);
    }

    /// <summary>The sidecar id <see cref="SubscribePair"/> last stored for <paramref name="metaId"/>. Tests only.</summary>
    internal Guid? VoiceSidecarSubscribedIdForTest(Guid metaId) =>
        _voiceSidecarSubscriptions.TryGetValue(metaId, out var pair) ? pair.SidecarId : null;

    private void SubscribePair(DysonAgentSession meta, DysonAgentSession sidecar)
    {
        var bus = meta.Config.Bus;
        if (bus is null)
            return;

        var metaId = meta.PersistenceId;
        var sidecarId = sidecar.PersistenceId;
        if (_voiceSidecarSubscriptions.TryGetValue(metaId, out var existing))
        {
            if (existing.SidecarId == sidecarId)
                return;

            // The Meta subscription is first and stays. Everything after it was the old sidecar.
            for (var i = 1; i < existing.Tokens.Count; i++)
                existing.Tokens[i].Dispose();
            if (existing.Tokens.Count > 1)
                existing.Tokens.RemoveRange(1, existing.Tokens.Count - 1);

            var replaced = bus.Subscribe<DysonVoiceSidecarMessageEvent>(
                DysonBusScopes.Session(sidecarId),
                message => OfferVoiceSidecarMessage(message));
            if (replaced.IsSuccess)
                existing.Tokens.Add(replaced.Value);
            _voiceSidecarSubscriptions[metaId] = (sidecarId, existing.Tokens);
            return;
        }

        var tokens = new List<IDisposable>();
        var metaSub = bus.Subscribe<DysonVoiceSidecarMessageEvent>(
            DysonBusScopes.Session(metaId),
            message => OfferVoiceSidecarMessage(message));
        var sidecarSub = bus.Subscribe<DysonVoiceSidecarMessageEvent>(
            DysonBusScopes.Session(sidecarId),
            message => OfferVoiceSidecarMessage(message));
        if (metaSub.IsSuccess)
            tokens.Add(metaSub.Value);
        if (sidecarSub.IsSuccess)
            tokens.Add(sidecarSub.Value);
        if (!_voiceSidecarSubscriptions.TryAdd(metaId, (sidecarId, tokens)))
        {
            foreach (var token in tokens)
                token.Dispose();
            SubscribePair(meta, sidecar);
        }
    }

    /// <summary>Disposes every voice-sidecar bus subscription. Called from runtime dispose.</summary>
    private void DisposeVoiceSidecarSubscriptions()
    {
        foreach (var pair in _voiceSidecarSubscriptions)
        {
            if (_voiceSidecarSubscriptions.TryRemove(pair.Key, out var removed))
            {
                foreach (var token in removed.Tokens)
                    token.Dispose();
            }
        }
    }

    private void OfferVoiceSidecarMessage(DysonVoiceSidecarMessageEvent message)
    {
        if (message.ToSessionId == Guid.Empty)
            return;

        var channel = _voiceSidecarInboxes.GetOrAdd(
            message.ToSessionId,
            id =>
            {
                var created = Channel.CreateUnbounded<DysonVoiceSidecarMessageEvent>(
                    new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
                _ = Task.Run(() => ReadVoiceSidecarInboxAsync(id, created.Reader));
                return created;
            });
        channel.Writer.TryWrite(message);
    }

    private async Task ReadVoiceSidecarInboxAsync(
        Guid sessionId,
        ChannelReader<DysonVoiceSidecarMessageEvent> reader)
    {
        await foreach (var message in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (message.ToSessionId != sessionId)
                continue;

            try
            {
                var generation = Volatile.Read(ref _voiceDeliveryGeneration);
                var delivered = await DeliverVoiceSidecarMessageAsync(message, generation, CancellationToken.None)
                    .ConfigureAwait(false);
                if (delivered.IsError && !IsPromptControl(delivered))
                    ReportError(delivered.Error);
            }
            catch (Exception ex)
            {
                ReportError(ex.Message);
            }
        }
    }

    private async Task<VoidResult<string>> DeliverVoiceSidecarMessageAsync(
        DysonVoiceSidecarMessageEvent message,
        int generation,
        CancellationToken ct)
    {
        if (generation != Volatile.Read(ref _voiceDeliveryGeneration))
            return VoidResult<string>.Success;
        if (!_sessionsById.TryGetValue(message.ToSessionId, out var target))
            return VoidResult<string>.Success;

        var toSidecar = message.Direction == DysonVoiceSidecarDirection.ToSidecar;
        var marker = toSidecar
            ? DysonInjectedUserComments.MetaMarker
            : DysonInjectedUserComments.SidecarMarker;
        var framed = toSidecar
            ? DysonInjectedUserComments.FormatMetaMessage(message.Text)
            : DysonInjectedUserComments.FormatSidecarMessage(message.Text);
        var route = DysonVoiceMode.RouteFor(
            DysonVoiceInputMode.CallsignActivated,
            target.InFlightPromptTurn is not null,
            target.HasActiveBackgroundRun,
            target.IsSummaryInFlight || target.HasAnySummarizingTurn);

        if (generation != Volatile.Read(ref _voiceDeliveryGeneration))
            return VoidResult<string>.Success;

        if (route == DysonVoiceRoute.Inject && target.InFlightPromptTurn is { } inFlight)
        {
            var injected = target.InjectTurnComment(inFlight.Id, message.Text, marker);
            if (injected.IsSuccess)
                return VoidResult<string>.Success;
        }

        var turn = DysonAgentSession.CreateNormalTurn(framed);
        var queued = EnqueuePrompt(target.PersistenceId, turn);
        if (queued.IsError)
            return VoidResult<string>.AsError(queued.Error);
        if (generation != Volatile.Read(ref _voiceDeliveryGeneration))
        {
            // Voice went off between the check and the enqueue. Drop what we just queued.
            TryRemovePrompt(target.PersistenceId, queued.Value.Id);
            return VoidResult<string>.Success;
        }

        return await DrainRoutedAsync(target, toSidecar, ct).ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> EnqueueAndDrainAsync(
        DysonAgentSession sidecar,
        DysonAgentTurn turn,
        Action accepted,
        CancellationToken ct)
    {
        var queued = EnqueuePrompt(sidecar.PersistenceId, turn);
        if (queued.IsError)
            return VoidResult<string>.AsError(queued.Error);
        accepted();
        return await DrainVoiceSidecarPromptsAsync(sidecar, ct).ConfigureAwait(false);
    }

    private Task<VoidResult<string>> DrainRoutedAsync(
        DysonAgentSession target,
        bool toSidecar,
        CancellationToken ct)
    {
        if (!toSidecar && MetaPromptDrainHook?.Invoke(target.PersistenceId) == true)
            return Task.FromResult(VoidResult<string>.Success);

        return DrainVoiceSidecarPromptsAsync(target, ct);
    }

    /// <summary>
    /// Single-flight drain of the runtime prompt queue. Each turn's outcome goes to its own
    /// <see cref="_voiceTurnResults"/> entry (or <see cref="ReportError"/> when nobody waits on it).
    /// A deferred turn (background run active) goes back to the head of the queue and is redrained
    /// once the background run is idle, the same way the host's RequeueUntilBackgroundRunIdle does.
    /// </summary>
    internal async Task<VoidResult<string>> DrainVoiceSidecarPromptsAsync(
        DysonAgentSession session,
        CancellationToken ct)
    {
        var gate = _voiceSidecarDrainGates.GetOrAdd(session.PersistenceId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (TryDequeuePrompt(session.PersistenceId, out var entry))
            {
                var ran = await ExecutePromptAsync(
                    session,
                    (s, token) => s.PromptHarnessTurnAsync(entry.Turn, token),
                    ct).ConfigureAwait(false);
                if (ran.IsError && ran.Error == PromptDeferredError)
                {
                    var requeued = EnqueuePrompt(session.PersistenceId, entry.Turn, entry.FilePaths, atHead: true);
                    if (requeued.IsError)
                    {
                        CompleteVoiceTurn(entry.Turn.Id, VoidResult<string>.AsError(requeued.Error));
                        continue;
                    }

                    _ = session.WaitForBackgroundRunIdleAsync(CancellationToken.None).ContinueWith(
                        _ => DrainRoutedAsync(session, session.IsVoiceSidecar, CancellationToken.None),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default).Unwrap();
                    return VoidResult<string>.Success;
                }

                CompleteVoiceTurn(entry.Turn.Id, IsPromptControl(ran) ? VoidResult<string>.Success : ran);
            }

            return VoidResult<string>.Success;
        }
        finally
        {
            gate.Release();
        }
    }

    private void CompleteVoiceTurn(Guid turnId, VoidResult<string> result)
    {
        if (_voiceTurnResults.TryGetValue(turnId, out var waiter))
            waiter.TrySetResult(result);
        else if (result.IsError)
            ReportError(result.Error);
    }

    private static bool IsPromptControl(VoidResult<string> result) =>
        result.IsError
        && (result.Error == PromptSupersededError || result.Error == PromptDeferredError);
}
