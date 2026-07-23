using System.Collections.Concurrent;

namespace DysonHarness;

public abstract class DysonAgentSession
{
    private int _nextSubagentId;
    private readonly ConcurrentQueue<DysonAgentInterrupt> _interrupts = new();
    private readonly SemaphoreSlim _interruptSignal = new(0);
    private readonly ConcurrentQueue<string> _logLines = new();

    protected DysonAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        DysonAgentProvider provider)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        SubSessions = new List<DysonAgentSession>();

        var prompt = DysonAgentSystemPrompts.ForMode(agentMode, config.CustomAgents);
        if (prompt.IsError)
            throw new ArgumentOutOfRangeException(nameof(agentMode), agentMode, prompt.Error);

        Mode = agentMode;
        SystemPrompt = prompt.Value;
        McpPipeline = DysonMcpPipeline.CreateDefault(config.McpAccessMode);
    }

    /// <summary>Session identity. Root sessions are 0; subagents are allocated from 1.</summary>
    public int Id { get; protected set; }

    /// <summary>Durable SQLite session id (distinct from runtime <see cref="Id"/>).</summary>
    public Guid PersistenceId { get; protected set; }

    /// <summary>UI/list title mirrored from persisted <c>sessions.Title</c>.</summary>
    public string? DisplayTitle { get; protected set; }

    public DysonAgentSessionConfig Config { get; }

    public string Mode { get; }

    public string SystemPrompt { get; }

    public DysonMcpPipeline McpPipeline { get; }

    public DysonAgentProvider Provider
    {
        get => field;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IList<DysonAgentSession> SubSessions { get; }

    /// <summary>Parent lookup for Wait/Inspect/Stop. Keyed by subagent Id.</summary>
    protected Dictionary<int, DysonAgentSession> SubagentsById { get; } = new();

    protected int AllocateSubagentId() => Interlocked.Increment(ref _nextSubagentId);

    /// <summary>
    /// Assigns a unique subagent Id (≥ 1), then registers the child in
    /// <see cref="SubSessions"/> and <see cref="SubagentsById"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Child is already registered.</exception>
    protected void RegisterSubagent(DysonAgentSession child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Id != 0 || SubagentsById.ContainsValue(child) || SubSessions.Contains(child))
            throw new InvalidOperationException("Subagent is already registered.");

        var id = AllocateSubagentId();
        child.Id = id;
        SubagentsById[id] = child;
        SubSessions.Add(child);
    }

    public void EnqueueInterrupt(DysonAgentInterrupt interrupt)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        _interrupts.Enqueue(interrupt);
        _interruptSignal.Release();
    }

    public bool TryDequeueInterrupt(out DysonAgentInterrupt interrupt)
    {
        if (!_interrupts.TryDequeue(out interrupt!))
            return false;

        // Keep signal count aligned when draining without WaitForInterruptAsync.
        _ = _interruptSignal.Wait(0);
        return true;
    }

    /// <summary>
    /// Waits for the next interrupt on this session's queue.
    /// Concrete <see cref="WaitForNotifyAsync"/> implementations should prefer draining this
    /// queue (e.g. map to a <c>DysonSubagentInterruptEvent</c>) so Work sees completions
    /// without busy-polling.
    /// </summary>
    public async Task<Result<DysonAgentInterrupt, string>> WaitForInterruptAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _interruptSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result<DysonAgentInterrupt, string>.AsError("Wait for interrupt was cancelled.");
        }

        if (_interrupts.TryDequeue(out var interrupt))
            return Result<DysonAgentInterrupt, string>.AsValue(interrupt);

        return Result<DysonAgentInterrupt, string>.AsError(
            "Interrupt signal received but queue was empty.");
    }

    protected void NotifySubagentCompleted(int subagentId, string? summary) =>
        EnqueueInterrupt(new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentCompleted,
            SubagentId = subagentId,
            Summary = summary,
        });

    public void AppendLog(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _logLines.Enqueue(line);
        LogAppended?.Invoke(this, line);
    }

    /// <summary>Raised after each <see cref="AppendLog"/> (hosts may persist a LogLine entry).</summary>
    public event EventHandler<string>? LogAppended;

    /// <summary>Raised after a successful <see cref="RenameAsync"/> (hosts should persist Title).</summary>
    public event EventHandler<DysonSessionRenamedEventArgs>? SessionRenamed;

    /// <summary>Snapshot of append-only log lines. When <paramref name="maxLines"/> is set, returns the most recent lines.</summary>
    public IReadOnlyList<string> SnapshotLog(int? maxLines = null)
    {
        var lines = _logLines.ToArray();
        if (maxLines is null || maxLines.Value >= lines.Length)
            return lines;

        if (maxLines.Value <= 0)
            return [];

        return lines.AsSpan(lines.Length - maxLines.Value).ToArray();
    }

    /// <summary>Session transcript turns (oldest first). Used by context optimization and future chat loop.</summary>
    protected List<DysonAgentTurn> TurnHistory { get; } = [];

    /// <summary>Public read-only view of <see cref="TurnHistory"/> for UI binding.</summary>
    public IReadOnlyList<DysonAgentTurn> Turns => TurnHistory;

    /// <summary>Raised after a turn is appended via <see cref="AddTurn"/> (hosts may UpsertTurn + TurnStarted log).</summary>
    public event EventHandler<DysonAgentTurn>? TurnAdded;

    /// <summary>Appends a turn to history and raises <see cref="TurnAdded"/>.</summary>
    protected void AddTurn(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        TurnHistory.Add(turn);
        TurnAdded?.Invoke(this, turn);
    }

    /// <summary>
    /// Hydrates this session from a full DB aggregate: sets <see cref="PersistenceId"/> /
    /// runtime <see cref="Id"/>, rebuilds turns (including tool state), and restores LogLine text.
    /// Caller must construct the session with matching mode/provider/config first.
    /// </summary>
    protected void RestoreFromPersisted(DysonPersistedSession state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Session);
        ArgumentNullException.ThrowIfNull(state.Turns);
        ArgumentNullException.ThrowIfNull(state.Logs);

        PersistenceId = state.Session.Id;
        Id = state.Session.RuntimeId;
        DisplayTitle = state.Session.Title;

        TurnHistory.Clear();
        foreach (var row in state.Turns.OrderBy(t => t.Sequence))
        {
            var turn = new DysonAgentTurn
            {
                Id = row.Id,
                Kind = row.Kind,
                Instruction = row.Instruction,
                AgentTitle = row.AgentTitle,
                AssistantText = row.AssistantText,
                ToolHistoryOptimized = row.ToolHistoryOptimized,
                CompactToolHistory = row.CompactToolHistory,
            };
            DysonTurnToolStateSerializer.ApplyToTurn(turn, row.ToolStateJson);
            TurnHistory.Add(turn);
        }

        while (_logLines.TryDequeue(out _))
        {
        }

        foreach (var log in state.Logs.OrderBy(l => l.Sequence))
        {
            if (!DysonSessionLogPayload.TryParseKind(log.Kind, out var kind)
                || kind != DysonSessionLogKind.LogLine)
            {
                continue;
            }

            var payload = DysonSessionLogPayload.Deserialize<DysonSessionLogLogLine>(log.PayloadJson);
            if (payload?.Line is not null)
                _logLines.Enqueue(payload.Line);
        }
    }

    /// <summary>Assigns <see cref="PersistenceId"/> after <see cref="DysonSessionStore.CreateSessionAsync"/>.</summary>
    protected void SetPersistenceId(Guid persistenceId) => PersistenceId = persistenceId;

    /// <summary>Sets <see cref="DisplayTitle"/> after create (mirrors persisted Title).</summary>
    protected void SetDisplayTitle(string? title) => DisplayTitle = title;

    public const int MaxDisplayTitleLength = 120;

    /// <summary>
    /// Renames the session for UI/list display. Validates, sets <see cref="DisplayTitle"/>,
    /// raises <see cref="SessionRenamed"/>. Caller/host should persist <c>sessions.Title</c>.
    /// </summary>
    public Task<VoidResult<string>> RenameAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(title))
            return Task.FromResult(new VoidResult<string>("Title is required."));

        var trimmed = title.Trim();
        if (trimmed.Length > MaxDisplayTitleLength)
            return Task.FromResult(new VoidResult<string>(
                $"Title must be at most {MaxDisplayTitleLength} characters."));

        DisplayTitle = trimmed;
        SessionRenamed?.Invoke(this, new DysonSessionRenamedEventArgs
        {
            PersistenceId = PersistenceId,
            Title = trimmed,
        });

        return Task.FromResult(VoidResult<string>.Success);
    }

    /// <summary>Compacts older tool history when turn-count or token thresholds fire.</summary>
    protected DysonContextOptimizer ContextOptimizer { get; set; } = new();

    /// <summary>Token counter for <see cref="OptimizeContextIfNeeded"/> thresholds.</summary>
    protected IDysonTokenCounter TokenCounter { get; set; } = new DysonTiktokenTokenCounter();

    /// <summary>
    /// Creates an ExpandThoughtProcess turn (reformulate before continuing heavy work).
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateExpandThoughtProcessTurn(string? focus = null) =>
        DysonExpandThoughtProcess.CreateTurn(focus);

    /// <summary>
    /// Creates a TaskCompletionConfirm turn after CompleteTask.
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateCompletionConfirmTurn(string? completeTaskSummary = null) =>
        DysonTaskCompletionFlow.CreateCompletionConfirmTurn(completeTaskSummary);

    /// <summary>
    /// Creates a Continuation turn after ContinueWork.
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateContinuationTurn(string? reason = null, string? remainingWork = null) =>
        DysonTaskCompletionFlow.CreateContinuationTurn(reason, remainingWork);

    /// <summary>
    /// Creates a ReportSummary turn after ConfirmTaskComplete (final handoff turn).
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateReportSummaryTurn(string? confirmRationale = null) =>
        DysonTaskCompletionFlow.CreateReportSummaryTurn(confirmRationale);

    /// <summary>
    /// Compacts eligible older turns' tool history when thresholds are met.
    /// Call before building the next provider request.
    /// </summary>
    public VoidResult<string> OptimizeContextIfNeeded()
    {
        if (!ContextOptimizer.ShouldOptimize(TurnHistory, TokenCounter))
            return VoidResult<string>.Success;

        return ContextOptimizer.Optimize(TurnHistory, TokenCounter);
    }

    public abstract Task<VoidResult<string>> LoadFunctionalContextAsync(
        CancellationToken cancellationToken = default);

    public abstract Task<VoidResult<string>> PromptAsync(
        string prompt,
        CancellationToken cancellationToken = default);

    public abstract Task<VoidResult<string>> PromptAsync(
        string prompt,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prefer draining the interrupt queue (see <see cref="WaitForInterruptAsync"/> /
    /// <see cref="TryDequeueInterrupt"/>) when mapping notify events, so Work’s async loop
    /// observes subagent completions without busy-polling.
    /// </summary>
    public abstract Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
        CancellationToken cancellationToken = default);
}
