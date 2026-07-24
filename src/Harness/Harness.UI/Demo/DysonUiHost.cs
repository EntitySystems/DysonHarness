using System.Collections.Concurrent;
using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>
/// Scoped UI host: new/resume sessions, prompt forwarding, live session registry,
/// parent/child navigation, and FIFO auto-Prompt on subagent report interrupts.
/// Branches on <see cref="DysonProviderKinds"/> for demo vs OpenAI-compatible sessions.
/// </summary>
public sealed class DysonUiHost : IAsyncDisposable
{
    private readonly DysonSessionStore _sessions;
    private readonly DysonModelStore _models;
    private readonly DysonWorkDirectoryStore _workDirectories;
    private readonly DysonAppSettingsStore _appSettings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DysonAgentSession> _sessionsById = new();
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _hookedSessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _busySessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _promptGates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _promptCtsBySession = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<DysonAgentInterrupt>> _pendingReportsByParent = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _autoTurnGates = new();
    private readonly ConcurrentDictionary<Guid, EventHandler<DysonToolCallStatusChangedEventArgs>> _toolHandlers = new();
    private readonly ConcurrentDictionary<Guid, EventHandler> _textHandlers = new();
    private readonly ConcurrentDictionary<Guid, StreamingNotifyState> _streamingNotify = new();
    private readonly ConcurrentDictionary<Guid, Guid?> _parentSessionIdByChild = new();

    private DemoDysonEngine? _engine;
    private DysonAgentSession? _session;
    private bool _disposed;

    static DysonUiHost() => DysonSubagentHostLogic.RunSelfCheck();

    public DysonUiHost(
        DysonSessionStore sessions,
        DysonModelStore models,
        DysonWorkDirectoryStore workDirectories,
        DysonAppSettingsStore appSettings,
        HttpClient http)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _workDirectories = workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public DemoDysonEngine? Engine => _engine;
    public DysonAgentSession? Session => _session;
    public Guid? ActiveSessionId => _session?.PersistenceId is { } id && id != Guid.Empty ? id : null;

    /// <summary>Parent persistence id for the focused session (live <see cref="DysonAgentSession.Parent"/> or DB).</summary>
    public Guid? ActiveParentSessionId
    {
        get
        {
            if (_session?.Parent?.PersistenceId is Guid live && live != Guid.Empty)
                return live;

            if (_session?.PersistenceId is Guid childId
                && _parentSessionIdByChild.TryGetValue(childId, out var stored)
                && stored is Guid pid
                && pid != Guid.Empty)
            {
                return pid;
            }

            return null;
        }
    }

    public string? LastError { get; private set; }

    /// <summary>True when the focused session has an in-flight host <see cref="PromptAsync"/>.</summary>
    public bool IsBusy =>
        ActiveSessionId is Guid id && _busySessions.ContainsKey(id);

    public event Action? Changed;

    public async Task<VoidResult<string>> EnsureDefaultModelAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsError)
            return new VoidResult<string>(existing.Error);

        if (existing.Value is not null)
            return VoidResult<string>.Success;

        var createProvider = await _models.CreateProviderAsync(
            new DysonModelProviderEntity
            {
                DisplayName = "Demo Mock",
                ProviderKind = DysonProviderKinds.Demo,
            },
            cancellationToken).ConfigureAwait(false);

        if (createProvider.IsError)
            return new VoidResult<string>(createProvider.Error);

        var addSlug = await _models.AddSlugAsync(
            createProvider.Value,
            slug: "demo-mock",
            displayAlias: "Demo Mock",
            isDefault: true,
            cancellationToken).ConfigureAwait(false);

        return addSlug.IsError
            ? new VoidResult<string>(addSlug.Error)
            : VoidResult<string>.Success;
    }

    public async Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default) =>
        await _sessions.ListSessionsAsync(
            workDirectoryId: workDirectoryId,
            rootsOnly: true,
            cancellationToken).ConfigureAwait(false);

    public async Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (ActiveSessionId == sessionId)
        {
            CancelPrompt();
            ClearFocus();
        }

        UnregisterSessionTree(sessionId);

        var deleted = await _sessions.DeleteSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (deleted.IsError)
        {
            LastError = deleted.Error;
            Notify();
            return deleted;
        }

        Notify();
        return VoidResult<string>.Success;
    }

    public Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListWorkDirectoriesAsync(
        CancellationToken cancellationToken = default) =>
        _workDirectories.ListAsync(cancellationToken);

    public async Task<VoidResult<string>> StartNewSessionAsync(
        string agentMode = DysonAgentModes.Work,
        Guid? modelSlugId = null,
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (workDirectoryId is null || workDirectoryId == Guid.Empty)
        {
            LastError = "Select a work directory before creating a session.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        var workDir = await _workDirectories.GetAsync(workDirectoryId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (workDir.IsError)
        {
            LastError = workDir.Error;
            Notify();
            return new VoidResult<string>(workDir.Error);
        }

        var providerResult = await ResolveProviderAsync(modelSlugId, cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
        {
            LastError = providerResult.Error;
            Notify();
            return new VoidResult<string>(providerResult.Error);
        }

        var kind = providerResult.Value.Kind;
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            var config = await BuildSessionConfigAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var created = await OpenAiCompatibleAgentSession.CreateAsync(
                _sessions,
                providerResult.Value.OpenAi!,
                _http,
                workDirectoryId.Value,
                workDir.Value.AbsolutePath,
                agentMode,
                config: config,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (created.IsError)
            {
                LastError = created.Error;
                Notify();
                return new VoidResult<string>(created.Error);
            }

            FocusSession(created.Value, parentSessionId: null);
        }
        else
        {
            var created = await DemoDysonAgentSession.CreateAsync(
                _sessions,
                providerResult.Value.Demo!,
                workDirectoryId.Value,
                agentMode,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (created.IsError)
            {
                LastError = created.Error;
                Notify();
                return new VoidResult<string>(created.Error);
            }

            FocusSession(created.Value, parentSessionId: null);
        }

        Notify();
        return VoidResult<string>.Success;
    }

    public async Task<VoidResult<string>> ResumeSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_sessionsById.TryGetValue(sessionId, out var live))
        {
            FocusSession(live, ResolveStoredParentId(live));
            Notify();
            return VoidResult<string>.Success;
        }

        return await LoadAndFocusSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Switch UI focus to a live or persisted session without disposing other registry entries.</summary>
    public Task<VoidResult<string>> NavigateToSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        ResumeSessionAsync(sessionId, cancellationToken);

    /// <summary>Focus the parent of the active session (live <see cref="DysonAgentSession.Parent"/> or DB).</summary>
    public async Task<VoidResult<string>> NavigateToParentAsync(
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_session is null)
        {
            LastError = "No active session.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        var parentId = ActiveParentSessionId;
        if (parentId is null)
        {
            LastError = "Active session has no parent.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        return await NavigateToSessionAsync(parentId.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Live card snapshot for a child persistence id. Null when the child is not in the host registry.
    /// Child status is persisted by the engine (<c>UpdateSessionMetaAsync</c>) on report/stop.
    /// </summary>
    public DysonSubagentCardState? GetSubagentCardState(Guid persistenceId)
    {
        if (persistenceId == Guid.Empty)
            return null;

        if (!_sessionsById.TryGetValue(persistenceId, out var session))
            return null;

        var latest = session.Turns.Count > 0 ? session.Turns[^1] : null;
        return new DysonSubagentCardState
        {
            PersistenceId = persistenceId,
            Title = session.DisplayTitle,
            LatestTurnAgentTitle = latest?.AgentTitle,
            IsRunning = DysonSubagentHostLogic.IsRunning(session.Status, latest),
            Status = session.Status,
        };
    }

    /// <summary>Cancels the in-flight <see cref="PromptAsync"/> for the focused session when busy.</summary>
    public void CancelPrompt()
    {
        if (ActiveSessionId is not Guid id)
            return;

        if (_promptCtsBySession.TryGetValue(id, out var cts))
            cts.Cancel();
    }

    public async Task<VoidResult<string>> PromptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            LastError = "No active session.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            LastError = "Prompt is empty.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        var result = await PromptOnSessionAsync(_session, prompt, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            LastError = result.Error;

        Notify();
        return result;
    }

    private async Task<VoidResult<string>> LoadAndFocusSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var full = await _sessions.GetFullSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (full.IsError)
        {
            LastError = full.Error;
            Notify();
            return new VoidResult<string>(full.Error);
        }

        var providerResult = await ResolveProviderAsync(full.Value.Session.ModelSlugId, cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
        {
            LastError = providerResult.Error;
            Notify();
            return new VoidResult<string>(providerResult.Error);
        }

        string? workPath = null;
        if (full.Value.Session.WorkDirectoryId is Guid wdId)
        {
            var wd = await _workDirectories.GetAsync(wdId, cancellationToken).ConfigureAwait(false);
            if (wd.IsError)
            {
                LastError = wd.Error;
                Notify();
                return new VoidResult<string>(wd.Error);
            }

            workPath = wd.Value.AbsolutePath;
        }

        var kind = providerResult.Value.Kind;
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(workPath))
            {
                LastError = "Session has no work directory; cannot resume OpenAI-compatible session.";
                Notify();
                return new VoidResult<string>(LastError);
            }

            var loaded = await OpenAiCompatibleAgentSession.LoadAsync(
                _sessions,
                sessionId,
                providerResult.Value.OpenAi!,
                _http,
                workPath,
                await BuildSessionConfigAsync(full.Value.Session.McpAccessMode, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            if (loaded.IsError)
            {
                LastError = loaded.Error;
                Notify();
                return new VoidResult<string>(loaded.Error);
            }

            FocusSession(loaded.Value, full.Value.Session.ParentSessionId);
        }
        else
        {
            var loaded = await DemoDysonAgentSession.LoadAsync(
                _sessions,
                sessionId,
                providerResult.Value.Demo!,
                new DysonAgentSessionConfig { McpAccessMode = full.Value.Session.McpAccessMode },
                cancellationToken).ConfigureAwait(false);

            if (loaded.IsError)
            {
                LastError = loaded.Error;
                Notify();
                return new VoidResult<string>(loaded.Error);
            }

            FocusSession(loaded.Value, full.Value.Session.ParentSessionId);
        }

        Notify();
        return VoidResult<string>.Success;
    }

    private sealed record ResolvedProvider(
        string Kind,
        DemoDysonAgentProvider? Demo,
        OpenAiCompatibleAgentProvider? OpenAi);

    private async Task<DysonAgentSessionConfig> BuildSessionConfigAsync(
        DysonMcpAccessMode? mcpAccessMode = null,
        CancellationToken cancellationToken = default)
    {
        var config = new DysonAgentSessionConfig();
        if (mcpAccessMode is { } mode)
            config.McpAccessMode = mode;

        var setting = await _appSettings
            .GetAsync(DysonAppSettingKeys.WebSearchSummarizerModelSlugId, cancellationToken)
            .ConfigureAwait(false);

        if (setting.IsError || string.IsNullOrWhiteSpace(setting.Value))
            return config;

        if (!Guid.TryParse(setting.Value, out var slugId) || slugId == Guid.Empty)
            return config;

        var slugResult = await _models.GetSlugAsync(slugId, cancellationToken).ConfigureAwait(false);
        if (slugResult.IsError || slugResult.Value is null)
            return config;

        var provider = slugResult.Value.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);

        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
            config.SummarizerProvider = new OpenAiCompatibleAgentProvider(slugResult.Value);

        return config;
    }

    private async Task<Result<ResolvedProvider, string>> ResolveProviderAsync(
        Guid? modelSlugId,
        CancellationToken cancellationToken)
    {
        DysonModelSlugEntity? slug = null;

        if (modelSlugId is Guid id)
        {
            var get = await _models.GetSlugAsync(id, cancellationToken).ConfigureAwait(false);
            if (get.IsError)
                return Result<ResolvedProvider, string>.AsError(get.Error);
            slug = get.Value;
        }
        else
        {
            var def = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
            if (def.IsError)
                return Result<ResolvedProvider, string>.AsError(def.Error);
            slug = def.Value;
        }

        var provider = slug?.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);

        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            return Result<ResolvedProvider, string>.AsValue(
                new ResolvedProvider(kind, null, new OpenAiCompatibleAgentProvider(slug)));
        }

        return Result<ResolvedProvider, string>.AsValue(
            new ResolvedProvider(DysonProviderKinds.Demo, new DemoDysonAgentProvider(slug), null));
    }

    private void FocusSession(DysonAgentSession session, Guid? parentSessionId)
    {
        EnsureRegistered(session);
        RememberParentId(session, parentSessionId ?? session.Parent?.PersistenceId);

        _session = session;
        _engine = new DemoDysonEngine(session);
    }

    private void ClearFocus()
    {
        _session = null;
        _engine = null;
    }

    private Guid? ResolveStoredParentId(DysonAgentSession session)
    {
        if (session.Parent?.PersistenceId is Guid live && live != Guid.Empty)
            return live;

        if (session.PersistenceId != Guid.Empty
            && _parentSessionIdByChild.TryGetValue(session.PersistenceId, out var stored))
        {
            return stored;
        }

        return null;
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
        session.SessionRenamed += OnSessionRenamed;
        session.SubagentSpawned += OnSubagentSpawned;
        session.InterruptEnqueued += OnInterruptEnqueued;

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

    private void UnregisterSessionTree(Guid rootPersistenceId)
    {
        var toRemove = _sessionsById
            .Where(kv =>
                kv.Key == rootPersistenceId
                || kv.Value.Parent?.PersistenceId == rootPersistenceId
                || (_parentSessionIdByChild.TryGetValue(kv.Key, out var p) && p == rootPersistenceId))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
            UnregisterSession(id);
    }

    private void UnregisterSession(Guid persistenceId)
    {
        _pendingReportsByParent.TryRemove(persistenceId, out _);
        _busySessions.TryRemove(persistenceId, out _);
        _parentSessionIdByChild.TryRemove(persistenceId, out _);

        if (_autoTurnGates.TryRemove(persistenceId, out var gate))
            gate.Dispose();

        if (_promptGates.TryRemove(persistenceId, out var promptGate))
            promptGate.Dispose();

        if (_promptCtsBySession.TryRemove(persistenceId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (!_sessionsById.TryRemove(persistenceId, out var session))
            return;

        UnhookSession(session);
    }

    private void UnhookSession(DysonAgentSession session)
    {
        if (!_hookedSessions.TryRemove(session, out _))
            return;

        session.TurnAdded -= OnTurnAdded;
        session.LogAppended -= OnLogAppended;
        session.SessionRenamed -= OnSessionRenamed;
        session.SubagentSpawned -= OnSubagentSpawned;
        session.InterruptEnqueued -= OnInterruptEnqueued;

        foreach (var turn in session.Turns)
            UnhookTurn(turn);
    }

    private void UnhookAllSessions()
    {
        foreach (var session in _hookedSessions.Keys.ToArray())
            UnhookSession(session);

        _sessionsById.Clear();
        _parentSessionIdByChild.Clear();
        _pendingReportsByParent.Clear();
        _busySessions.Clear();

        foreach (var cts in _promptCtsBySession.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }

        _promptCtsBySession.Clear();

        foreach (var gate in _autoTurnGates.Values)
            gate.Dispose();
        _autoTurnGates.Clear();

        foreach (var gate in _promptGates.Values)
            gate.Dispose();
        _promptGates.Clear();

        _toolHandlers.Clear();
        _textHandlers.Clear();
        _streamingNotify.Clear();
    }

    private void HookTurn(DysonAgentTurn turn)
    {
        EventHandler<DysonToolCallStatusChangedEventArgs> toolHandler = (_, args) =>
            _ = OnToolStatusAsync(turn, args);

        if (_toolHandlers.TryAdd(turn.Id, toolHandler))
            turn.ToolCallStatusChanged += toolHandler;

        EventHandler textHandler = (_, _) =>
        {
            // Final handoff / clear: flush immediately so Markdig replaces preview without throttle lag.
            if (!turn.IsStreaming)
            {
                FlushNotifyForTurn(turn.Id);
                // Background child PromptAsync bypasses host — persist completion when streaming ends.
                _ = PersistTurnCompletedIfNeededAsync(turn);
            }
            else
                ThrottledNotifyForTurn(turn.Id);
        };
        if (_textHandlers.TryAdd(turn.Id, textHandler))
            turn.AssistantTextChanged += textHandler;
    }

    private void UnhookTurn(DysonAgentTurn turn)
    {
        if (_toolHandlers.TryRemove(turn.Id, out var toolHandler))
            turn.ToolCallStatusChanged -= toolHandler;

        if (_textHandlers.TryRemove(turn.Id, out var textHandler))
            turn.AssistantTextChanged -= textHandler;

        _streamingNotify.TryRemove(turn.Id, out _);
    }

    private void FlushNotifyForTurn(Guid turnId)
    {
        var state = _streamingNotify.GetOrAdd(turnId, _ => new StreamingNotifyState());
        lock (state.Lock)
        {
            state.Pending = false;
            state.LastNotifyTicks = Environment.TickCount64;
        }

        Notify();
    }

    private void ThrottledNotifyForTurn(Guid turnId)
    {
        const int intervalMs = 75;
        var state = _streamingNotify.GetOrAdd(turnId, _ => new StreamingNotifyState());

        lock (state.Lock)
        {
            var now = Environment.TickCount64;
            var elapsed = now - state.LastNotifyTicks;
            if (elapsed >= intervalMs)
            {
                state.LastNotifyTicks = now;
                state.Pending = false;
                Notify();
                return;
            }

            if (state.Pending)
                return;

            state.Pending = true;
            var delayMs = (int)(intervalMs - elapsed);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                lock (state.Lock)
                {
                    if (!state.Pending)
                        return;

                    state.Pending = false;
                    state.LastNotifyTicks = Environment.TickCount64;
                }

                Notify();
            });
        }
    }

    private sealed class StreamingNotifyState
    {
        public long LastNotifyTicks;
        public bool Pending;
        public object Lock = new();
    }

    private void OnSubagentSpawned(object? sender, DysonAgentSession child)
    {
        if (sender is DysonAgentSession parent)
            RememberParentId(child, parent.PersistenceId == Guid.Empty ? null : parent.PersistenceId);

        EnsureRegistered(child);
        // PersistenceId is assigned after SubagentSpawned in CreateChildAsync — refresh on a short poll.
        _ = EnsureChildRegistryKeyAsync(child);
        Notify();
    }

    private async Task EnsureChildRegistryKeyAsync(DysonAgentSession child)
    {
        for (var i = 0; i < 40; i++)
        {
            RefreshRegistryKey(child);
            if (child.PersistenceId != Guid.Empty)
            {
                Notify();
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

    private void OnInterruptEnqueued(object? sender, DysonAgentInterrupt interrupt)
    {
        if (sender is not DysonAgentSession parent)
            return;

        RefreshRegistryKey(parent);

        if (interrupt.Kind is not (
            DysonAgentInterruptKind.SubagentCompleted
            or DysonAgentInterruptKind.SubagentFailed
            or DysonAgentInterruptKind.SubagentStopped))
        {
            return;
        }

        if (parent.PersistenceId == Guid.Empty)
            return;

        var queue = _pendingReportsByParent.GetOrAdd(
            parent.PersistenceId,
            _ => new ConcurrentQueue<DysonAgentInterrupt>());
        queue.Enqueue(interrupt);
        Notify();
        _ = DrainAutoTurnsAsync(parent.PersistenceId);
    }

    private async Task DrainAutoTurnsAsync(Guid parentPersistenceId)
    {
        var gate = _autoTurnGates.GetOrAdd(parentPersistenceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            while (true)
            {
                if (!_pendingReportsByParent.TryGetValue(parentPersistenceId, out var queue)
                    || !queue.TryDequeue(out var interrupt))
                {
                    break;
                }

                if (!_sessionsById.TryGetValue(parentPersistenceId, out var parent))
                    break;

                string? title = null;
                if (interrupt.PersistenceId is Guid childId
                    && childId != Guid.Empty
                    && _sessionsById.TryGetValue(childId, out var child))
                {
                    title = child.DisplayTitle;
                }
                else if (parent.TryGetSubagent(interrupt.SubagentId, out var byRuntime))
                {
                    title = byRuntime.DisplayTitle;
                }

                var prompt = DysonSubagentHostLogic.BuildSubagentReportContinuationPrompt(interrupt, title);
                var result = await PromptOnSessionAsync(parent, prompt, CancellationToken.None)
                    .ConfigureAwait(false);
                if (result.IsError)
                {
                    LastError = result.Error;
                    Notify();
                    break;
                }
            }
        }
        finally
        {
            gate.Release();
        }

        // Race: interrupt may enqueue after last empty check while gate was held.
        if (_pendingReportsByParent.TryGetValue(parentPersistenceId, out var leftover)
            && !leftover.IsEmpty)
        {
            _ = DrainAutoTurnsAsync(parentPersistenceId);
        }
    }

    private async Task<VoidResult<string>> PromptOnSessionAsync(
        DysonAgentSession session,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty)
            return new VoidResult<string>("Session is not persisted.");

        var sessionId = session.PersistenceId;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _promptCtsBySession[sessionId] = linked;
        var token = linked.Token;
        var promptGate = _promptGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

        try
        {
            try
            {
                await promptGate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new VoidResult<string>("Prompt was cancelled.");
            }

            _busySessions[sessionId] = 0;
            Notify();

            try
            {
                var userLog = DysonSessionLogPayload.CreateEntry(
                    sessionId,
                    DysonSessionLogKind.UserPrompt,
                    new DysonSessionLogUserPrompt(prompt));

                var appendUser = await PersistAsync(
                    () => _sessions.AppendLogAsync(userLog, token),
                    token).ConfigureAwait(false);
                if (appendUser.IsError)
                    return appendUser;

                var result = await session.PromptAsync(prompt, token).ConfigureAwait(false);
                if (result.IsError)
                    return result;

                var last = session.Turns.Count > 0 ? session.Turns[^1] : null;
                if (last is not null)
                {
                    var complete = await PersistTurnCompletedAsync(session, last, token)
                        .ConfigureAwait(false);
                    if (complete.IsError)
                        return complete;
                }

                return VoidResult<string>.Success;
            }
            finally
            {
                _busySessions.TryRemove(sessionId, out _);
                promptGate.Release();
                Notify();
            }
        }
        finally
        {
            if (_promptCtsBySession.TryRemove(sessionId, out var cts))
                cts.Dispose();

            linked.Dispose();
            _ = DrainAutoTurnsAsync(sessionId);
        }
    }

    private async Task PersistTurnCompletedIfNeededAsync(DysonAgentTurn turn)
    {
        if (turn.CompletedUtc is not null)
            return;

        var session = FindSessionOwningTurn(turn);
        if (session is null || session.PersistenceId == Guid.Empty)
            return;

        // Host-owned PromptOnSessionAsync persists after PromptAsync returns.
        if (_busySessions.ContainsKey(session.PersistenceId))
            return;

        await PersistTurnCompletedAsync(session, turn, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnTurnAdded(object? sender, DysonAgentTurn turn)
    {
        if (sender is not DysonAgentSession session)
            return;

        RefreshRegistryKey(session);
        HookTurn(turn);
        _ = PersistTurnStartedAsync(session, turn);
        Notify();
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

        _ = PersistAsync(() => _sessions.AppendLogAsync(entry), CancellationToken.None);
        Notify();
    }

    private void OnSessionRenamed(object? sender, DysonSessionRenamedEventArgs args)
    {
        if (sender is DysonAgentSession session)
            RefreshRegistryKey(session);

        Notify();
    }

    private async Task PersistTurnStartedAsync(DysonAgentSession session, DysonAgentTurn turn)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        var sessionId = session.PersistenceId;
        var sequence = IndexOfTurn(session, turn);
        if (sequence < 0)
            sequence = session.Turns.Count - 1;

        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        await PersistAsync(() => _sessions.UpsertTurnAsync(entity), CancellationToken.None)
            .ConfigureAwait(false);

        var started = DysonTurnPersistence.CreateTurnStartedLog(sessionId, turn);
        await PersistAsync(() => _sessions.AppendLogAsync(started), CancellationToken.None)
            .ConfigureAwait(false);

        Notify();
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
            await PersistAsync(() => _sessions.AppendLogAsync(log), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var sequence = IndexOfTurn(session, turn);
        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        await PersistAsync(() => _sessions.UpsertTurnAsync(entity), CancellationToken.None)
            .ConfigureAwait(false);

        Notify();
    }

    private DysonAgentSession? FindSessionOwningTurn(DysonAgentTurn turn)
    {
        if (_session is not null && IndexOfTurn(_session, turn) >= 0)
            return _session;

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

        var upsert = await PersistAsync(
            () => _sessions.UpsertTurnAsync(entity, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (upsert.IsError)
            return upsert;

        var reply = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.AgentReply,
            new DysonSessionLogAgentReply(turn.Id, turn.AgentTitle, turn.AssistantText ?? ""),
            turnId: turn.Id);

        var appendReply = await PersistAsync(
            () => _sessions.AppendLogAsync(reply, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (appendReply.IsError)
            return appendReply;

        var completed = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.TurnCompleted,
            new DysonSessionLogTurnCompleted(turn.Id, turn.Kind, turn.AgentTitle),
            turnId: turn.Id);

        return await PersistAsync(
            () => _sessions.AppendLogAsync(completed, cancellationToken),
            cancellationToken).ConfigureAwait(false);
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

    private async Task<VoidResult<string>> PersistAsync(
        Func<Task<VoidResult<string>>> action,
        CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        ClearFocus();
        UnhookAllSessions();
        _persistGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
