namespace DysonHarness;

/// <summary>
/// Hooks a live session tree onto <see cref="DysonMessageBus"/> (status, spawn, turns, parent-events, coalesced activity).
/// </summary>
public sealed class DysonSessionEventPublisher(DysonMessageBus bus) : IDisposable
{
    private readonly DysonMessageBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    private readonly object _gate = new();
    // ponytail: one hook table + refcount so Wave 3 can Attach from runtime and UI on the same tree
    // without double-publishing. Upgrade only if a third attacher needs a different lifetime.
    private readonly Dictionary<DysonAgentSession, HookState> _hooks = new();
    private readonly List<AttachToken> _tokens = [];
    private bool _disposed;

    public Result<IDisposable, string> Attach(DysonAgentSession root)
    {
        if (root is null)
            return Result<IDisposable, string>.AsError("root is required");

        lock (_gate)
        {
            if (_disposed)
                return Result<IDisposable, string>.AsError("publisher is disposed");

            var token = new AttachToken(this);
            _tokens.Add(token);
            IncrementHookLocked(token, root);
            return Result<IDisposable, string>.AsValue(token);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var (session, state) in _hooks)
                UnhookLocked(session, state);

            _hooks.Clear();
            _tokens.Clear();
        }
    }

    private void IncrementHookLocked(AttachToken token, DysonAgentSession session)
    {
        if (!token.Sessions.Add(session))
            return;

        if (_hooks.TryGetValue(session, out var state))
        {
            state.RefCount++;
        }
        else
        {
            state = CreateHook(session);
            state.RefCount = 1;
            _hooks[session] = state;
            foreach (var turn in session.Turns)
                HookTurnLocked(session, state, turn);
        }

        foreach (var child in session.SubSessions.ToArray())
            IncrementHookLocked(token, child);
    }

    private HookState CreateHook(DysonAgentSession session)
    {
        var state = new HookState
        {
            StatusHandler = (_, e) => OnStatusChanged(session, e),
            SpawnHandler = (_, child) => OnSubagentSpawned(session, child),
            TurnAddedHandler = (_, turn) => OnTurnAdded(session, turn),
            ParentEventsHandler = (_, _) => OnParentEventsChanged(session),
        };
        state.Coalescer = new DysonNotifyCoalescer(_ => PublishActivity(session));
        session.StatusChanged += state.StatusHandler;
        session.SubagentSpawned += state.SpawnHandler;
        session.TurnAdded += state.TurnAddedHandler;
        session.ParentEventsChanged += state.ParentEventsHandler;
        return state;
    }

    private void HookTurnLocked(DysonAgentSession session, HookState state, DysonAgentTurn turn)
    {
        if (state.TurnTextHandlers.ContainsKey(turn))
            return;

        EventHandler handler = (_, _) => OnAssistantTextChanged(session);
        state.TurnTextHandlers[turn] = handler;
        turn.AssistantTextChanged += handler;
    }

    private void OnSubagentSpawned(DysonAgentSession parent, DysonAgentSession child)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            foreach (var token in _tokens)
            {
                if (token.Sessions.Contains(parent))
                    IncrementHookLocked(token, child);
            }
        }

        PublishSpawn(child);
    }

    private void OnStatusChanged(DysonAgentSession session, DysonSessionStatusChangedEventArgs e)
    {
        Guid persistenceId;
        Guid? parentPersistenceId;
        int runtimeId;
        lock (_gate)
        {
            if (_disposed || !_hooks.ContainsKey(session))
                return;

            persistenceId = session.PersistenceId;
            parentPersistenceId = session.Parent is null ? null : session.Parent.PersistenceId;
            runtimeId = session.Id;
        }

        var evt = new DysonSubagentStatusChangedEvent(
            persistenceId,
            parentPersistenceId,
            runtimeId,
            e.Status,
            e.Status == DysonSessionStatus.Active,
            e.Summary ?? session.LastReportSummary);

        _bus.Publish(DysonBusScopes.Session(persistenceId), evt);
        if (parentPersistenceId is { } parentId && parentId != Guid.Empty)
            _bus.Publish(DysonBusScopes.Session(parentId), evt);
    }

    private void OnParentEventsChanged(DysonAgentSession session)
    {
        Guid persistenceId;
        bool hasPendingAsk;
        bool hasPendingUserDialog;
        lock (_gate)
        {
            if (_disposed || !_hooks.ContainsKey(session))
                return;

            persistenceId = session.PersistenceId;
            hasPendingAsk = session.PendingAskQuestions is { Count: > 0 }
                || HasPendingParentEventKind(session, DysonAskQuestion.AskQuestionKind);
            hasPendingUserDialog = session.PendingUserDialog is not null
                || HasPendingParentEventKind(session, DysonPromptUserDialog.PromptUserDialogKind);
        }

        _bus.Publish(
            DysonBusScopes.Session(persistenceId),
            new DysonParentEventsChangedEvent(persistenceId, hasPendingAsk, hasPendingUserDialog));
    }

    private static bool HasPendingParentEventKind(DysonAgentSession session, string kind)
    {
        foreach (var evt in session.PendingOrRecentParentEvents)
        {
            if (evt.Status == DysonParentEventStatus.Pending
                && string.Equals(evt.Kind, kind, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void OnTurnAdded(DysonAgentSession session, DysonAgentTurn turn)
    {
        DysonNotifyCoalescer? coalescer = null;
        Guid persistenceId;
        lock (_gate)
        {
            if (_disposed || !_hooks.TryGetValue(session, out var state))
                return;

            HookTurnLocked(session, state, turn);
            coalescer = state.Coalescer;
            persistenceId = session.PersistenceId;
        }

        _bus.Publish(
            DysonBusScopes.Session(persistenceId),
            new DysonSessionTurnAddedEvent(persistenceId, turn.Id, turn.Kind));
        coalescer.Notify(DysonHostChangeKind.Streaming);
    }

    private void OnAssistantTextChanged(DysonAgentSession session)
    {
        DysonNotifyCoalescer? coalescer;
        lock (_gate)
        {
            if (_disposed || !_hooks.TryGetValue(session, out var state))
                return;

            coalescer = state.Coalescer;
        }

        coalescer.Notify(DysonHostChangeKind.Streaming);
    }

    private void PublishSpawn(DysonAgentSession child)
    {
        var parent = child.Parent;
        if (parent is null)
            return;

        var evt = new DysonSubagentSpawnedEvent(
            parent.PersistenceId,
            child.PersistenceId,
            child.Id,
            child.DisplayTitle ?? "",
            child.Mode);

        _bus.Publish(DysonBusScopes.Session(parent.PersistenceId), evt);
        _bus.Publish(DysonBusScopes.Session(child.PersistenceId), evt);
    }

    private void PublishActivity(DysonAgentSession session)
    {
        DysonSubagentActivityChangedEvent evt;
        Guid? parentPersistenceId;
        lock (_gate)
        {
            if (_disposed || !_hooks.TryGetValue(session, out var state))
                return;

            var title = session.DisplayTitle ?? "";
            var latestTurn = session.Turns.Count > 0 ? session.Turns[^1] : null;
            var stepTitle = DysonReasoningHistoryUi.TryGetLatestStepTitle(latestTurn);
            var isRunning = session.Status == DysonSessionStatus.Active;
            var tuple = (title, stepTitle, isRunning);
            if (state.LastActivity is { } last && last == tuple)
                return;

            state.LastActivity = tuple;
            evt = new DysonSubagentActivityChangedEvent(
                session.PersistenceId,
                session.Id,
                title,
                stepTitle,
                isRunning);
            parentPersistenceId = session.Parent is null ? null : session.Parent.PersistenceId;
        }

        _bus.Publish(DysonBusScopes.Session(evt.PersistenceId), evt);
        if (parentPersistenceId is { } parentId && parentId != Guid.Empty)
            _bus.Publish(DysonBusScopes.Session(parentId), evt);
    }

    private void Release(AttachToken token)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _tokens.Remove(token);
            foreach (var session in token.Sessions)
                DecrementHookLocked(session);
        }
    }

    private void DecrementHookLocked(DysonAgentSession session)
    {
        if (!_hooks.TryGetValue(session, out var state))
            return;

        state.RefCount--;
        if (state.RefCount > 0)
            return;

        UnhookLocked(session, state);
        _hooks.Remove(session);
    }

    private static void UnhookLocked(DysonAgentSession session, HookState state)
    {
        session.StatusChanged -= state.StatusHandler;
        session.SubagentSpawned -= state.SpawnHandler;
        session.TurnAdded -= state.TurnAddedHandler;
        session.ParentEventsChanged -= state.ParentEventsHandler;
        foreach (var (turn, handler) in state.TurnTextHandlers)
            turn.AssistantTextChanged -= handler;

        state.TurnTextHandlers.Clear();
        state.Coalescer.Dispose();
    }

    private sealed class HookState
    {
        public int RefCount;
        public required EventHandler<DysonSessionStatusChangedEventArgs> StatusHandler;
        public required EventHandler<DysonAgentSession> SpawnHandler;
        public required EventHandler<DysonAgentTurn> TurnAddedHandler;
        public required EventHandler ParentEventsHandler;
        public DysonNotifyCoalescer Coalescer = null!;
        public readonly Dictionary<DysonAgentTurn, EventHandler> TurnTextHandlers = new();
        public (string Title, string? LatestTurnStepTitle, bool IsRunning)? LastActivity;
    }

    private sealed class AttachToken(DysonSessionEventPublisher publisher) : IDisposable
    {
        public HashSet<DysonAgentSession> Sessions { get; } = [];
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            publisher.Release(this);
        }
    }
}
