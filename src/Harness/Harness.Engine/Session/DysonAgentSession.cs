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
    private readonly ConcurrentDictionary<Guid, DysonParentEvent> _pendingParentEvents = new();
    private readonly HashSet<int> _waitingOnSubagentIds = [];
    private readonly object _waitingOnGate = new();
    private readonly ConcurrentQueue<DysonAgentTurn> _pendingTurns = new();
    private readonly Stack<DysonAgentTurn> _inFlightPromptStack = new();
    private TaskCompletionSource<Result<string, string>>? _parentEventWaitTcs;
    private TaskCompletionSource<Result<string, string>>? _askQuestionTcs;
    private TaskCompletionSource<Result<string, string>>? _promptUserDialogTcs;
    private readonly object _askQuestionGate = new();
    // ponytail: shared UI+MCP claim set; upgrade to per-turn CancellationToken if hang recovery is needed
    private readonly ConcurrentDictionary<Guid, byte> _summarizingTurnIds = new();
    private readonly SemaphoreSlim _summarizeGate = new(1, 1);

    protected DysonAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        DysonAgentProvider provider,
        string? systemPromptSuffix = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        SubSessions = new List<DysonAgentSession>();

        var prompt = DysonAgentSystemPrompts.ForMode(agentMode, config.CustomAgents);
        if (prompt.IsError)
            throw new ArgumentOutOfRangeException(nameof(agentMode), agentMode, prompt.Error);

        Mode = agentMode;
        SystemPrompt = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            prompt.Value,
            systemPromptSuffix,
            DysonAgentSystemPrompts.BuildPluginInstructionBlock(config))!;
        // Do not gate as root here: Parent is always null in the ctor. Roots call
        // ConfigureRootInterAgentTools from Create/Load; children get gated in Register/Restore.
        McpPipeline = DysonSessionToolsetBuilder.BuildInitial(config, agentMode, ResolveModelSlugId());
    }

    /// <summary>Root-only catalog gate (depth 0). Call from root Create/Load, never from child ctors.</summary>
    protected void ConfigureRootInterAgentTools()
    {
        McpPipeline.ConfigureInterAgentTools(0);
        DysonSessionToolsetBuilder.ReapplyDisabledTools(
            McpPipeline, Config, Mode, ResolveModelSlugId());
    }

    private Guid? ResolveModelSlugId() =>
        Provider is OpenAiCompatibleAgentProvider oai ? oai.SlugId : null;

    /// <summary>0 = root, 1 = direct child of root, 2+ = deeper.</summary>
    public int ComputeDepth()
    {
        var depth = 0;
        for (var p = Parent; p is not null; p = p.Parent)
            depth++;
        return depth;
    }

    /// <summary>True while this session is blocked inside <see cref="WaitForSubagentAsync"/> on any child.</summary>
    public bool IsWaitingOnAnySubagent
    {
        get
        {
            lock (_waitingOnGate)
                return _waitingOnSubagentIds.Count > 0;
        }
    }

    /// <summary>Subagent ids currently waited on by <see cref="WaitForSubagentAsync"/>.</summary>
    public IReadOnlyList<int> WaitingOnSubagentIds
    {
        get
        {
            lock (_waitingOnGate)
                return _waitingOnSubagentIds.OrderBy(id => id).ToArray();
        }
    }

    /// <summary>True while this child is blocked in <see cref="TriggerParentEventAsync"/> awaiting a reply.</summary>
    public bool HasPendingParentEventWait =>
        _parentEventWaitTcs is { Task.IsCompleted: false };

    /// <summary>Root AskQuestion pending questions (null when idle).</summary>
    public IReadOnlyList<DysonAskQuestionItem>? PendingAskQuestions { get; private set; }

    /// <summary>Root PromptUserDialog pending request (null when idle).</summary>
    public DysonPromptUserDialogRequest? PendingUserDialog { get; private set; }

    /// <summary>Pending + recently addressed inbound parent events (host Subagent-event UI).</summary>
    public IReadOnlyList<DysonParentEvent> PendingOrRecentParentEvents =>
        _pendingParentEvents.Values
            .OrderByDescending(e => e.Timestamp)
            .Take(20)
            .ToArray();

    /// <summary>Session identity. Root sessions are 0; subagents are allocated from 1.</summary>
    public int Id { get; protected set; }

    /// <summary>Durable SQLite session id (distinct from runtime <see cref="Id"/>).</summary>
    public Guid PersistenceId { get; protected set; }

    /// <summary>UI/list title mirrored from persisted <c>sessions.Title</c>.</summary>
    public string? DisplayTitle { get; protected set; }

    /// <summary>Live parent when this session was spawned via <see cref="RegisterSubagent"/>.</summary>
    public DysonAgentSession? Parent { get; private set; }

    /// <summary>Mirrored from persisted session status (Active until report/stop/fail/interrupt).</summary>
    public DysonSessionStatus Status { get; private set; } = DysonSessionStatus.Active;

    /// <summary>Last SubmitSubagentReport / stop / fail summary when terminal.</summary>
    public string? LastReportSummary { get; private set; }

    public bool IsTerminal =>
        Status is DysonSessionStatus.Completed
            or DysonSessionStatus.Stopped
            or DysonSessionStatus.Failed
            or DysonSessionStatus.Interrupted;

    /// <summary>True while any turn is mid <c>SummarizeTurns</c> (host or MCP).</summary>
    public bool HasAnySummarizingTurn => !_summarizingTurnIds.IsEmpty;

    /// <summary>True while this turn id is claimed for summarization.</summary>
    public bool IsSummarizingTurn(Guid turnId) =>
        turnId != Guid.Empty && _summarizingTurnIds.ContainsKey(turnId);

    /// <summary>
    /// Claims <paramref name="turnId"/> for summarization. False if empty or already claimed.
    /// </summary>
    public bool TryBeginSummarizeTurn(Guid turnId) =>
        turnId != Guid.Empty && _summarizingTurnIds.TryAdd(turnId, 0);

    /// <summary>Releases a summarization claim (no-op if not claimed).</summary>
    public void EndSummarizeTurn(Guid turnId) =>
        _summarizingTurnIds.TryRemove(turnId, out _);

    /// <summary>Single-flight gate for host/MCP summarize pipelines on this session.</summary>
    public Task EnterSummarizeGateAsync(CancellationToken cancellationToken = default) =>
        _summarizeGate.WaitAsync(cancellationToken);

    /// <summary>Releases <see cref="EnterSummarizeGateAsync"/>.</summary>
    public void ExitSummarizeGate() => _summarizeGate.Release();

    public DysonAgentSessionConfig Config { get; }

    public string Mode { get; private set; }

    public string SystemPrompt { get; private set; }

    /// <summary>
    /// Bumped by <see cref="ApplyAgentMode"/> so OpenAI <c>prompt_cache_key</c> invalidates
    /// after a mid-session system-prompt rebuild (cache loss is intentional).
    /// </summary>
    public int SystemPromptGeneration { get; private set; }

    /// <summary>
    /// Bumps the prompt-cache generation (e.g. after custom MCP catalog merge/strip).
    /// </summary>
    public void BumpSystemPromptGeneration() => SystemPromptGeneration++;

    public DysonMcpPipeline McpPipeline { get; private set; }

    /// <summary>
    /// Rebuilds <see cref="Mode"/> / <see cref="SystemPrompt"/> and the MCP catalog for a
    /// mid-session mode switch (full rebuild so re-enabled tools return).
    /// No-op (no generation bump) when <paramref name="agentMode"/> matches current mode.
    /// </summary>
    public VoidResult<string> ApplyAgentMode(string agentMode, string? systemPromptSuffix = null)
    {
        if (string.IsNullOrWhiteSpace(agentMode))
            return new VoidResult<string>("Agent mode is required.");

        var trimmed = agentMode.Trim();
        if (string.Equals(Mode, trimmed, StringComparison.OrdinalIgnoreCase))
            return VoidResult<string>.Success;

        var prompt = DysonAgentSystemPrompts.ForMode(trimmed, Config.CustomAgents);
        if (prompt.IsError)
            return new VoidResult<string>(prompt.Error);

        Mode = trimmed;
        SystemPrompt = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            prompt.Value,
            systemPromptSuffix,
            DysonAgentSystemPrompts.BuildPluginInstructionBlock(Config))!;

        if (Config.ToolPolicy is not null)
        {
            Config.DisabledTools = DysonToolPolicyResolver.Resolve(
                Config.ToolPolicy, Mode, ResolveModelSlugId());
        }

        McpPipeline = DysonSessionToolsetBuilder.Build(
            Config,
            Mode,
            interAgentDepth: ComputeDepth(),
            omitRootTaskCompletionTools: Parent is not null,
            modelSlugId: ResolveModelSlugId());
        SystemPromptGeneration++;
        AppendLog($"mode → {Mode} (system prompt + toolset rebuilt)");
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Stores the current presentation snapshot and rewrites <c>RenderHtmlVisualization</c>
    /// when that tool is in the catalog. Does not bump <see cref="SystemPromptGeneration"/>.
    /// </summary>
    public void ApplyUiTheme(DysonUiThemeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Config.UiTheme = snapshot;
        McpPipeline.ApplyVisualizationTheme(snapshot);
    }

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
        ApplyChildStructuralGates(child);
        SubagentsById[id] = child;
        SubSessions.Add(child);
        SubagentSpawned?.Invoke(this, child);
    }

    /// <summary>
    /// Re-links a previously persisted child into <see cref="SubSessions"/> / <see cref="SubagentsById"/>
    /// using the child's existing runtime <see cref="Id"/> (from DB <c>RuntimeId</c>).
    /// Does not raise <see cref="SubagentSpawned"/> — hosts register explicitly after load.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Child id &lt; 1, or id/instance already registered to a different session.
    /// </exception>
    public void RestoreRegisteredSubagent(DysonAgentSession child)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (child.Id < 1)
            throw new InvalidOperationException("Restored subagent must already have RuntimeId ≥ 1.");

        if (SubagentsById.TryGetValue(child.Id, out var existingById))
        {
            if (!ReferenceEquals(existingById, child))
            {
                throw new InvalidOperationException(
                    $"Subagent id {child.Id} is already registered to a different session.");
            }

            child.Parent = this;
            ApplyChildStructuralGates(child);
            BumpNextSubagentId(child.Id);
            return;
        }

        if (SubagentsById.ContainsValue(child) || SubSessions.Contains(child))
            throw new InvalidOperationException("Subagent is already registered under a different id.");

        child.Parent = this;
        ApplyChildStructuralGates(child);
        SubagentsById[child.Id] = child;
        SubSessions.Add(child);
        BumpNextSubagentId(child.Id);
    }

    /// <summary>
    /// Ensures the next <see cref="AllocateSubagentId"/> is strictly greater than <paramref name="seenId"/>.
    /// </summary>
    private void BumpNextSubagentId(int seenId)
    {
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref _nextSubagentId);
            if (snapshot >= seenId)
                return;
        } while (Interlocked.CompareExchange(ref _nextSubagentId, seenId, snapshot) != snapshot);
    }

    /// <summary>
    /// Subagents finish via <c>SubmitSubagentReport</c>; hide root CompleteTask flow tools from their catalog.
    /// Re-applies mode denylist after structural Ensure* so policy stays authoritative.
    /// </summary>
    private static void ApplyChildStructuralGates(DysonAgentSession child)
    {
        DysonSessionToolsetBuilder.OmitRootTaskCompletionTools(child.McpPipeline);
        child.McpPipeline.ConfigureInterAgentTools(child.ComputeDepth());
        DysonSessionToolsetBuilder.ReapplyDisabledTools(
            child.McpPipeline, child.Config, child.Mode, child.ResolveModelSlugId());
        child.Config.CustomMcpHost?.AttachSession(child);
        child.Config.PluginMcpHost?.AttachSession(child);
    }

    public bool TryGetSubagent(int subagentId, out DysonAgentSession child) =>
        SubagentsById.TryGetValue(subagentId, out child!);

    /// <summary>
    /// JSON array of direct children for the <c>ListSubagents</c> MCP tool
    /// (<c>subagentId</c>, <c>persistenceId</c>, <c>agentMode</c>, <c>title</c>, <c>status</c>, optional <c>modelLabel</c>).
    /// </summary>
    public string FormatListSubagentsJson()
    {
        var items = SubSessions
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                subagentId = c.Id,
                persistenceId = c.PersistenceId,
                agentMode = c.Mode,
                title = c.DisplayTitle,
                status = c.Status.ToString(),
                modelLabel = FormatSubagentModelLabel(c.Provider),
            });

        return JsonSerializer.Serialize(items);
    }

    private static string? FormatSubagentModelLabel(DysonAgentProvider? provider) =>
        provider switch
        {
            OpenAiCompatibleAgentProvider oai =>
                $"{oai.DisplayAlias} · {oai.ProviderDisplayName} / {oai.Slug}",
            _ => null,
        };

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

    /// <summary>Raised when inbound parent events or root AskQuestion / PromptUserDialog pending state changes (UI).</summary>
    public event EventHandler? ParentEventsChanged;

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
    /// <param name="modelSlug">Optional model slug/alias; omit to inherit the parent’s current provider.</param>
    /// <param name="reasoningEffort">Optional effort override; null/omit → slug default when resolving a slug, else keep parent’s current effort.</param>
    public abstract Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
        string agentMode,
        string task,
        string? context = null,
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
        string? modelSlug = null,
        string? reasoningEffort = null,
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

        lock (_waitingOnGate)
            _waitingOnSubagentIds.Add(subagentId);

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
        finally
        {
            lock (_waitingOnGate)
                _waitingOnSubagentIds.Remove(subagentId);
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

    /// <summary>
    /// Child → parent: queue an event and block until <see cref="RespondToSubagentEvent"/>.
    /// Fails immediately if the parent is inside <see cref="WaitForSubagentAsync"/> for any child.
    /// </summary>
    public async Task<Result<string, string>> TriggerParentEventAsync(
        string kind,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (Parent is null)
            return Result<string, string>.AsError("TriggerParentEvent: session has no parent.");

        if (string.IsNullOrWhiteSpace(kind))
            return Result<string, string>.AsError("TriggerParentEvent: kind is required.");

        payload ??= "";

        if (Parent.IsWaitingOnAnySubagent)
        {
            var ids = string.Join(", ", Parent.WaitingOnSubagentIds);
            return Result<string, string>.AsError(
                "TriggerParentEvent: parent is waiting on subagent id(s) [" + ids +
                "] and cannot address new events (deadlock guard).");
        }

        var evt = new DysonParentEvent
        {
            EventId = Guid.NewGuid(),
            SubagentId = Id,
            PersistenceId = PersistenceId == Guid.Empty ? null : PersistenceId,
            Kind = kind.Trim(),
            Payload = payload,
        };

        if (!Parent._pendingParentEvents.TryAdd(evt.EventId, evt))
            return Result<string, string>.AsError("TriggerParentEvent: failed to register event.");

        var waitTcs = new TaskCompletionSource<Result<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _parentEventWaitTcs = waitTcs;

        Parent.EnqueueInterrupt(new DysonAgentInterrupt
        {
            Kind = DysonAgentInterruptKind.SubagentEvent,
            SubagentId = Id,
            PersistenceId = evt.PersistenceId,
            Summary = evt.Kind,
            EventId = evt.EventId,
            EventKind = evt.Kind,
            Payload = evt.Payload,
        });
        Parent.RaiseParentEventsChanged();

        try
        {
            using var reg = cancellationToken.Register(() =>
            {
                waitTcs.TrySetResult(Result<string, string>.AsError("TriggerParentEvent was cancelled."));
                if (Parent._pendingParentEvents.TryGetValue(evt.EventId, out var pending)
                    && pending.Status == DysonParentEventStatus.Pending)
                {
                    pending.Status = DysonParentEventStatus.Cancelled;
                    pending.ReplyTcs.TrySetResult(
                        Result<string, string>.AsError("TriggerParentEvent was cancelled."));
                    Parent.RaiseParentEventsChanged();
                }
            });

            var replyTask = evt.ReplyTcs.Task;
            var cancelTask = waitTcs.Task;
            var completed = await Task.WhenAny(replyTask, cancelTask).ConfigureAwait(false);
            var result = await completed.ConfigureAwait(false);

            if (!ReferenceEquals(completed, replyTask)
                && pendingStillOpen())
            {
                evt.Status = DysonParentEventStatus.Cancelled;
                evt.ReplyTcs.TrySetResult(result);
                Parent.RaiseParentEventsChanged();
            }

            return result;

            bool pendingStillOpen() =>
                Parent._pendingParentEvents.TryGetValue(evt.EventId, out var p)
                && p.Status == DysonParentEventStatus.Pending;
        }
        finally
        {
            if (ReferenceEquals(_parentEventWaitTcs, waitTcs))
                _parentEventWaitTcs = null;
        }
    }

    /// <summary>
    /// Completes a pending inbound event. Not wait-gated — succeeds even mid-<see cref="WaitForSubagentAsync"/>.
    /// </summary>
    public Result<string, string> RespondToSubagentEvent(int subagentId, Guid eventId, string reply)
    {
        if (eventId == Guid.Empty)
            return Result<string, string>.AsError("RespondToSubagentEvent: eventId is required.");

        if (!_pendingParentEvents.TryGetValue(eventId, out var evt))
            return Result<string, string>.AsError($"RespondToSubagentEvent: unknown eventId {eventId:D}.");

        if (evt.SubagentId != subagentId)
        {
            return Result<string, string>.AsError(
                $"RespondToSubagentEvent: eventId {eventId:D} belongs to subagent {evt.SubagentId}, not {subagentId}.");
        }

        if (evt.Status != DysonParentEventStatus.Pending)
        {
            return Result<string, string>.AsError(
                $"RespondToSubagentEvent: eventId {eventId:D} is already {evt.Status}.");
        }

        reply ??= "";
        evt.Status = DysonParentEventStatus.Addressed;
        evt.ReplyTcs.TrySetResult(Result<string, string>.AsValue(reply));
        RaiseParentEventsChanged();

        return Result<string, string>.AsValue(JsonSerializer.Serialize(new
        {
            eventId = evt.EventId,
            subagentId = evt.SubagentId,
            status = "addressed",
        }));
    }

    /// <summary>
    /// Parent → child inject. Default queues next-turn prompt; <paramref name="interruptSubagent"/> cancels
    /// in-flight turn (and any parent-event wait) then starts immediately.
    /// </summary>
    public Task<Result<string, string>> TriggerSubagentEventAsync(
        int subagentId,
        string payload,
        bool interruptSubagent = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSubagent(subagentId, out var child))
            return Task.FromResult(Result<string, string>.AsError($"Unknown subagentId {subagentId}."));

        if (string.IsNullOrWhiteSpace(payload))
            return Task.FromResult(Result<string, string>.AsError("TriggerSubagentEvent: payload is required."));

        var trimmed = payload.Trim();

        if (child.HasPendingParentEventWait && !interruptSubagent)
        {
            return Task.FromResult(Result<string, string>.AsError(
                "TriggerSubagentEvent: child is awaiting a parent-event reply; pass interruptSubagent=true to cancel that wait and inject."));
        }

        if (interruptSubagent)
        {
            child.CancelPendingParentEventWait(
                "cancelled by parent TriggerSubagentEvent");
            child.CancelBackgroundRun();
            child.ClearPendingTurns();
            var runCts = new CancellationTokenSource();
            child.AttachBackgroundRun(runCts);
            KickOffChildPrompt(child, CreateInjectedSubagentTurn(trimmed), runCts);

            return Task.FromResult(Result<string, string>.AsValue(JsonSerializer.Serialize(new
            {
                subagentId,
                persistenceId = child.PersistenceId,
                status = "interrupted",
            })));
        }

        child.EnqueuePendingTurn(CreateInjectedSubagentTurn(trimmed));
        if (!child.HasActiveBackgroundRun)
        {
            var runCts = new CancellationTokenSource();
            child.AttachBackgroundRun(runCts);
            if (child.TryDequeuePendingTurn(out var next))
                KickOffChildPrompt(child, next, runCts);
            else
                runCts.Dispose();
        }

        return Task.FromResult(Result<string, string>.AsValue(JsonSerializer.Serialize(new
        {
            subagentId,
            persistenceId = child.PersistenceId,
            status = "queued",
        })));
    }

    /// <summary>Root-only: block until the host UI answers (composer Ask popover).</summary>
    public async Task<Result<string, string>> AskQuestionAsync(
        string questionsJson,
        CancellationToken cancellationToken = default)
    {
        if (Parent is not null)
            return Result<string, string>.AsError("AskQuestion: root sessions only.");

        var parsed = DysonAskQuestion.ParseQuestionsJson(questionsJson);
        if (parsed.IsError)
            return Result<string, string>.AsError(parsed.Error);

        TaskCompletionSource<Result<string, string>> tcs;
        lock (_askQuestionGate)
        {
            if (_askQuestionTcs is { Task.IsCompleted: false })
                return Result<string, string>.AsError("AskQuestion: another question set is already pending.");

            if (_promptUserDialogTcs is { Task.IsCompleted: false })
                return Result<string, string>.AsError("AskQuestion: a PromptUserDialog is already pending.");

            tcs = new TaskCompletionSource<Result<string, string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _askQuestionTcs = tcs;
            PendingAskQuestions = parsed.Value;
        }

        RaiseParentEventsChanged();

        try
        {
            using var reg = cancellationToken.Register(() =>
                tcs.TrySetResult(Result<string, string>.AsError("AskQuestion was cancelled.")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_askQuestionGate)
            {
                if (ReferenceEquals(_askQuestionTcs, tcs))
                {
                    _askQuestionTcs = null;
                    PendingAskQuestions = null;
                }
            }

            RaiseParentEventsChanged();
        }
    }

    /// <summary>Completes a pending root <see cref="AskQuestionAsync"/> with a pre-formatted answer block.</summary>
    public Result<string, string> RespondToAskQuestion(string formattedAnswers)
    {
        lock (_askQuestionGate)
        {
            if (_askQuestionTcs is null || _askQuestionTcs.Task.IsCompleted)
                return Result<string, string>.AsError("RespondToAskQuestion: no pending AskQuestion.");

            var body = formattedAnswers ?? "";
            _askQuestionTcs.TrySetResult(Result<string, string>.AsValue(body));
            PendingAskQuestions = null;
            _askQuestionTcs = null;
        }

        RaiseParentEventsChanged();
        return Result<string, string>.AsValue("ok");
    }

    /// <summary>L1 wrapper: <see cref="TriggerParentEventAsync"/> with kind askQuestion.</summary>
    public Task<Result<string, string>> AskQuestionFromParentAsync(
        string questionsJson,
        CancellationToken cancellationToken = default)
    {
        var parsed = DysonAskQuestion.ParseQuestionsJson(questionsJson);
        if (parsed.IsError)
            return Task.FromResult(Result<string, string>.AsError(parsed.Error));

        // Re-serialize normalized questions array as payload.
        var payload = JsonSerializer.Serialize(parsed.Value.Select(q => new
        {
            prompt = q.Prompt,
            options = q.Options,
            allowMultiple = q.AllowMultiple,
        }));

        return TriggerParentEventAsync(DysonAskQuestion.AskQuestionKind, payload, cancellationToken);
    }

    /// <summary>Root-only: block until the host UI picks a modal action (or Skip).</summary>
    public async Task<Result<string, string>> PromptUserDialogAsync(
        string dialogJson,
        CancellationToken cancellationToken = default)
    {
        if (Parent is not null)
            return Result<string, string>.AsError("PromptUserDialog: root sessions only.");

        var parsed = DysonPromptUserDialog.ParseDialogJson(dialogJson);
        if (parsed.IsError)
            return Result<string, string>.AsError(parsed.Error);

        TaskCompletionSource<Result<string, string>> tcs;
        lock (_askQuestionGate)
        {
            if (_promptUserDialogTcs is { Task.IsCompleted: false })
                return Result<string, string>.AsError("PromptUserDialog: another dialog is already pending.");

            if (_askQuestionTcs is { Task.IsCompleted: false })
                return Result<string, string>.AsError("PromptUserDialog: an AskQuestion is already pending.");

            tcs = new TaskCompletionSource<Result<string, string>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _promptUserDialogTcs = tcs;
            PendingUserDialog = parsed.Value;
        }

        RaiseParentEventsChanged();

        try
        {
            using var reg = cancellationToken.Register(() =>
                tcs.TrySetResult(Result<string, string>.AsError("PromptUserDialog was cancelled.")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_askQuestionGate)
            {
                if (ReferenceEquals(_promptUserDialogTcs, tcs))
                {
                    _promptUserDialogTcs = null;
                    PendingUserDialog = null;
                }
            }

            RaiseParentEventsChanged();
        }
    }

    /// <summary>Completes a pending root <see cref="PromptUserDialogAsync"/> with a pre-formatted JSON result.</summary>
    public Result<string, string> RespondToPromptUserDialog(string formattedResult)
    {
        lock (_askQuestionGate)
        {
            if (_promptUserDialogTcs is null || _promptUserDialogTcs.Task.IsCompleted)
                return Result<string, string>.AsError("RespondToPromptUserDialog: no pending PromptUserDialog.");

            var body = formattedResult ?? "";
            _promptUserDialogTcs.TrySetResult(Result<string, string>.AsValue(body));
            PendingUserDialog = null;
            _promptUserDialogTcs = null;
        }

        RaiseParentEventsChanged();
        return Result<string, string>.AsValue("ok");
    }

    /// <summary>L1 wrapper: <see cref="TriggerParentEventAsync"/> with kind promptUserDialog.</summary>
    public Task<Result<string, string>> PromptUserDialogFromParentAsync(
        string dialogJson,
        CancellationToken cancellationToken = default)
    {
        var parsed = DysonPromptUserDialog.ParseDialogJson(dialogJson);
        if (parsed.IsError)
            return Task.FromResult(Result<string, string>.AsError(parsed.Error));

        var payload = DysonPromptUserDialog.SerializeRequest(parsed.Value);
        return TriggerParentEventAsync(DysonPromptUserDialog.PromptUserDialogKind, payload, cancellationToken);
    }

    internal void CancelPendingParentEventWait(string reason)
    {
        var tcs = _parentEventWaitTcs;
        tcs?.TrySetResult(Result<string, string>.AsError(reason));

        if (Parent is null)
            return;

        foreach (var evt in Parent._pendingParentEvents.Values)
        {
            if (evt.SubagentId != Id || evt.Status != DysonParentEventStatus.Pending)
                continue;

            evt.Status = DysonParentEventStatus.Cancelled;
            evt.ReplyTcs.TrySetResult(Result<string, string>.AsError(reason));
        }

        Parent.RaiseParentEventsChanged();
    }

    internal bool HasActiveBackgroundRun
    {
        get
        {
            var cts = _runCts;
            return cts is not null && !cts.IsCancellationRequested;
        }
    }

    /// <summary>Queues a harness follow-up turn (completion confirm, report, inject, etc.).</summary>
    public void EnqueuePendingTurn(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _pendingTurns.Enqueue(turn);
    }

    /// <summary>Dequeues the next pending harness turn, if any.</summary>
    public bool TryDequeuePendingTurn(out DysonAgentTurn turn) =>
        _pendingTurns.TryDequeue(out turn!);

    /// <summary>True when at least one harness follow-up turn is still queued on the session.</summary>
    public bool HasPendingTurn => !_pendingTurns.IsEmpty;

    /// <summary>Drops all queued harness turns (e.g. on interrupt).</summary>
    public void ClearPendingTurns()
    {
        while (_pendingTurns.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// Turn currently inside <c>PromptWithTurnAsync</c> (nested via stack). Prefer over
    /// <see cref="TurnHistory"/>[^1] for phase guards — mid-prompt history appends (e.g.
    /// PlanResult) can displace the last history entry without ending the prompt.
    /// </summary>
    public DysonAgentTurn? InFlightPromptTurn =>
        _inFlightPromptStack.Count > 0 ? _inFlightPromptStack.Peek() : null;

    /// <summary>
    /// Marks <paramref name="turn"/> as the in-flight prompt turn until disposed.
    /// Nestable (DropContext inject inside another prompt). Call after <see cref="AddTurn"/>.
    /// </summary>
    public IDisposable BeginInFlightPrompt(DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        _inFlightPromptStack.Push(turn);
        return new InFlightPromptScope(this);
    }

    /// <summary>
    /// True while the in-flight turn is <see cref="DysonAgentTurnKind.TaskCompletionConfirm"/>
    /// (ConfirmTaskComplete / ContinueWork phase guard).
    /// </summary>
    public bool IsInTaskCompletionConfirmPhase =>
        IsInPhase(DysonAgentTurnKind.TaskCompletionConfirm);

    /// <summary>
    /// True while the in-flight turn is <see cref="DysonAgentTurnKind.RethinkToolUsage"/>
    /// (ResumeCurrentTask phase guard).
    /// </summary>
    public bool IsInRethinkToolUsagePhase =>
        IsInPhase(DysonAgentTurnKind.RethinkToolUsage);

    /// <summary>
    /// True while the in-flight turn is <see cref="DysonAgentTurnKind.ExpandThoughtProcess"/>
    /// (blocks nested ExpandThoughtProcess).
    /// </summary>
    public bool IsInExpandThoughtProcessPhase =>
        IsInPhase(DysonAgentTurnKind.ExpandThoughtProcess);

    /// <summary>
    /// True while the in-flight turn is <see cref="DysonAgentTurnKind.DropContext"/>
    /// (blocks nested DropContext inject).
    /// </summary>
    public bool IsInDropContextPhase =>
        IsInPhase(DysonAgentTurnKind.DropContext);

    private bool IsInPhase(DysonAgentTurnKind kind)
    {
        var inFlight = InFlightPromptTurn;
        if (inFlight is not null)
            return inFlight.Kind == kind;

        // History fallback: CompletedUtc must still be null (turn finished = not in phase).
        if (TurnHistory.Count == 0)
            return false;
        var current = TurnHistory[^1];
        return current.Kind == kind && current.CompletedUtc is null;
    }

    private sealed class InFlightPromptScope(DysonAgentSession session) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            session._inFlightPromptStack.Pop();
        }
    }

    /// <summary>
    /// Session override for max target context tokens.
    /// Null = inherit slug / harness default; 0 = Off (no DropContext inject).
    /// </summary>
    public int? MaxTargetContextTokens { get; set; }

    /// <summary>Current slug <c>DefaultMaxTargetContextTokens</c> (null = harness default).</summary>
    public int? SlugDefaultMaxTargetContextTokens { get; set; }

    /// <summary>
    /// Last provider-reported <c>prompt_tokens</c> / <c>input_tokens</c> from usage (optional UI secondary).
    /// </summary>
    public int? LastReportedPromptTokens { get; set; }

    /// <summary>
    /// Cascade: session override if set → slug default if set → harness 100K.
    /// 0 means Off / unlimited (no inject).
    /// </summary>
    public int ResolveEffectiveMaxTargetContextTokens() =>
        DysonMaxTargetContextTokens.Resolve(MaxTargetContextTokens, SlugDefaultMaxTargetContextTokens);

    /// <summary>
    /// Estimated tokens for the outbound Completions/Responses payload (idle: no in-flight rounds).
    /// </summary>
    public int EstimateOutgoingContextTokens() =>
        DysonOutgoingContextTokens.Count(this, TokenCounter);

    private void RaiseParentEventsChanged() => ParentEventsChanged?.Invoke(this, EventArgs.Empty);

    private static string BuildInjectedSubagentPrompt(string payload) =>
        """
        Harness injection: the parent sent instructions via TriggerSubagentEvent. Follow them and continue your task.

        ## Injected instructions
        """ + payload.Trim();

    private static DysonAgentTurn CreateInjectedSubagentTurn(string payload) =>
        new()
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = BuildInjectedSubagentPrompt(payload),
            StartedUtc = DateTime.UtcNow,
        };

    /// <summary>Normal turn for host/user text or BeginBuildPlan continuation.</summary>
    public static DysonAgentTurn CreateNormalTurn(string instruction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        return new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = instruction.Trim(),
            StartedUtc = DateTime.UtcNow,
        };
    }

    public Task<Result<string, string>> SubmitSubagentReportAsync(
        string summary,
        bool failed = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(summary))
            return Task.FromResult(Result<string, string>.AsError("SubmitSubagentReport: summary is required."));

        var incomplete = Todos
            .Where(t => t.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing)
            .Select(t => $"{t.TaskCode} ({t.DisplayName})={t.Status}")
            .ToArray();

        // Failed reports may leave todos incomplete (blocker handoff). Successful reports require all Complete.
        if (incomplete.Length > 0 && !failed)
        {
            return Task.FromResult(Result<string, string>.AsError(
                "SubmitSubagentReport: incomplete todos: " + string.Join("; ", incomplete)));
        }

        var trimmed = summary.Trim();
        var status = failed ? DysonSessionStatus.Failed : DysonSessionStatus.Completed;

        // Terminal handoff already accepted: reject retries (Failed→Completed supersede still allowed).
        if (Status == DysonSessionStatus.Completed
            || (Status == DysonSessionStatus.Failed && failed)
            || Status == DysonSessionStatus.Stopped)
        {
            return Task.FromResult(Result<string, string>.AsError(
                Status == DysonSessionStatus.Stopped
                    ? $"SubmitSubagentReport: session already {Status}."
                    : "SubmitSubagentReport: already submitted."));
        }

        if (!TryAcceptSubagentReport(status, trimmed))
        {
            // Race: another thread accepted between the check and TryAccept.
            return Task.FromResult(Result<string, string>.AsError(
                Status is DysonSessionStatus.Completed or DysonSessionStatus.Failed
                    ? "SubmitSubagentReport: already submitted."
                    : $"SubmitSubagentReport: session already {Status}."));
        }

        if (Parent is not null)
        {
            if (failed)
                Parent.NotifySubagentFailed(Id, trimmed, PersistenceId == Guid.Empty ? null : PersistenceId);
            else
                Parent.NotifySubagentCompleted(Id, trimmed, PersistenceId == Guid.Empty ? null : PersistenceId);
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
        if (status is not (DysonSessionStatus.Completed
            or DysonSessionStatus.Stopped
            or DysonSessionStatus.Failed
            or DysonSessionStatus.Interrupted))
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

    /// <summary>
    /// Accepts a child <c>SubmitSubagentReport</c>: first terminal mark, or supersede
    /// <see cref="DysonSessionStatus.Failed"/> with <see cref="DysonSessionStatus.Completed"/> only.
    /// Completed/Stopped stay locked; Failed→Failed is rejected.
    /// </summary>
    public bool TryAcceptSubagentReport(DysonSessionStatus status, string? summary)
    {
        if (status is not (DysonSessionStatus.Completed or DysonSessionStatus.Failed))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Must be Completed or Failed.");

        lock (_terminalGate)
        {
            if (Status is DysonSessionStatus.Completed
                or DysonSessionStatus.Stopped
                or DysonSessionStatus.Interrupted)
                return false;

            // Failed may only be superseded by Completed (harness premature fail → agent handoff).
            if (Status == DysonSessionStatus.Failed && status == DysonSessionStatus.Failed)
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

    /// <summary>
    /// Builds the first-turn prompt for a spawned child. All modes get
    /// <see cref="DysonAgentSystemPrompts.SubagentReportRequiredMandate"/>; Explore/Drone get extras.
    /// </summary>
    protected static string BuildChildFirstPrompt(string agentMode, string task, string? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DysonAgentSystemPrompts.SubagentReportRequiredMandate.Trim());
        sb.AppendLine();

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
    /// Fire-and-forget child turn; on unexpected failure marks Failed, persists, and notifies parent.
    /// </summary>
    protected static void KickOffChildPrompt(
        DysonAgentSession child,
        DysonAgentTurn turn,
        CancellationTokenSource runCts)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(runCts);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await child.PromptHarnessTurnAsync(turn, runCts.Token).ConfigureAwait(false);
                if (runCts.IsCancellationRequested)
                {
                    // Interrupt / Stop owns follow-up; still drain pending turns if child stays active.
                    TryDrainPendingTurn(child);
                    return;
                }

                if (child.IsTerminal)
                    return;

                if (TryDrainPendingTurn(child))
                    return;

                // A child with unfinished todos gets one harness reflection before its
                // ordinary missing-SubmitSubagentReport failure gate. If that reflection
                // itself does not report, this same path retains the existing failure behavior.
                if (DysonTaskEndReflectFlow.TryCreateForChild(child, out var reflection)
                    && reflection is not null)
                {
                    child.EnqueuePendingTurn(reflection);
                    if (TryDrainPendingTurn(child))
                        return;
                }

                var failSummary = ResolveKickOffFailureSummary(child, result);
                if (child.TryMarkTerminal(DysonSessionStatus.Failed, failSummary))
                {
                    await PersistKickOffFailureAsync(child, failSummary).ConfigureAwait(false);
                    child.Parent?.NotifySubagentFailed(
                        child.Id,
                        failSummary,
                        child.PersistenceId == Guid.Empty ? null : child.PersistenceId);
                }
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
                // StopSubagent / interrupt owns terminal state; drain queued turns if still active.
                TryDrainPendingTurn(child);
            }
            catch (Exception ex)
            {
                var failSummary = FormatKickOffExceptionSummary(ex);
                if (child.TryMarkTerminal(DysonSessionStatus.Failed, failSummary))
                {
                    await PersistKickOffFailureAsync(child, failSummary).ConfigureAwait(false);
                    child.Parent?.NotifySubagentFailed(
                        child.Id,
                        failSummary,
                        child.PersistenceId == Guid.Empty ? null : child.PersistenceId);
                }
            }
            finally
            {
                runCts.Dispose();
            }
        });
    }

    /// <summary>Starts the next queued pending turn when the child is still Active.</summary>
    private static bool TryDrainPendingTurn(DysonAgentSession child)
    {
        if (child.IsTerminal)
            return false;

        if (!child.TryDequeuePendingTurn(out var next))
            return false;

        var nextCts = new CancellationTokenSource();
        child.AttachBackgroundRun(nextCts);
        KickOffChildPrompt(child, next, nextCts);
        return true;
    }

    /// <summary>
    /// Non-empty failure reason for kickoff: PromptAsync error, else last turn snippet, else harness message.
    /// </summary>
    public static string ResolveKickOffFailureSummary(DysonAgentSession child, VoidResult<string> promptResult)
    {
        ArgumentNullException.ThrowIfNull(child);

        if (promptResult.IsError && !string.IsNullOrWhiteSpace(promptResult.Error))
            return promptResult.Error.Trim();

        var snippet = TryGetLastTurnFailureSnippet(child);
        if (!string.IsNullOrWhiteSpace(snippet))
            return snippet;

        return "Child finished without SubmitSubagentReport (no assistant output).";
    }

    /// <summary>Formats <c>{Type}: {Message}</c> (+ inner) for kickoff exception path.</summary>
    public static string FormatKickOffExceptionSummary(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var summary = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is { } inner && !string.IsNullOrWhiteSpace(inner.Message))
            summary += $" ({inner.GetType().Name}: {inner.Message})";
        return string.IsNullOrWhiteSpace(summary)
            ? "Child prompt failed with an unexpected exception."
            : summary;
    }

    private static string? TryGetLastTurnFailureSnippet(DysonAgentSession child, int maxChars = 500)
    {
        if (child.TurnHistory.Count == 0)
            return null;

        var turn = child.TurnHistory[^1];
        var text = !string.IsNullOrWhiteSpace(turn.AssistantText)
            ? turn.AssistantText
            : turn.StreamingPreview;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length <= maxChars)
            return trimmed;

        return trimmed[..maxChars] + "…";
    }

    /// <summary>
    /// Persist child Failed status + parent interrupt log (mirrors SubmitSubagentReport executor path).
    /// </summary>
    private static async Task PersistKickOffFailureAsync(DysonAgentSession child, string summary)
    {
        var store = child.SessionStore;
        if (store is null || child.PersistenceId == Guid.Empty)
            return;

        await store.UpdateSessionMetaAsync(
            new DysonSessionMetaUpdate
            {
                SessionId = child.PersistenceId,
                Status = DysonSessionStatus.Failed,
            }).ConfigureAwait(false);

        var statusLog = DysonSessionLogPayload.CreateEntry(
            child.PersistenceId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(DysonSessionStatus.Failed, summary));
        await store.AppendLogAsync(statusLog).ConfigureAwait(false);

        var parent = child.Parent;
        if (parent is null || parent.PersistenceId == Guid.Empty)
            return;

        var interruptLog = DysonSessionLogPayload.CreateEntry(
            parent.PersistenceId,
            DysonSessionLogKind.Interrupt,
            new DysonSessionLogInterrupt(
                DysonAgentInterruptKind.SubagentFailed.ToString(),
                SubagentId: child.Id,
                Summary: summary,
                PersistenceId: child.PersistenceId));
        await store.AppendLogAsync(interruptLog).ConfigureAwait(false);
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

    /// <summary>
    /// Raised by <see cref="EvaluateTaskLifecycle"/> when a root session is at a stable
    /// task-lifecycle boundary. Host should enqueue the matching harness turn or persist terminal.
    /// </summary>
    public event EventHandler<DysonTaskLifecycleEventArgs>? TaskLifecycle;

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
    protected IDysonSessionRepository? SessionStore { get; set; }

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
        MaxTargetContextTokens = state.Session.MaxTargetContextTokens;
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
                PlanRelativePath = row.PlanRelativePath,
                AssistantText = row.AssistantText,
                ToolHistoryOptimized = row.ToolHistoryOptimized,
                CompactToolHistory = row.CompactToolHistory,
                IsExcludedFromContext = row.IsExcludedFromContext,
                ContextSummary = row.ContextSummary,
                InterruptionReason = row.InterruptionReason,
                StartedUtc = row.CreatedUtc,
                CompletedUtc = row.CompletedUtc,
            };
            turn.RestoreReasoningLog(
                DysonReasoningLogSerializer.DeserializeOrSynthesize(row.ReasoningLogJson, row.ReasoningText));
            turn.RestoreSkillsUsed(DysonSkillsUsedSerializer.Deserialize(row.SkillsUsedJson));
            turn.RestoreUserImages(DysonUserImagesSerializer.Deserialize(row.UserImagesJson));
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

    /// <summary>Assigns <see cref="PersistenceId"/> after <see cref="IDysonSessionRepository.CreateSessionAsync"/>.</summary>
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
    /// Creates a DropContext turn (prune older noise when over max target).
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateDropContextTurn() =>
        DysonDropContextFlow.CreateTurn();

    /// <summary>
    /// Creates a FullSummarize turn (one session summary that replaces earlier turns).
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateFullSummarizeTurn() =>
        DysonFullSummarizeFlow.CreateTurn();

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
    /// Creates a TaskEndReflect turn (incomplete todos after a substantive root turn).
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreateTaskEndReflectTurn() =>
        DysonTaskEndReflectFlow.CreateTurn(Todos);

    /// <summary>
    /// Creates a BugReview orchestration turn for a runnable review level (Low/Medium).
    /// Does not append to <see cref="TurnHistory"/>. Host must call
    /// <see cref="DysonTaskLifecycleFlow.IsReviewRunnable"/> first.
    /// </summary>
    public DysonAgentTurn CreateBugReviewTurn(DysonAutomaticCodeReviewLevel level) =>
        DysonTaskLifecycleFlow.CreateBugReviewTurn(level);

    /// <summary>Creates action-aware automatic review orchestration with its initial worktree scope.</summary>
    public DysonAgentTurn CreateBugReviewTurn(
        DysonAutomaticCodeReviewLevel level,
        DysonAutomaticCodeReviewAction action,
        string? worktreeScope) =>
        DysonTaskLifecycleFlow.CreateBugReviewTurn(level, action, worktreeScope);

    /// <summary>
    /// Evaluates root task-lifecycle after a completed turn (or on restore / last-child-done).
    /// Pass host-side <c>DysonSubagentHostLogic.HasActiveDescendant(session)</c> for the
    /// active-descendant gate (the engine also walks <see cref="SubSessions"/>).
    /// Pass <paramref name="hasQueuedFollowUp"/> when the host/runtime already has a
    /// follow-up prompt queued. Raises <see cref="TaskLifecycle"/> when an action is required.
    /// </summary>
    public DysonTaskLifecycleDecision EvaluateTaskLifecycle(
        bool hasActiveDescendant,
        bool hasQueuedFollowUp = false)
    {
        var decision = DysonTaskLifecycleFlow.Evaluate(this, hasActiveDescendant, hasQueuedFollowUp);
        if (decision.Kind is { } kind)
            TaskLifecycle?.Invoke(this, new DysonTaskLifecycleEventArgs { Kind = kind });
        return decision;
    }

    /// <summary>
    /// Creates a PlanResult turn after SubmitPlan (no auto LLM).
    /// Does not append to <see cref="TurnHistory"/>.
    /// </summary>
    public DysonAgentTurn CreatePlanResultTurn(string planRelativePath, string title) =>
        DysonPlanResultFlow.CreateTurn(planRelativePath, title);

    /// <summary>
    /// Appends a completed PlanResult turn and raises <see cref="TurnAdded"/> for host persistence.
    /// </summary>
    public DysonAgentTurn AppendPlanResultTurn(string planRelativePath, string title)
    {
        var turn = CreatePlanResultTurn(planRelativePath, title);
        AddTurn(turn);
        return turn;
    }

    /// <summary>
    /// Appends a completed UI-only <see cref="DysonAgentTurnKind.DisplayInfo"/> turn
    /// (message in <see cref="DysonAgentTurn.AssistantText"/>). No inference.
    /// </summary>
    public DysonAgentTurn AppendDisplayInfoTurn(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var now = DateTime.UtcNow;
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.DisplayInfo,
            AssistantText = message.Trim(),
            StartedUtc = now,
            CompletedUtc = now,
        };
        AddTurn(turn);
        return turn;
    }

    /// <summary>
    /// Appends a completed <see cref="DysonAgentTurnKind.ModeSwitch"/> turn
    /// (<see cref="DysonAgentTurn.Instruction"/> = <c>From→To</c>, banner in
    /// <see cref="DysonAgentTurn.AssistantText"/>). No inference. Included in provider
    /// transcripts as a short harness user message.
    /// </summary>
    public DysonAgentTurn AppendModeSwitchTurn(string fromMode, string toMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(toMode);
        var from = fromMode.Trim();
        var to = toMode.Trim();
        var now = DateTime.UtcNow;
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.ModeSwitch,
            Instruction = $"{from}→{to}",
            AssistantText = $"Switched to {to}",
            StartedUtc = now,
            CompletedUtc = now,
        };
        AddTurn(turn);
        return turn;
    }

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
    /// Runs a pre-built harness/user turn (preserves <see cref="DysonAgentTurn.Kind"/>).
    /// Used for completion confirm / report / continuation and host queue drain.
    /// </summary>
    public abstract Task<VoidResult<string>> PromptHarnessTurnAsync(
        DysonAgentTurn turn,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as <see cref="PromptHarnessTurnAsync(DysonAgentTurn, CancellationToken)"/> with
    /// workspace-relative paths appended to the round-0 user message (<c>Attached paths:</c>).
    /// Default ignores paths; OpenAI-compatible sessions honor them.
    /// </summary>
    public virtual Task<VoidResult<string>> PromptHarnessTurnAsync(
        DysonAgentTurn turn,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        return PromptHarnessTurnAsync(turn, cancellationToken);
    }

    /// <summary>
    /// Work-mode Build plan: creates a <see cref="DysonAgentTurnKind.BeginBuildPlan"/> turn
    /// and runs the same tool/reply loop as <see cref="PromptAsync"/>.
    /// Optional <paramref name="reportBlocks"/> fold buffered Explore reports into the Instruction.
    /// </summary>
    public abstract Task<VoidResult<string>> PromptBeginBuildPlanAsync(
        string planRelativePath,
        IReadOnlyList<string>? reportBlocks = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parent auto-turn after subagent completion: creates a
    /// <see cref="DysonAgentTurnKind.SubagentReportProcessing"/> turn and runs the same
    /// tool/reply loop as <see cref="PromptAsync"/> (analyze report, then continue — one turn).
    /// </summary>
    public abstract Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
        DysonAgentInterrupt interrupt,
        string? title = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompt-queue drain path when Instruction is already built
    /// (<see cref="DysonAgentTurnKind.SubagentReportProcessing"/>).
    /// </summary>
    public abstract Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
        string instruction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parent auto-turn after a subscribed long-running shell exits: creates a
    /// <see cref="DysonAgentTurnKind.ShellExited"/> turn with auto-read tail and runs the same
    /// tool/reply loop as <see cref="PromptAsync"/>.
    /// </summary>
    public abstract Task<VoidResult<string>> PromptShellExitedAsync(
        DysonAgentInterrupt interrupt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Prefer draining the interrupt queue (see <see cref="WaitForInterruptAsync"/> /
    /// <see cref="TryDequeueInterrupt"/>) when mapping notify events, so Work’s async loop
    /// observes subagent completions without busy-polling.
    /// </summary>
    public abstract Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
        CancellationToken cancellationToken = default);
}
