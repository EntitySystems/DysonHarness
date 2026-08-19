using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DysonHarness;

/// <summary>
/// Subject-scoped live session graph owner. Registered scoped and retained by
/// <see cref="DysonSessionRuntimeRegistry"/>, not by a circuit.
/// Owns factory session leases, PersistenceId registration, parent-id tracking,
/// durable turn/log/tool persistence, per-session prompt execution (single-flight
/// gate + runtime-owned CTS), and per-session FIFO prompt queues (already-built
/// <see cref="DysonAgentTurn"/> plus an optional file-path snapshot). Queue storage
/// is not drained or executed here. Auto-turns and task-lifecycle evaluation stay
/// on the host.
/// </summary>
public sealed class DysonSessionRuntime : IAsyncDisposable
{
    private readonly IDysonSessionRepository _sessions;
    private readonly IDysonAgentSessionRuntimeFactory _sessionFactory;
    private readonly ConcurrentDictionary<Guid, DysonAgentSession> _sessionsById = new();
    private readonly ConcurrentDictionary<Guid, Guid> _parentSessionIdByChild = new();
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _hookedSessions = new();
    private readonly ConcurrentDictionary<Guid, DysonAgentSessionRuntimeLease> _leasesById = new();
    private readonly ConcurrentDictionary<Guid, EventHandler<DysonToolCallStatusChangedEventArgs>> _toolHandlers = new();
    private readonly ConcurrentDictionary<Guid, byte> _busySessions = new();
    // ponytail: one lock for all session queues; split per session if enqueue becomes hot.
    private readonly object _promptQueueGate = new();
    private readonly Dictionary<Guid, Queue<DysonQueuedPrompt>> _promptQueues = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _promptGates = new();
    private readonly ConcurrentDictionary<CancellationTokenSource, Guid> _livePromptCts = new();
    private readonly ConcurrentDictionary<int, Task> _persistTasks = new();
    private readonly ConcurrentDictionary<int, Task> _executionTasks = new();
    private readonly SemaphoreSlim _graphGate = new(1, 1);
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private readonly string _subjectId;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private bool _recoveryCompleted;
    private int _persistSeq;
    private int _executionSeq;
    private long _changeVersion;
    private int _disposed;


    public DysonSessionRuntime(
        IDysonSubjectContext subjectContext,
        IDysonSessionRepository sessions,
        IDysonAgentSessionRuntimeFactory sessionFactory)
    {
        ArgumentNullException.ThrowIfNull(subjectContext);
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));

        var normalized = DysonSessionRuntimeRegistry.NormalizeSubjectId(subjectContext.SubjectId);
        if (normalized.IsError)
            throw new ArgumentException(normalized.Error, nameof(subjectContext));

        _subjectId = normalized.Value;
    }

    /// <summary>
    /// Subject captured at construction. Never re-reads <see cref="IDysonSubjectContext"/>,
    /// so a later rebound scoped context cannot change this runtime's identity.
    /// </summary>
    public string SubjectId => _subjectId;

    /// <summary>Current runtime-level error, if any. Circuit-local UI errors stay on the facade.</summary>
    public string? LastError { get; private set; }

    /// <summary>Process-restart recovery result from the first retained-scope attach, if it ran successfully.</summary>
    public DysonSessionRecoveryReport? LastRecoveryReport { get; private set; }

    public event EventHandler<DysonRuntimeChange>? Changed;

    /// <summary>
    /// Repairs durable unfinished work once for this retained subject runtime. It never replays
    /// a model/tool call; registry creation invokes it before exposing the runtime to a circuit.
    /// </summary>
    public async Task<Result<DysonSessionRecoveryReport, string>> EnsureRecoveredAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed != 0)
            return Result<DysonSessionRecoveryReport, string>.AsError("Session runtime has been disposed.");

        await _recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return Result<DysonSessionRecoveryReport, string>.AsError("Session runtime has been disposed.");

            if (_recoveryCompleted && LastRecoveryReport is { } completed)
                return Result<DysonSessionRecoveryReport, string>.AsValue(completed);

            var recovered = await new DysonSessionRecoveryService(_sessions)
                .RecoverAsync(cancellationToken)
                .ConfigureAwait(false);
            if (recovered.IsError)
                return Result<DysonSessionRecoveryReport, string>.AsError(recovered.Error);

            LastRecoveryReport = recovered.Value;
            _recoveryCompleted = true;
            RaiseChanged(DysonRuntimeChangeKind.Recovery);
            return Result<DysonSessionRecoveryReport, string>.AsValue(recovered.Value);
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    /// <summary>In-memory live session lookup. Does not cold-load from persistence.</summary>
    public bool TryGetSession(Guid sessionId, [NotNullWhen(true)] out DysonAgentSession? session)
    {
        session = null;
        if (_disposed != 0 || sessionId == Guid.Empty)
            return false;

        if (_sessionsById.TryGetValue(sessionId, out session))
            return true;

        foreach (var candidate in _hookedSessions.Keys)
        {
            if (candidate.PersistenceId != sessionId)
                continue;

            EnsureSessionMapped(candidate);
            session = candidate;
            return true;
        }

        return false;
    }

    /// <summary>In-memory live session lookup. Does not cold-load from persistence.</summary>
    public Task<Result<DysonAgentSession, string>> GetSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed != 0)
            return Task.FromResult(Result<DysonAgentSession, string>.AsError("Session runtime has been disposed."));

        if (sessionId == Guid.Empty)
            return Task.FromResult(Result<DysonAgentSession, string>.AsError("Session id is required."));

        if (TryGetSession(sessionId, out var session))
            return Task.FromResult(Result<DysonAgentSession, string>.AsValue(session));

        return Task.FromResult(Result<DysonAgentSession, string>.AsError("Session was not found."));
    }

    public bool TryGetParentSessionId(Guid sessionId, out Guid parentSessionId)
    {
        parentSessionId = Guid.Empty;
        if (_disposed != 0 || sessionId == Guid.Empty)
            return false;

        if (!_parentSessionIdByChild.TryGetValue(sessionId, out var parent) || parent == Guid.Empty)
            return false;

        parentSessionId = parent;
        return true;
    }

    public bool IsBusy(Guid sessionId) =>
        _disposed == 0 && sessionId != Guid.Empty && _busySessions.ContainsKey(sessionId);

    public int GetQueuedPromptCount(Guid sessionId)
    {
        if (_disposed != 0 || sessionId == Guid.Empty)
            return 0;

        lock (_promptQueueGate)
        {
            return _promptQueues.TryGetValue(sessionId, out var queue) ? queue.Count : 0;
        }
    }

    /// <summary>
    /// Enqueues an already-built turn for a registered, persisted session. Copies
    /// <paramref name="filePaths"/> into an immutable snapshot. Does not execute the prompt.
    /// </summary>
    public Result<DysonQueuedPrompt, string> EnqueuePrompt(
        Guid sessionId,
        DysonAgentTurn turn,
        IReadOnlyList<string>? filePaths = null)
    {
        ArgumentNullException.ThrowIfNull(turn);

        if (_disposed != 0)
            return Result<DysonQueuedPrompt, string>.AsError("Session runtime has been disposed.");

        if (sessionId == Guid.Empty)
            return Result<DysonQueuedPrompt, string>.AsError("Session id is required.");

        if (!_sessionsById.TryGetValue(sessionId, out var session) || session.PersistenceId == Guid.Empty)
            return Result<DysonQueuedPrompt, string>.AsError("Session is not registered with this runtime.");

        if (!turn.AllowEnqueue)
            return Result<DysonQueuedPrompt, string>.AsError("TaskEndReflect cannot be enqueued.");

        var entry = new DysonQueuedPrompt
        {
            SessionId = sessionId,
            Turn = turn,
            FilePaths = CopyFilePaths(filePaths),
        };

        lock (_promptQueueGate)
        {
            if (_disposed != 0)
                return Result<DysonQueuedPrompt, string>.AsError("Session runtime has been disposed.");

            if (!_sessionsById.ContainsKey(sessionId))
                return Result<DysonQueuedPrompt, string>.AsError("Session is not registered with this runtime.");

            if (!_promptQueues.TryGetValue(sessionId, out var queue))
            {
                queue = new Queue<DysonQueuedPrompt>();
                _promptQueues[sessionId] = queue;
            }

            queue.Enqueue(entry);
        }

        RaiseChanged(DysonRuntimeChangeKind.Queue, sessionId);
        return Result<DysonQueuedPrompt, string>.AsValue(entry);
    }

    public bool TryPeekPrompt(Guid sessionId, [NotNullWhen(true)] out DysonQueuedPrompt? prompt)
    {
        prompt = null;
        if (_disposed != 0 || sessionId == Guid.Empty)
            return false;

        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var queue) || queue.Count == 0)
                return false;

            return queue.TryPeek(out prompt) && prompt is not null;
        }
    }

    public bool TryDequeuePrompt(Guid sessionId, [NotNullWhen(true)] out DysonQueuedPrompt? prompt)
    {
        prompt = null;
        if (_disposed != 0 || sessionId == Guid.Empty)
            return false;

        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var queue) || !queue.TryDequeue(out prompt))
                return false;

            if (queue.Count == 0)
                _promptQueues.Remove(sessionId);
        }

        RaiseChanged(DysonRuntimeChangeKind.Queue, sessionId);
        return prompt is not null;
    }

    /// <summary>
    /// Drops every queued prompt for <paramref name="sessionId"/>. Succeeds when the
    /// queue is already empty or the session is gone. Does not cancel in-flight execution.
    /// </summary>
    public VoidResult<string> DiscardQueuedPrompts(Guid sessionId)
    {
        if (_disposed != 0)
            return VoidResult<string>.AsError("Session runtime has been disposed.");

        if (sessionId == Guid.Empty)
            return VoidResult<string>.AsError("Session id is required.");

        if (!ClearPromptQueue(sessionId))
            return VoidResult<string>.Success;

        RaiseChanged(DysonRuntimeChangeKind.Queue, sessionId);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Runs <paramref name="run"/> under the per-session single-flight gate. The runtime
    /// owns a linked CTS (caller token + <see cref="CancelPrompt"/> / dispose) so a circuit
    /// token is not required for cancellation. Successful runs persist every unfinished turn.
    /// Does not enqueue, drain, or evaluate task lifecycle.
    /// </summary>
    public async Task<VoidResult<string>> ExecutePromptAsync(
        DysonAgentSession session,
        Func<DysonAgentSession, CancellationToken, Task<VoidResult<string>>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(run);

        await _lifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        Task<VoidResult<string>> execution;
        try
        {
            if (_disposed != 0)
                return VoidResult<string>.AsError("Session runtime has been disposed.");

            if (session.PersistenceId == Guid.Empty)
                return VoidResult<string>.AsError("Session is not persisted.");

            EnsureSessionMapped(session);
            if (!_sessionsById.TryGetValue(session.PersistenceId, out var live)
                || !ReferenceEquals(live, session))
            {
                return VoidResult<string>.AsError("Session is not registered with this runtime.");
            }

            execution = ExecutePromptCoreAsync(session, run, cancellationToken);
            TrackExecution(execution);
        }
        finally
        {
            _lifetimeGate.Release();
        }

        return await execution.ConfigureAwait(false);
    }

    /// <summary>Cancels every in-flight / waiting prompt CTS for <paramref name="sessionId"/>.</summary>
    public VoidResult<string> CancelPrompt(Guid sessionId)
    {
        if (_disposed != 0)
            return VoidResult<string>.AsError("Session runtime has been disposed.");

        if (sessionId == Guid.Empty)
            return VoidResult<string>.AsError("Session id is required.");

        CancelPromptTokens(sessionId);
        return VoidResult<string>.Success;
    }

    public async Task<Result<DysonAgentSession, string>> CreateRootAsync(
        DysonAgentSessionRuntimeCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed != 0)
            return Result<DysonAgentSession, string>.AsError("Session runtime has been disposed.");

        if (string.IsNullOrWhiteSpace(request.AgentMode))
            return FailSession("Agent mode is required.");

        await _graphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return Result<DysonAgentSession, string>.AsError("Session runtime has been disposed.");

            var created = await _sessionFactory.CreateRootAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return FailSession(created.Error);

            return await AdoptFactoryLeaseAsync(created.Value, parentSessionId: null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _graphGate.Release();
        }
    }

    public async Task<Result<DysonAgentSession, string>> LoadSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed != 0)
            return Result<DysonAgentSession, string>.AsError("Session runtime has been disposed.");

        if (sessionId == Guid.Empty)
            return FailSession("Session id is required.");

        if (_sessionsById.TryGetValue(sessionId, out var live))
            return Result<DysonAgentSession, string>.AsValue(live);

        await _graphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return Result<DysonAgentSession, string>.AsError("Session runtime has been disposed.");

            if (_sessionsById.TryGetValue(sessionId, out live))
                return Result<DysonAgentSession, string>.AsValue(live);

            var loaded = await _sessionFactory.LoadAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (loaded.IsError)
                return FailSession(loaded.Error);

            Guid? parentId = null;
            var full = await _sessions.GetFullSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (full.IsSuccess)
                parentId = full.Value.Session.ParentSessionId;

            return await AdoptFactoryLeaseAsync(loaded.Value, parentId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _graphGate.Release();
        }
    }

    public async Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed != 0)
            return VoidResult<string>.AsError("Session runtime has been disposed.");

        if (sessionId == Guid.Empty)
            return FailVoid("Session id is required.");

        await FlushPersistenceAsync().ConfigureAwait(false);
        await _graphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return VoidResult<string>.AsError("Session runtime has been disposed.");

            await UnregisterSessionTreeAsync(sessionId).ConfigureAwait(false);

            var deleted = await _sessions.DeleteSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (deleted.IsError)
                return FailVoid(deleted.Error);

            ClearLastError();
            RaiseChanged(DysonRuntimeChangeKind.SessionGraph, sessionId);
            return VoidResult<string>.Success;
        }
        finally
        {
            _graphGate.Release();
        }
    }

    public void ReportError(string message)
    {
        if (_disposed != 0)
            return;

        LastError = string.IsNullOrWhiteSpace(message) ? "Unexpected error." : message.Trim();
        RaiseChanged(DysonRuntimeChangeKind.Error);
    }

    public void ClearLastError()
    {
        if (_disposed != 0 || LastError is null)
            return;

        LastError = null;
        RaiseChanged(DysonRuntimeChangeKind.Error);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            CancelAllPromptTokens();
        }
        finally
        {
            _lifetimeGate.Release();
        }

        await FlushExecutionAsync().ConfigureAwait(false);
        await FlushPersistenceAsync().ConfigureAwait(false);

        await _graphGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in _hookedSessions.Keys.ToArray())
                UnhookSession(session);

            foreach (var lease in _leasesById.Values.ToArray())
                await lease.DisposeAsync().ConfigureAwait(false);

            foreach (var gate in _promptGates.Values)
                gate.Dispose();

            foreach (var cts in _livePromptCts.Keys)
                cts.Dispose();

            _sessionsById.Clear();
            _parentSessionIdByChild.Clear();
            _hookedSessions.Clear();
            _leasesById.Clear();
            _toolHandlers.Clear();
            _busySessions.Clear();
            lock (_promptQueueGate)
                _promptQueues.Clear();
            _promptGates.Clear();
            _livePromptCts.Clear();
            LastError = null;
        }
        finally
        {
            _graphGate.Release();
        }
    }

    internal IDysonSessionRepository Sessions => _sessions;

    internal IDysonAgentSessionRuntimeFactory SessionFactory => _sessionFactory;

    internal Task FlushPersistenceAsync()
    {
        var pending = _persistTasks.Values.ToArray();
        return pending.Length == 0 ? Task.CompletedTask : Task.WhenAll(pending);
    }

    internal async Task FlushExecutionAsync()
    {
        var pending = _executionTasks.Values.ToArray();
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Observed so dispose can finish after a faulted prompt.
            }
        }
    }

    private async Task<VoidResult<string>> ExecutePromptCoreAsync(
        DysonAgentSession session,
        Func<DysonAgentSession, CancellationToken, Task<VoidResult<string>>> run,
        CancellationToken cancellationToken)
    {
        var sessionId = session.PersistenceId;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _livePromptCts[linked] = sessionId;
        var token = linked.Token;
        var promptGate = _promptGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        var gateHeld = false;

        try
        {
            if (_disposed != 0)
                return VoidResult<string>.AsError("Session runtime has been disposed.");

            try
            {
                await promptGate.WaitAsync(token).ConfigureAwait(false);
                gateHeld = true;
            }
            catch (OperationCanceledException)
            {
                return VoidResult<string>.AsError("Prompt was cancelled.");
            }

            if (_disposed != 0)
                return VoidResult<string>.AsError("Session runtime has been disposed.");

            _busySessions[sessionId] = 0;
            RaiseChanged(DysonRuntimeChangeKind.Busy, sessionId);

            try
            {
                VoidResult<string> result;
                try
                {
                    result = await run(session, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return VoidResult<string>.AsError("Prompt was cancelled.");
                }

                if (result.IsError)
                    return result;

                var last = session.Turns.Count > 0 ? session.Turns[^1] : null;
                if (last is not null)
                {
                    if (last.Kind == DysonAgentTurnKind.ShellExited)
                        DysonLongRunningShellExitedFlow.TrimInstructionAfterCompletion(last);

                    IReadOnlyList<DysonAgentTurn> dropped = [];
                    if (DysonFullSummarizeFlow.ShouldApplyAfterCompletion(last.Kind))
                        dropped = DysonFullSummarizeFlow.ApplyAfterCompletion(session, last);

                    foreach (var turn in session.Turns)
                    {
                        if (turn.CompletedUtc is not null)
                            continue;

                        var complete = await PersistTurnCompletedAsync(session, turn, token)
                            .ConfigureAwait(false);
                        if (complete.IsError)
                            return complete;
                    }

                    var persistDropped = await PersistDroppedTurnsAsync(session, dropped, token)
                        .ConfigureAwait(false);
                    if (persistDropped.IsError)
                        return persistDropped;

                    RaiseChanged(DysonRuntimeChangeKind.SessionGraph, sessionId);
                }

                return VoidResult<string>.Success;
            }
            finally
            {
                _busySessions.TryRemove(sessionId, out _);
                RaiseChanged(DysonRuntimeChangeKind.Busy, sessionId);
            }
        }
        finally
        {
            if (gateHeld)
                promptGate.Release();

            _livePromptCts.TryRemove(linked, out _);
            linked.Dispose();
        }
    }

    private async Task<VoidResult<string>> PersistTurnCompletedAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty)
            return VoidResult<string>.Success;

        var sessionId = session.PersistenceId;
        var sequence = IndexOfTurn(session, turn);
        turn.CompletedUtc = DateTime.UtcNow;
        var entity = DysonTurnPersistence.ToEntity(
            turn,
            sessionId,
            sequence,
            completedUtc: turn.CompletedUtc);

        var upsert = await PersistAsync(() => _sessions.UpsertTurnAsync(entity, cancellationToken))
            .ConfigureAwait(false);
        if (upsert.IsError)
            return upsert;

        var reply = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.AgentReply,
            new DysonSessionLogAgentReply(turn.Id, turn.AgentTitle, turn.AssistantText ?? ""),
            turnId: turn.Id);

        var appendReply = await PersistAsync(() => _sessions.AppendLogAsync(reply, cancellationToken))
            .ConfigureAwait(false);
        if (appendReply.IsError)
            return appendReply;

        var completed = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.TurnCompleted,
            new DysonSessionLogTurnCompleted(turn.Id, turn.Kind, turn.AgentTitle),
            turnId: turn.Id);

        return await PersistAsync(() => _sessions.AppendLogAsync(completed, cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> PersistDroppedTurnsAsync(
        DysonAgentSession session,
        IReadOnlyList<DysonAgentTurn> dropped,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty || dropped.Count == 0)
            return VoidResult<string>.Success;

        var sessionId = session.PersistenceId;
        foreach (var turn in dropped)
        {
            var sequence = IndexOfTurn(session, turn);
            var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
            var upsert = await PersistAsync(() => _sessions.UpsertTurnAsync(entity, cancellationToken))
                .ConfigureAwait(false);
            if (upsert.IsError)
                return upsert;
        }

        return VoidResult<string>.Success;
    }

    private void CancelPromptTokens(Guid sessionId)
    {
        foreach (var kv in _livePromptCts)
        {
            if (kv.Value != sessionId)
                continue;

            try
            {
                kv.Key.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void CancelAllPromptTokens()
    {
        foreach (var cts in _livePromptCts.Keys)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void TrackExecution(Task task)
    {
        var id = Interlocked.Increment(ref _executionSeq);
        _executionTasks[id] = task;
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                if (state is not ValueTuple<ConcurrentDictionary<int, Task>, int> boxed)
                    return;

                boxed.Item1.TryRemove(boxed.Item2, out _);
                _ = completed.Exception;
            },
            (_executionTasks, id),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<Result<DysonAgentSession, string>> AdoptFactoryLeaseAsync(
        DysonAgentSessionRuntimeLease lease,
        Guid? parentSessionId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        ArgumentNullException.ThrowIfNull(lease);

        var session = lease.Session;
        if (session.PersistenceId == Guid.Empty)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            return FailSession("Factory session is missing PersistenceId.");
        }

        var id = session.PersistenceId;
        if (_sessionsById.TryGetValue(id, out var existing))
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            RememberParentId(existing, parentSessionId);
            EnsureRegistered(existing);
            ClearLastError();
            return Result<DysonAgentSession, string>.AsValue(existing);
        }

        if (!_leasesById.TryAdd(id, lease))
        {
            await lease.DisposeAsync().ConfigureAwait(false);
            if (_sessionsById.TryGetValue(id, out existing))
            {
                RememberParentId(existing, parentSessionId);
                EnsureRegistered(existing);
                ClearLastError();
                return Result<DysonAgentSession, string>.AsValue(existing);
            }

            return FailSession("Session is already registered.");
        }

        RememberParentId(session, parentSessionId ?? session.Parent?.PersistenceId);
        EnsureRegistered(session);
        ClearLastError();
        RaiseChanged(DysonRuntimeChangeKind.SessionGraph, id);
        return Result<DysonAgentSession, string>.AsValue(session);
    }

    private void EnsureRegistered(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        RefreshRegistryKey(session);

        if (!_hookedSessions.TryAdd(session, 0))
        {
            RegisterSubSessions(session);
            return;
        }

        session.TurnAdded += OnTurnAdded;
        session.LogAppended += OnLogAppended;
        session.TodosChanged += OnTodosChanged;
        session.SubagentSpawned += OnSubagentSpawned;

        foreach (var turn in session.Turns)
            HookTurn(turn);

        RegisterSubSessions(session);
    }

    private void RegisterSubSessions(DysonAgentSession session)
    {
        foreach (var child in session.SubSessions)
        {
            RememberParentId(child, session.PersistenceId == Guid.Empty ? null : session.PersistenceId);
            EnsureRegistered(child);
        }
    }

    private void RefreshRegistryKey(DysonAgentSession session)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        _sessionsById[session.PersistenceId] = session;
        if (session.Parent?.PersistenceId is Guid parentId && parentId != Guid.Empty)
            _parentSessionIdByChild[session.PersistenceId] = parentId;
    }

    private void RememberParentId(DysonAgentSession session, Guid? parentSessionId)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        if (parentSessionId is Guid pid && pid != Guid.Empty)
            _parentSessionIdByChild[session.PersistenceId] = pid;
        else if (session.Parent?.PersistenceId is Guid live && live != Guid.Empty)
            _parentSessionIdByChild[session.PersistenceId] = live;
    }

    private async Task UnregisterSessionTreeAsync(Guid rootPersistenceId)
    {
        if (_sessionsById.TryGetValue(rootPersistenceId, out var root))
            UnhookUnmappedDescendants(root);

        foreach (var hooked in _hookedSessions.Keys)
        {
            if (hooked.PersistenceId != Guid.Empty)
                continue;
            if (IsLiveDescendantOf(hooked, rootPersistenceId))
                UnhookSession(hooked);
        }

        var toRemove = CollectMappedDescendantIds(rootPersistenceId);
        foreach (var id in toRemove)
            await UnregisterSessionAsync(id).ConfigureAwait(false);
    }

    private HashSet<Guid> CollectMappedDescendantIds(Guid rootPersistenceId)
    {
        var ids = new HashSet<Guid> { rootPersistenceId };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var kv in _sessionsById)
            {
                if (ids.Contains(kv.Key))
                    continue;

                var parentId = kv.Value.Parent?.PersistenceId ?? Guid.Empty;
                if (parentId != Guid.Empty && ids.Contains(parentId))
                {
                    ids.Add(kv.Key);
                    grew = true;
                    continue;
                }

                if (_parentSessionIdByChild.TryGetValue(kv.Key, out var mapped) && ids.Contains(mapped))
                {
                    ids.Add(kv.Key);
                    grew = true;
                }
            }
        }

        return ids;
    }

    private static bool IsLiveDescendantOf(DysonAgentSession session, Guid rootPersistenceId)
    {
        for (var current = session; current is not null; current = current.Parent)
        {
            if (current.PersistenceId == rootPersistenceId)
                return true;
        }

        return false;
    }

    private void UnhookUnmappedDescendants(DysonAgentSession session)
    {
        foreach (var child in session.SubSessions)
        {
            UnhookUnmappedDescendants(child);
            if (child.PersistenceId == Guid.Empty)
                UnhookSession(child);
        }
    }

    private async Task UnregisterSessionAsync(Guid persistenceId)
    {
        _busySessions.TryRemove(persistenceId, out _);
        if (ClearPromptQueue(persistenceId))
            RaiseChanged(DysonRuntimeChangeKind.Queue, persistenceId);
        _parentSessionIdByChild.TryRemove(persistenceId, out _);
        CancelPromptTokens(persistenceId);

        if (_sessionsById.TryRemove(persistenceId, out var session))
            UnhookSession(session);

        if (_leasesById.TryRemove(persistenceId, out var lease))
            await lease.DisposeAsync().ConfigureAwait(false);
    }

    private void UnhookSession(DysonAgentSession session)
    {
        if (!_hookedSessions.TryRemove(session, out _))
            return;

        session.TurnAdded -= OnTurnAdded;
        session.LogAppended -= OnLogAppended;
        session.TodosChanged -= OnTodosChanged;
        session.SubagentSpawned -= OnSubagentSpawned;

        foreach (var turn in session.Turns)
            UnhookTurn(turn);
    }

    private void HookTurn(DysonAgentTurn turn)
    {
        EventHandler<DysonToolCallStatusChangedEventArgs> toolHandler = (_, args) =>
            QueuePersist(() => OnToolStatusAsync(turn, args));

        if (_toolHandlers.TryAdd(turn.Id, toolHandler))
            turn.ToolCallStatusChanged += toolHandler;
    }

    private void UnhookTurn(DysonAgentTurn turn)
    {
        if (_toolHandlers.TryRemove(turn.Id, out var toolHandler))
            turn.ToolCallStatusChanged -= toolHandler;
    }

    private void OnTurnAdded(object? sender, DysonAgentTurn turn)
    {
        if (sender is not DysonAgentSession session)
            return;

        RefreshRegistryKey(session);
        HookTurn(turn);
        QueuePersist(() => PersistTurnStartedAsync(session, turn));
        RaiseChanged(DysonRuntimeChangeKind.SessionGraph, session.PersistenceId);
    }

    private void OnLogAppended(object? sender, string line)
    {
        if (sender is not DysonAgentSession session || session.PersistenceId == Guid.Empty)
            return;

        RefreshRegistryKey(session);

        var entry = DysonSessionLogPayload.CreateEntry(
            session.PersistenceId,
            DysonSessionLogKind.LogLine,
            new DysonSessionLogLogLine(line));

        QueuePersist(() => PersistAsync(() => _sessions.AppendLogAsync(entry)));
        RaiseChanged(DysonRuntimeChangeKind.SessionGraph, session.PersistenceId);
    }

    private void OnTodosChanged(object? sender, EventArgs e)
    {
        if (sender is not DysonAgentSession session)
            return;

        RefreshRegistryKey(session);
        RaiseChanged(DysonRuntimeChangeKind.SessionGraph, session.PersistenceId);
    }

    private void OnSubagentSpawned(object? sender, DysonAgentSession child)
    {
        if (sender is not DysonAgentSession parent)
            return;

        RememberParentId(child, parent.PersistenceId == Guid.Empty ? null : parent.PersistenceId);
        EnsureRegistered(child);
        if (child.PersistenceId == Guid.Empty)
            _ = EnsureChildRegistryKeyAsync(child);
        else
            EnsureSessionMapped(child);
        RaiseChanged(
            DysonRuntimeChangeKind.SessionGraph,
            child.PersistenceId == Guid.Empty ? parent.PersistenceId : child.PersistenceId);
    }

    private async Task EnsureChildRegistryKeyAsync(DysonAgentSession child)
    {
        for (var i = 0; i < 40; i++)
        {
            if (_disposed != 0 || !_hookedSessions.ContainsKey(child))
                return;

            EnsureSessionMapped(child);
            if (child.PersistenceId != Guid.Empty)
            {
                RaiseChanged(DysonRuntimeChangeKind.SessionGraph, child.PersistenceId);
                return;
            }

            try
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }
    }

    private void EnsureSessionMapped(DysonAgentSession session)
    {
        if (session.PersistenceId == Guid.Empty || !_hookedSessions.ContainsKey(session))
            return;

        RememberParentId(session, session.Parent?.PersistenceId);
        RefreshRegistryKey(session);
    }

    private async Task PersistTurnStartedAsync(DysonAgentSession session, DysonAgentTurn turn)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        var sessionId = session.PersistenceId;
        var sequence = IndexOfTurn(session, turn);
        if (sequence < 0)
            sequence = Math.Max(0, session.Turns.Count - 1);

        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        var upsert = await PersistAsync(() => _sessions.UpsertTurnAsync(entity)).ConfigureAwait(false);
        if (upsert.IsError)
            return;

        var started = DysonTurnPersistence.CreateTurnStartedLog(sessionId, turn);
        await PersistAsync(() => _sessions.AppendLogAsync(started)).ConfigureAwait(false);
    }

    private async Task OnToolStatusAsync(
        DysonAgentTurn turn,
        DysonToolCallStatusChangedEventArgs args)
    {
        var session = FindSessionOwningTurn(turn);
        if (session is null || session.PersistenceId == Guid.Empty)
            return;

        var sessionId = session.PersistenceId;
        var kind = DysonTurnPersistence.LogKindForToolStatus(args.NewStatus);
        if (kind is DysonSessionLogKind logKind)
        {
            var log = DysonTurnPersistence.CreateToolCallLog(
                sessionId,
                turn.Id,
                args.Tracked,
                logKind);
            await PersistAsync(() => _sessions.AppendLogAsync(log)).ConfigureAwait(false);
        }

        var sequence = IndexOfTurn(session, turn);
        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        await PersistAsync(() => _sessions.UpsertTurnAsync(entity)).ConfigureAwait(false);
        RaiseChanged(DysonRuntimeChangeKind.SessionGraph, sessionId);
    }

    private DysonAgentSession? FindSessionOwningTurn(DysonAgentTurn turn)
    {
        foreach (var session in _sessionsById.Values)
        {
            if (IndexOfTurn(session, turn) >= 0)
                return session;
        }

        foreach (var session in _hookedSessions.Keys)
        {
            if (IndexOfTurn(session, turn) >= 0)
                return session;
        }

        return null;
    }

    private static int IndexOfTurn(DysonAgentSession session, DysonAgentTurn turn)
    {
        for (var i = 0; i < session.Turns.Count; i++)
        {
            if (session.Turns[i].Id == turn.Id)
                return i;
        }

        return -1;
    }

    private void QueuePersist(Func<Task> action)
    {
        if (_disposed != 0)
            return;

        var id = Interlocked.Increment(ref _persistSeq);
        var task = PersistQueuedAsync(action);
        _persistTasks[id] = task;
        _ = task.ContinueWith(
            static (completed, state) =>
            {
                if (state is not ValueTuple<ConcurrentDictionary<int, Task>, int> boxed)
                    return;

                boxed.Item1.TryRemove(boxed.Item2, out _);
                _ = completed.Exception;
            },
            (_persistTasks, id),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task PersistQueuedAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ponytail: persistence is best-effort until shutdown flush; do not crash the session graph
        }
    }

    private async Task<VoidResult<string>> PersistAsync(Func<Task<VoidResult<string>>> action)
    {
        if (_disposed != 0)
            return VoidResult<string>.Success;

        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return VoidResult<string>.Success;

            return await action().ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private bool ClearPromptQueue(Guid sessionId)
    {
        lock (_promptQueueGate)
            return _promptQueues.Remove(sessionId);
    }

    private static IReadOnlyList<string> CopyFilePaths(IReadOnlyList<string>? filePaths)
    {
        if (filePaths is null || filePaths.Count == 0)
            return [];

        var copy = new string[filePaths.Count];
        for (var i = 0; i < filePaths.Count; i++)
            copy[i] = filePaths[i];

        return Array.AsReadOnly(copy);
    }

    private Result<DysonAgentSession, string> FailSession(string error)
    {
        ReportError(error);
        return Result<DysonAgentSession, string>.AsError(error);
    }

    private VoidResult<string> FailVoid(string error)
    {
        ReportError(error);
        return VoidResult<string>.AsError(error);
    }

    private void RaiseChanged(DysonRuntimeChangeKind kind, Guid? sessionId = null)
    {
        if (_disposed != 0)
            return;

        var version = Interlocked.Increment(ref _changeVersion);
        Changed?.Invoke(this, new DysonRuntimeChange
        {
            SubjectId = _subjectId,
            SessionId = sessionId is Guid id && id != Guid.Empty ? id : null,
            Kind = kind,
            Version = version,
        });
    }
}
