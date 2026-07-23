using System.Collections.Concurrent;
using DysonHarness;

namespace Harness.UI.Demo;

/// <summary>
/// Scoped UI host: new/resume sessions, prompt forwarding, and persistence hooks.
/// </summary>
public sealed class DysonUiHost : IAsyncDisposable
{
    private readonly DysonSessionStore _sessions;
    private readonly DysonModelStore _models;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, EventHandler<DysonToolCallStatusChangedEventArgs>> _toolHandlers = new();

    private DemoDysonEngine? _engine;
    private DemoDysonAgentSession? _session;
    private bool _disposed;

    public DysonUiHost(DysonSessionStore sessions, DysonModelStore models)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    public DemoDysonEngine? Engine => _engine;
    public DemoDysonAgentSession? Session => _session;
    public Guid? ActiveSessionId => _session?.PersistenceId is { } id && id != Guid.Empty ? id : null;
    public string? LastError { get; private set; }
    public bool IsBusy { get; private set; }

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
                ProviderKind = "demo",
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
        CancellationToken cancellationToken = default) =>
        await _sessions.ListSessionsAsync(rootsOnly: true, cancellationToken).ConfigureAwait(false);

    public async Task<VoidResult<string>> StartNewSessionAsync(
        string agentMode = DysonAgentModes.Work,
        Guid? modelSlugId = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var providerResult = await ResolveProviderAsync(modelSlugId, cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
        {
            LastError = providerResult.Error;
            Notify();
            return new VoidResult<string>(providerResult.Error);
        }

        DetachSession();

        var created = await DemoDysonAgentSession.CreateAsync(
            _sessions,
            providerResult.Value,
            agentMode,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (created.IsError)
        {
            LastError = created.Error;
            Notify();
            return new VoidResult<string>(created.Error);
        }

        AttachSession(created.Value);
        Notify();
        return VoidResult<string>.Success;
    }

    public async Task<VoidResult<string>> ResumeSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

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

        DetachSession();

        var loaded = await DemoDysonAgentSession.LoadAsync(
            _sessions,
            sessionId,
            providerResult.Value,
            new DysonAgentSessionConfig { McpAccessMode = full.Value.Session.McpAccessMode },
            cancellationToken).ConfigureAwait(false);

        if (loaded.IsError)
        {
            LastError = loaded.Error;
            Notify();
            return new VoidResult<string>(loaded.Error);
        }

        AttachSession(loaded.Value);
        Notify();
        return VoidResult<string>.Success;
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

        IsBusy = true;
        LastError = null;
        Notify();

        try
        {
            var sessionId = _session.PersistenceId;
            var userLog = DysonSessionLogPayload.CreateEntry(
                sessionId,
                DysonSessionLogKind.UserPrompt,
                new DysonSessionLogUserPrompt(prompt));

            var appendUser = await PersistAsync(
                () => _sessions.AppendLogAsync(userLog, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (appendUser.IsError)
            {
                LastError = appendUser.Error;
                return appendUser;
            }

            if (_session.Turns.Count == 0)
            {
                var title = prompt.Trim();
                if (title.Length > 64)
                    title = title[..64] + "…";

                await PersistAsync(
                    () => _sessions.UpdateSessionMetaAsync(
                        new DysonSessionMetaUpdate
                        {
                            SessionId = sessionId,
                            Title = title,
                        },
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }

            var result = await _session.PromptAsync(prompt, cancellationToken).ConfigureAwait(false);
            if (result.IsError)
            {
                LastError = result.Error;
                return result;
            }

            var last = _session.Turns.Count > 0 ? _session.Turns[^1] : null;
            if (last is not null)
            {
                var complete = await PersistTurnCompletedAsync(last, cancellationToken)
                    .ConfigureAwait(false);
                if (complete.IsError)
                {
                    LastError = complete.Error;
                    return complete;
                }
            }

            return VoidResult<string>.Success;
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    private async Task<Result<DemoDysonAgentProvider, string>> ResolveProviderAsync(
        Guid? modelSlugId,
        CancellationToken cancellationToken)
    {
        DysonModelSlugEntity? slug = null;

        if (modelSlugId is Guid id)
        {
            var get = await _models.GetSlugAsync(id, cancellationToken).ConfigureAwait(false);
            if (get.IsError)
                return Result<DemoDysonAgentProvider, string>.AsError(get.Error);
            slug = get.Value;
        }
        else
        {
            var def = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
            if (def.IsError)
                return Result<DemoDysonAgentProvider, string>.AsError(def.Error);
            slug = def.Value;
        }

        return Result<DemoDysonAgentProvider, string>.AsValue(new DemoDysonAgentProvider(slug));
    }

    private void AttachSession(DemoDysonAgentSession session)
    {
        _session = session;
        _engine = new DemoDysonEngine(session);

        session.TurnAdded += OnTurnAdded;
        session.LogAppended += OnLogAppended;

        foreach (var turn in session.Turns)
            HookTurn(turn);
    }

    private void DetachSession()
    {
        if (_session is null)
            return;

        _session.TurnAdded -= OnTurnAdded;
        _session.LogAppended -= OnLogAppended;

        foreach (var turn in _session.Turns)
            UnhookTurn(turn);

        _session = null;
        _engine = null;
        _toolHandlers.Clear();
    }

    private void HookTurn(DysonAgentTurn turn)
    {
        EventHandler<DysonToolCallStatusChangedEventArgs> handler = (_, args) =>
            _ = OnToolStatusAsync(turn, args);

        if (_toolHandlers.TryAdd(turn.Id, handler))
            turn.ToolCallStatusChanged += handler;
    }

    private void UnhookTurn(DysonAgentTurn turn)
    {
        if (_toolHandlers.TryRemove(turn.Id, out var handler))
            turn.ToolCallStatusChanged -= handler;
    }

    private void OnTurnAdded(object? sender, DysonAgentTurn turn)
    {
        HookTurn(turn);
        _ = PersistTurnStartedAsync(turn);
        Notify();
    }

    private void OnLogAppended(object? sender, string line)
    {
        if (_session is null || _session.PersistenceId == Guid.Empty)
            return;

        var entry = DysonSessionLogPayload.CreateEntry(
            _session.PersistenceId,
            DysonSessionLogKind.LogLine,
            new DysonSessionLogLogLine(line));

        _ = PersistAsync(() => _sessions.AppendLogAsync(entry), CancellationToken.None);
        Notify();
    }

    private async Task PersistTurnStartedAsync(DysonAgentTurn turn)
    {
        if (_session is null || _session.PersistenceId == Guid.Empty)
            return;

        var sessionId = _session.PersistenceId;
        var sequence = _session.Turns.ToList().FindIndex(t => t.Id == turn.Id);
        if (sequence < 0)
            sequence = _session.Turns.Count - 1;

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
        if (_session is null || _session.PersistenceId == Guid.Empty)
            return;

        var sessionId = _session.PersistenceId;
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

        var sequence = IndexOfTurn(turn);
        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        await PersistAsync(() => _sessions.UpsertTurnAsync(entity), CancellationToken.None)
            .ConfigureAwait(false);

        Notify();
    }

    private async Task<VoidResult<string>> PersistTurnCompletedAsync(
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        if (_session is null || _session.PersistenceId == Guid.Empty)
            return VoidResult<string>.Success;

        var sessionId = _session.PersistenceId;
        var sequence = IndexOfTurn(turn);
        var entity = DysonTurnPersistence.ToEntity(
            turn,
            sessionId,
            sequence,
            completedUtc: DateTimeOffset.UtcNow);

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

    private int IndexOfTurn(DysonAgentTurn turn)
    {
        if (_session is null)
            return 0;

        for (var i = 0; i < _session.Turns.Count; i++)
        {
            if (_session.Turns[i].Id == turn.Id)
                return i;
        }

        return Math.Max(0, _session.Turns.Count - 1);
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
        DetachSession();
        _persistGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
