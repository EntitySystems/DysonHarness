using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace DysonHarness;

public abstract class DysonAgentSession
{
    private int _nextSubagentId;
    private readonly ConcurrentQueue<DysonAgentInterrupt> _interrupts = new();
    private readonly SemaphoreSlim _interruptSignal = new(0);
    private readonly ConcurrentQueue<string> _logLines = new();
    private readonly List<DysonSessionTodo> _todos = [];
    private readonly object _todosGate = new();
    private readonly object _terminalGate = new();
    private readonly TaskCompletionSource<(DysonSessionStatus Status, string? Summary)> _terminalTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _runCts;

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
        McpPipeline = DysonMcpPipeline.CreateDefault(config.McpAccessMode, config.AvailableShellTypes);
    }

    /// <summary>Session identity. Root sessions are 0; subagents are allocated from 1.</summary>
    public int Id { get; protected set; }

    /// <summary>Durable SQLite session id (distinct from runtime <see cref="Id"/>).</summary>
    public Guid PersistenceId { get; protected set; }

    /// <summary>UI/list title mirrored from persisted <c>sessions.Title</c>.</summary>
    public string? DisplayTitle { get; protected set; }

    /// <summary>Live parent when this session was spawned via <see cref="RegisterSubagent"/>.</summary>
    public DysonAgentSession? Parent { get; private set; }

    /// <summary>Mirrored from persisted session status (Active until report/stop/fail).</summary>
    public DysonSessionStatus Status { get; private set; } = DysonSessionStatus.Active;

    /// <summary>Last SubmitSubagentReport / stop / fail summary when terminal.</summary>
    public string? LastReportSummary { get; private set; }

    public bool IsTerminal =>
        Status is DysonSessionStatus.Completed or DysonSessionStatus.Stopped or DysonSessionStatus.Failed;

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
    /// Assigns a unique subagent Id (≥ 1), sets <see cref="Parent"/>, then registers the child in
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
        child.Parent = this;
        OmitRootTaskCompletionTools(child.McpPipeline);
        SubagentsById[id] = child;
        SubSessions.Add(child);
        SubagentSpawned?.Invoke(this, child);
    }

    /// <summary>
    /// Subagents finish via <c>SubmitSubagentReport</c>; hide root CompleteTask flow tools from their catalog.
    /// </summary>
    private static void OmitRootTaskCompletionTools(DysonMcpPipeline pipeline)
    {
        pipeline.Tools.Remove("CompleteTask");
        pipeline.Tools.Remove("ConfirmTaskComplete");
        pipeline.Tools.Remove("ContinueWork");
    }

    public bool TryGetSubagent(int subagentId, out DysonAgentSession child) =>
        SubagentsById.TryGetValue(subagentId, out child!);

    public void EnqueueInterrupt(DysonAgentInterrupt interrupt)
    {
        ArgumentNullException.ThrowIfNull(interrupt);
        _interrupts.Enqueue(interrupt);
        _interruptSignal.Release();
        InterruptEnqueued?.Invoke(this, interrupt);
    }

    /// <summary>Raised after each <see cref="EnqueueInterrupt"/> (host auto-turn / cards).</summary>
    public event EventHandler<DysonAgentInterrupt>? InterruptEnqueued;

    /// <summary>Raised after <see cref="RegisterSubagent"/> (host session registry).</summary>
    public event EventHandler<DysonAgentSession>? SubagentSpawned;

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

    protected void NotifySubagentCompleted(int subagentId, string? summary, Guid? persistenceId = null) =>
        EnqueueInterrupt(new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentCompleted,
            SubagentId = subagentId,
            PersistenceId = persistenceId,
            Summary = summary,
        });

    protected void NotifySubagentStopped(int subagentId, string? summary, Guid? persistenceId = null) =>
        EnqueueInterrupt(new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentStopped,
            SubagentId = subagentId,
            PersistenceId = persistenceId,
            Summary = summary,
        });

    protected void NotifySubagentFailed(int subagentId, string? summary, Guid? persistenceId = null) =>
        EnqueueInterrupt(new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentFailed,
            SubagentId = subagentId,
            PersistenceId = persistenceId,
            Summary = summary,
        });

    /// <summary>
    /// Soft spawn policy: Plan banned; Explore never spawns; Drone may spawn Explore only
    /// (Drone→Drone rejected). Child mode must resolve via <see cref="DysonAgentSystemPrompts.ForMode"/>.
    /// </summary>
    public static VoidResult<string> ValidateSubagentSpawn(
        string parentMode,
        string childMode,
        IReadOnlyDictionary<string, string>? customAgents = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(childMode);

        if (string.Equals(parentMode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
            return new VoidResult<string>("Explore cannot spawn subagents.");

        if (string.Equals(childMode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
            return new VoidResult<string>("Plan cannot be used as a subagent mode (top-level only).");

        if (string.Equals(parentMode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(childMode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
                return new VoidResult<string>("Drone cannot spawn another Drone by default; spawn Explore instead.");

            if (!string.Equals(childMode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
                return new VoidResult<string>("Drone may only spawn Explore subagents.");
        }

        var resolved = DysonAgentSystemPrompts.ForMode(childMode, customAgents);
        if (resolved.IsError)
            return new VoidResult<string>(resolved.Error);

        return VoidResult<string>.Success;
    }

    /// <summary>Spawn a child session (non-blocking background prompt). Concrete providers implement persist + clone.</summary>
    /// <param name="initialTodos">Optional seed for the child’s own todo list (applied after the child row is persisted).</param>
    public abstract Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
        string agentMode,
        string task,
        string? context = null,
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
        CancellationToken cancellationToken = default);

    /// <summary>Default WaitForSubagent timeout when the tool omits <c>timeoutMs</c> (5 minutes).</summary>
    public const int DefaultWaitForSubagentTimeoutMs = 300_000;

    public async Task<Result<string, string>> WaitForSubagentAsync(
        int subagentId,
        int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetSubagent(subagentId, out var child))
            return Result<string, string>.AsError($"Unknown subagentId {subagentId}.");

        var effectiveTimeoutMs = timeoutMs ?? DefaultWaitForSubagentTimeoutMs;

        try
        {
            (DysonSessionStatus Status, string? Summary) terminal;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (effectiveTimeoutMs >= 0)
                timeoutCts.CancelAfter(effectiveTimeoutMs);

            try
            {
                terminal = await child.WaitForTerminalAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result<string, string>.AsValue(JsonSerializer.Serialize(new
                {
                    subagentId,
                    persistenceId = child.PersistenceId,
                    status = "timeout",
                    childStatus = child.Status.ToString(),
                    summary = child.LastReportSummary,
                }));
            }

            return Result<string, string>.AsValue(JsonSerializer.Serialize(new
            {
                subagentId,
                persistenceId = child.PersistenceId,
                status = terminal.Status.ToString(),
                summary = terminal.Summary,
            }));
        }
        catch (OperationCanceledException)
        {
            return Result<string, string>.AsError("WaitForSubagent was cancelled.");
        }
    }

    public Result<string, string> InspectSubagentLog(int subagentId, int? maxLines = null)
    {
        if (!TryGetSubagent(subagentId, out var child))
            return Result<string, string>.AsError($"Unknown subagentId {subagentId}.");

        var lines = child.SnapshotLog(maxLines);
        return Result<string, string>.AsValue(JsonSerializer.Serialize(new
        {
            subagentId,
            persistenceId = child.PersistenceId,
            status = child.Status.ToString(),
            lines,
        }));
    }

    public Task<Result<string, string>> StopSubagentAsync(
        int subagentId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSubagent(subagentId, out var child))
            return Task.FromResult(Result<string, string>.AsError($"Unknown subagentId {subagentId}."));

        child.CancelBackgroundRun();
        var summary = string.IsNullOrWhiteSpace(reason) ? "Stopped by parent." : reason.Trim();
        if (child.TryMarkTerminal(DysonSessionStatus.Stopped, summary))
            NotifySubagentStopped(child.Id, summary, child.PersistenceId == Guid.Empty ? null : child.PersistenceId);

        return Task.FromResult(Result<string, string>.AsValue(JsonSerializer.Serialize(new
        {
            subagentId,
            persistenceId = child.PersistenceId,
            status = child.Status.ToString(),
            summary = child.LastReportSummary,
        })));
    }

    public Task<Result<string, string>> SubmitSubagentReportAsync(
        string summary,
        bool failed = false,
        bool skipTasksCheck = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(summary))
            return Task.FromResult(Result<string, string>.AsError("SubmitSubagentReport: summary is required."));

        var incomplete = Todos
            .Where(t => t.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing)
            .Select(t => new
            {
                taskCode = t.TaskCode,
                displayName = t.DisplayName,
                status = t.Status.ToString(),
            })
            .ToArray();

        if (incomplete.Length > 0 && !skipTasksCheck)
        {
            var list = string.Join("; ", incomplete.Select(t =>
                $"{t.taskCode} ({t.displayName})={t.status}"));
            return Task.FromResult(Result<string, string>.AsError(
                "SubmitSubagentReport: incomplete todos (pass skipTasksCheck to override): " + list));
        }

        var trimmed = summary.Trim();
        var status = failed ? DysonSessionStatus.Failed : DysonSessionStatus.Completed;
        if (!TryMarkTerminal(status, trimmed))
        {
            return Task.FromResult(Result<string, string>.AsError(
                $"SubmitSubagentReport: session already {Status}."));
        }

        if (Parent is not null)
        {
            if (failed)
                Parent.NotifySubagentFailed(Id, trimmed, PersistenceId == Guid.Empty ? null : PersistenceId);
            else
                Parent.NotifySubagentCompleted(Id, trimmed, PersistenceId == Guid.Empty ? null : PersistenceId);
        }

        if (incomplete.Length > 0)
        {
            return Task.FromResult(Result<string, string>.AsValue(JsonSerializer.Serialize(new
            {
                subagentId = Id,
                persistenceId = PersistenceId,
                status = Status.ToString(),
                summary = trimmed,
                incompleteTodos = incomplete,
                skipTasksCheck = true,
            })));
        }

        return Task.FromResult(Result<string, string>.AsValue(JsonSerializer.Serialize(new
        {
            subagentId = Id,
            persistenceId = PersistenceId,
            status = Status.ToString(),
            summary = trimmed,
        })));
    }

    public Task<(DysonSessionStatus Status, string? Summary)> WaitForTerminalAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_terminalGate)
        {
            if (IsTerminal)
                return Task.FromResult((Status, LastReportSummary));
        }

        return _terminalTcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Marks terminal status once; returns false if already terminal.</summary>
    public bool TryMarkTerminal(DysonSessionStatus status, string? summary)
    {
        if (status is not (DysonSessionStatus.Completed or DysonSessionStatus.Stopped or DysonSessionStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Must be a terminal status.");

        lock (_terminalGate)
        {
            if (IsTerminal)
                return false;

            Status = status;
            LastReportSummary = summary;
            _terminalTcs.TrySetResult((status, summary));
            return true;
        }
    }

    /// <summary>Stores the CTS used to cancel the background <see cref="PromptAsync"/> for StopSubagent.</summary>
    protected void AttachBackgroundRun(CancellationTokenSource runCts)
    {
        ArgumentNullException.ThrowIfNull(runCts);
        _runCts = runCts;
    }

    protected void CancelBackgroundRun()
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }
    }

    /// <summary>Builds the first-turn prompt for a spawned child (Explore/Drone get harness mandates).</summary>
    protected static string BuildChildFirstPrompt(string agentMode, string task, string? context)
    {
        var sb = new StringBuilder();
        if (string.Equals(agentMode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(DysonAgentSystemPrompts.ExploreFirstTurnReportMandate.Trim());
            sb.AppendLine();
        }
        else if (string.Equals(agentMode, DysonAgentModes.Drone, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine(DysonAgentSystemPrompts.DroneFirstTurnContextMandate.Trim());
            sb.AppendLine();
        }

        sb.AppendLine(task.Trim());
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine();
            sb.AppendLine("## Context");
            sb.AppendLine(context.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    protected static string TitleFromTask(string task)
    {
        var t = task.Trim().Replace('\r', ' ').Replace('\n', ' ');
        while (t.Contains("  ", StringComparison.Ordinal))
            t = t.Replace("  ", " ", StringComparison.Ordinal);

        if (t.Length <= 80)
            return string.IsNullOrEmpty(t) ? "Subagent" : t;

        return t[..80] + "…";
    }

    /// <summary>
    /// Fire-and-forget child prompt; on unexpected failure marks Failed and notifies parent.
    /// </summary>
    protected static void KickOffChildPrompt(DysonAgentSession child, string prompt, CancellationTokenSource runCts)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(runCts);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await child.PromptAsync(prompt, runCts.Token).ConfigureAwait(false);
                if (runCts.IsCancellationRequested || child.IsTerminal)
                    return;

                var failSummary = result.IsError
                    ? result.Error
                    : "Child finished without SubmitSubagentReport";
                if (child.TryMarkTerminal(DysonSessionStatus.Failed, failSummary))
                {
                    child.Parent?.NotifySubagentFailed(
                        child.Id,
                        failSummary,
                        child.PersistenceId == Guid.Empty ? null : child.PersistenceId);
                }
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
                // StopSubagent owns terminal state.
            }
            catch (Exception ex)
            {
                if (child.TryMarkTerminal(DysonSessionStatus.Failed, ex.Message))
                {
                    child.Parent?.NotifySubagentFailed(
                        child.Id,
                        ex.Message,
                        child.PersistenceId == Guid.Empty ? null : child.PersistenceId);
                }
            }
            finally
            {
                runCts.Dispose();
            }
        });
    }

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

    /// <summary>In-memory session todo list (oldest/create-order first).</summary>
    public IReadOnlyList<DysonSessionTodo> Todos
    {
        get
        {
            lock (_todosGate)
                return _todos.ToArray();
        }
    }

    /// <summary>Raised after in-memory todos change (create/update/delete/restore/replace).</summary>
    public event EventHandler? TodosChanged;

    /// <summary>Optional store for durable todo mutations when <see cref="PersistenceId"/> is set.</summary>
    protected DysonSessionStore? SessionStore { get; set; }

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
    /// runtime <see cref="Id"/>, rebuilds turns (including tool state), restores LogLine text,
    /// and restores todos.
    /// Caller must construct the session with matching mode/provider/config first.
    /// </summary>
    protected void RestoreFromPersisted(DysonPersistedSession state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Session);
        ArgumentNullException.ThrowIfNull(state.Turns);
        ArgumentNullException.ThrowIfNull(state.Logs);
        ArgumentNullException.ThrowIfNull(state.Todos);

        PersistenceId = state.Session.Id;
        Id = state.Session.RuntimeId;
        DisplayTitle = state.Session.Title;
        Status = state.Session.Status;
        if (IsTerminal)
            _terminalTcs.TrySetResult((Status, LastReportSummary));

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
                StartedUtc = row.CreatedUtc,
                CompletedUtc = row.CompletedUtc,
            };
            DysonTurnToolStateSerializer.ApplyToTurn(turn, row.ToolStateJson);
            turn.FinalizeIncompleteTools(
                "Tool call did not complete (cancelled or interrupted).");
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

        RestoreTodos(state.Todos);
    }

    /// <summary>Replaces the in-memory todo list (e.g. after resume) and raises <see cref="TodosChanged"/>.</summary>
    public void RestoreTodos(IEnumerable<DysonSessionTodo> todos)
    {
        ArgumentNullException.ThrowIfNull(todos);
        lock (_todosGate)
        {
            _todos.Clear();
            _todos.AddRange(todos.OrderBy(t => t.Sequence));
        }

        TodosChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ListTodosAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<IReadOnlyList<DysonSessionTodo>, string>.AsValue(Todos));
    }

    public async Task<Result<DysonSessionTodo, string>> CreateTodoAsync(
        string taskCode,
        string displayName,
        DysonSessionTodoStatus status = DysonSessionTodoStatus.Pending,
        IReadOnlyList<string>? comments = null,
        CancellationToken cancellationToken = default)
    {
        if (PersistenceId != Guid.Empty)
        {
            if (SessionStore is null)
                return Result<DysonSessionTodo, string>.AsError("Session store is not available.");

            var persisted = await SessionStore.CreateTodoAsync(
                    new DysonSessionTodoCreateRequest
                    {
                        SessionId = PersistenceId,
                        TaskCode = taskCode,
                        DisplayName = displayName,
                        Status = status,
                        Comments = comments,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (persisted.IsError)
                return persisted;

            UpsertTodoInMemory(persisted.Value);
            return persisted;
        }

        if (string.IsNullOrWhiteSpace(taskCode))
            return Result<DysonSessionTodo, string>.AsError("TaskCode is required.");

        if (string.IsNullOrWhiteSpace(displayName))
            return Result<DysonSessionTodo, string>.AsError("DisplayName is required.");

        if (!Enum.IsDefined(status))
            return Result<DysonSessionTodo, string>.AsError($"Invalid status '{status}'.");

        var code = taskCode.Trim();
        lock (_todosGate)
        {
            if (_todos.Any(t => string.Equals(t.TaskCode, code, StringComparison.Ordinal)))
                return Result<DysonSessionTodo, string>.AsError($"Todo TaskCode '{code}' already exists.");
        }

        var now = DateTime.UtcNow;
        int sequence;
        lock (_todosGate)
            sequence = (_todos.Count == 0 ? 0 : _todos.Max(t => t.Sequence)) + 1;

        var todo = new DysonSessionTodo
        {
            Id = Guid.NewGuid(),
            SessionId = PersistenceId,
            TaskCode = code,
            DisplayName = displayName.Trim(),
            Status = status,
            Comments = comments?.ToArray() ?? [],
            Sequence = sequence,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        UpsertTodoInMemory(todo);
        return Result<DysonSessionTodo, string>.AsValue(todo);
    }

    public async Task<Result<DysonSessionTodo, string>> UpdateTodoAsync(
        string taskCode,
        string? displayName = null,
        DysonSessionTodoStatus? status = null,
        IReadOnlyList<string>? comments = null,
        string? appendComment = null,
        CancellationToken cancellationToken = default)
    {
        if (PersistenceId != Guid.Empty)
        {
            if (SessionStore is null)
                return Result<DysonSessionTodo, string>.AsError("Session store is not available.");

            var persisted = await SessionStore.UpdateTodoAsync(
                    new DysonSessionTodoUpdateRequest
                    {
                        SessionId = PersistenceId,
                        TaskCode = taskCode,
                        DisplayName = displayName,
                        Status = status,
                        Comments = comments,
                        AppendComment = appendComment,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (persisted.IsError)
                return persisted;

            UpsertTodoInMemory(persisted.Value);
            return persisted;
        }

        if (string.IsNullOrWhiteSpace(taskCode))
            return Result<DysonSessionTodo, string>.AsError("TaskCode is required.");

        if (status is { } s && !Enum.IsDefined(s))
            return Result<DysonSessionTodo, string>.AsError($"Invalid status '{s}'.");

        var code = taskCode.Trim();
        DysonSessionTodo updated;
        lock (_todosGate)
        {
            var idx = _todos.FindIndex(t => string.Equals(t.TaskCode, code, StringComparison.Ordinal));
            if (idx < 0)
                return Result<DysonSessionTodo, string>.AsError($"Todo '{code}' not found.");

            var current = _todos[idx];
            if (displayName is not null && string.IsNullOrWhiteSpace(displayName))
                return Result<DysonSessionTodo, string>.AsError("DisplayName cannot be empty.");

            var nextComments = comments?.ToArray() ?? current.Comments.ToArray();
            if (appendComment is not null)
                nextComments = [.. nextComments, appendComment];

            updated = new DysonSessionTodo
            {
                Id = current.Id,
                SessionId = current.SessionId,
                TaskCode = current.TaskCode,
                DisplayName = displayName?.Trim() ?? current.DisplayName,
                Status = status ?? current.Status,
                Comments = nextComments,
                Sequence = current.Sequence,
                CreatedUtc = current.CreatedUtc,
                UpdatedUtc = DateTime.UtcNow,
            };
            _todos[idx] = updated;
        }

        TodosChanged?.Invoke(this, EventArgs.Empty);
        return Result<DysonSessionTodo, string>.AsValue(updated);
    }

    public async Task<VoidResult<string>> DeleteTodoAsync(
        string taskCode,
        CancellationToken cancellationToken = default)
    {
        if (PersistenceId != Guid.Empty)
        {
            if (SessionStore is null)
                return new VoidResult<string>("Session store is not available.");

            var deleted = await SessionStore.DeleteTodoAsync(PersistenceId, taskCode, cancellationToken)
                .ConfigureAwait(false);

            if (deleted.IsError)
                return deleted;

            RemoveTodoInMemory(taskCode);
            return VoidResult<string>.Success;
        }

        if (string.IsNullOrWhiteSpace(taskCode))
            return new VoidResult<string>("TaskCode is required.");

        if (!RemoveTodoInMemory(taskCode))
            return new VoidResult<string>($"Todo '{taskCode.Trim()}' not found.");

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Replaces the in-memory list; when persisted, also replaces rows via the store.
    /// </summary>
    public async Task<Result<IReadOnlyList<DysonSessionTodo>, string>> ReplaceTodosAsync(
        IReadOnlyList<DysonSessionTodoReplaceItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (PersistenceId != Guid.Empty)
        {
            if (SessionStore is null)
            {
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    "Session store is not available.");
            }

            var replaced = await SessionStore.ReplaceTodosAsync(PersistenceId, items, cancellationToken)
                .ConfigureAwait(false);

            if (replaced.IsError)
                return replaced;

            RestoreTodos(replaced.Value);
            return replaced;
        }

        var now = DateTime.UtcNow;
        var built = new List<DysonSessionTodo>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.TaskCode))
            {
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"items[{i}].TaskCode is required.");
            }

            if (string.IsNullOrWhiteSpace(item.DisplayName))
            {
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"items[{i}].DisplayName is required.");
            }

            if (!Enum.IsDefined(item.Status))
            {
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"items[{i}].Status is invalid.");
            }

            var code = item.TaskCode.Trim();
            if (!seen.Add(code))
            {
                return Result<IReadOnlyList<DysonSessionTodo>, string>.AsError(
                    $"Duplicate TaskCode '{code}' in replace set.");
            }

            built.Add(new DysonSessionTodo
            {
                Id = Guid.NewGuid(),
                SessionId = PersistenceId,
                TaskCode = code,
                DisplayName = item.DisplayName.Trim(),
                Status = item.Status,
                Comments = item.Comments?.ToArray() ?? [],
                Sequence = i + 1,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        RestoreTodos(built);
        return Result<IReadOnlyList<DysonSessionTodo>, string>.AsValue(built);
    }

    private void UpsertTodoInMemory(DysonSessionTodo todo)
    {
        ArgumentNullException.ThrowIfNull(todo);
        lock (_todosGate)
        {
            var idx = _todos.FindIndex(
                t => string.Equals(t.TaskCode, todo.TaskCode, StringComparison.Ordinal));
            if (idx >= 0)
                _todos[idx] = todo;
            else
                _todos.Add(todo);

            _todos.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
        }

        TodosChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool RemoveTodoInMemory(string taskCode)
    {
        var code = taskCode.Trim();
        lock (_todosGate)
        {
            var idx = _todos.FindIndex(t => string.Equals(t.TaskCode, code, StringComparison.Ordinal));
            if (idx < 0)
                return false;

            _todos.RemoveAt(idx);
        }

        TodosChanged?.Invoke(this, EventArgs.Empty);
        return true;
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
