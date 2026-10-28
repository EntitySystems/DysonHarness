namespace DysonHarness;

/// <summary>
/// OpenAI-compatible agent session: Completions or Responses tool loop with cache-friendly requests.
/// </summary>
public sealed class OpenAiCompatibleAgentSession : DysonAgentSession
{
    /// <summary>Default tool-round budget per turn (non-Explore modes).</summary>
    public const int MaxToolRounds = 50;

    /// <summary>Explore-mode tool-round budget per turn.</summary>
    public const int MaxToolRoundsExplore = 120;

    /// <summary>Resolves the tool-round budget for <paramref name="mode"/>.</summary>
    public static int ResolveMaxToolRounds(string mode) =>
        string.Equals(mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase)
            ? MaxToolRoundsExplore
            : MaxToolRounds;

    /// <summary>Resolves the tool-round budget for this session's <see cref="DysonAgentSession.Mode"/>.</summary>
    public int ResolveMaxToolRounds() => ResolveMaxToolRounds(Mode);

    /// <summary>
    /// Soft-closes <paramref name="turn"/> after tool-round budget exhaustion and optionally enqueues
    /// a <see cref="DysonAgentTurnKind.RethinkToolUsage"/> follow-up. Always returns Success
    /// (never an exhaustion error string). Does not enqueue rethink when already on rethink, when
    /// session mode is Explore (Explore uses a no-tools recap instead), or when session mode is
    /// Meta Agent (no ResumeCurrentTask). Meta Agent Drone still enqueues rethink.
    /// </summary>
    public static VoidResult<string> SoftPauseAfterToolLoopExhaustion(
        DysonAgentSession session,
        DysonAgentTurn turn,
        int maxRounds)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        // Soft-pause has no new model reply — don't invent segments; clear any leftover preview.
        turn.ClearStreamingPreview();
        turn.ClearReasoningPreview();
        turn.FinalizeIncompleteTools(OpenAiCacheFriendlyTranscriptBuilder.IncompleteToolResultContent);

        var isRethink = turn.Kind == DysonAgentTurnKind.RethinkToolUsage;
        var isExplore = string.Equals(
            session.Mode,
            DysonAgentModes.Explore,
            StringComparison.OrdinalIgnoreCase);
        var isMetaAgent = DysonSessionPolicy.IsMetaAgent(session.Mode);

        string text;
        if (isRethink)
        {
            text =
                $"# Rethink budget exhausted\n\nThis rethink turn hit the {maxRounds}-round tool budget without ResumeCurrentTask or a concluding reply. No further rethink was scheduled.";
        }
        else if (isExplore)
        {
            text = DysonRethinkToolUsageFlow.ExploreBudgetExhaustedFallback;
        }
        else if (isMetaAgent)
        {
            text =
                $"# Tool rounds paused\n\nTool loop reached the {maxRounds}-round budget without a final reply.";
        }
        else
        {
            text =
                $"# Tool rounds paused\n\nTool loop reached the {maxRounds}-round budget without a final reply. A rethink turn will analyze whether to call ResumeCurrentTask or stop.";
        }

        ApplyAssistantText(turn, text);
        turn.FinishStreaming();
        turn.FinishReasoningStreaming();

        if (!isRethink && !isExplore && !isMetaAgent)
            session.EnqueuePendingTurn(DysonRethinkToolUsageFlow.CreateTurn());

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Soft-closes <paramref name="turn"/> after a tool result with
    /// <see cref="DysonToolCallResult.EndsCurrentTurn"/> (e.g. ExpandThoughtProcess / StartNewTurn).
    /// Prefers non-empty <paramref name="modelContent"/> (same-round reply text) over a harness note.
    /// Always returns Success; does not enqueue rethink.
    /// </summary>
    public static VoidResult<string> SoftCloseAfterEndsCurrentTurn(
        DysonAgentTurn turn,
        string? endingToolName = null,
        string? modelContent = null)
    {
        ArgumentNullException.ThrowIfNull(turn);

        // Tool-loop caller commits Thought for this round before SoftClose; leftover preview only.
        CommitReasoningRound(turn, reply: null, roundIndex: 0, isFinalAssistant: true);
        turn.ClearStreamingPreview();
        turn.ClearReasoningPreview();

        var text = !string.IsNullOrWhiteSpace(modelContent)
            ? modelContent
            : SoftCloseHarnessNote(endingToolName);
        ApplyAssistantText(turn, text);
        turn.FinishStreaming();
        turn.FinishReasoningStreaming();
        return VoidResult<string>.Success;
    }

    private static string SoftCloseHarnessNote(string? endingToolName) =>
        endingToolName switch
        {
            "StartNewTurn" =>
                "# Starting new turn\n\n" +
                "StartNewTurn was called; this turn ends. A Normal turn with the provided instructions will run next.",
            "ExpandThoughtProcess" =>
                "# Expanding thought process\n\n" +
                "ExpandThoughtProcess was called; this turn ends. A reformulation turn will run next.",
            "SubmitSubagentReport" =>
                "# Report accepted\n\n" +
                "SubmitSubagentReport was accepted; this turn ends.",
            _ =>
                "# Turn ended\n\n" +
                "An end-turn tool was called; this turn ends.",
        };

    private readonly IDysonSessionRepository? _store;
    private readonly HttpClient _http;
    private string _workDirectoryPath;
    private Guid _workDirectoryId;
    private readonly OpenAiCompletionsClient _completions;
    private readonly OpenAiResponsesClient _responses;
    private readonly IDysonModelRepository? _models;
    private readonly IDysonUsageAnalyticsRepository? _usageAnalytics;
    private readonly string _workDirectoryName;
    private readonly string _registeredWorkDirectoryPath;
    private readonly IDysonPlanRepository? _plans;
    // ponytail: one hop per prompt; Explore recap shares this so a main-loop hop is not repeated
    private bool _fallbackAppliedThisTurn;
    // Per turn, not one field: two concurrent turns can both pause before either compacts.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _pausedForCompactionTurnIds = new();

    public OpenAiCompatibleAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        string workDirectoryAbsolutePath,
        IDysonSessionRepository? store = null,
        Guid workDirectoryId = default,
        IDysonModelRepository? models = null,
        string? systemPromptSuffix = null,
        IDysonUsageAnalyticsRepository? usageAnalytics = null,
        string workDirectoryName = "",
        string? registeredWorkDirectoryAbsolutePath = null,
        IDysonPlanRepository? plans = null)
        : base(agentMode, config, provider, systemPromptSuffix)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workDirectoryPath = Path.GetFullPath(workDirectoryAbsolutePath);
        _registeredWorkDirectoryPath = string.IsNullOrWhiteSpace(registeredWorkDirectoryAbsolutePath)
            ? _workDirectoryPath
            : Path.GetFullPath(registeredWorkDirectoryAbsolutePath);
        RegisteredWorkDirectoryAbsolutePath = _registeredWorkDirectoryPath;
        _store = store;
        SessionStore = store;
        _workDirectoryId = workDirectoryId;
        _models = models;
        _usageAnalytics = usageAnalytics;
        _workDirectoryName = workDirectoryName ?? "";
        _plans = plans;
        _completions = new OpenAiCompletionsClient(_http);
        _responses = new OpenAiResponsesClient(_http);
    }

    public OpenAiCompatibleAgentProvider OpenAiProvider => (OpenAiCompatibleAgentProvider)Provider;

    public string WorkDirectoryPath => _workDirectoryPath;

    public Guid WorkDirectoryId => _workDirectoryId;

    /// <summary>Rebinds native workspace root (registered checkout vs session worktree).</summary>
    public override void RebindWorkDirectoryPath(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        _workDirectoryPath = Path.GetFullPath(absolutePath);
    }

    public static async Task<Result<OpenAiCompatibleAgentSession, string>> CreateAsync(
        IDysonSessionRepository store,
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        Guid workDirectoryId,
        string workDirectoryAbsolutePath,
        string agentMode = DysonAgentModes.Work,
        DysonAgentSessionConfig? config = null,
        string? title = null,
        IDysonModelRepository? models = null,
        IDysonUsageAnalyticsRepository? usageAnalytics = null,
        string workDirectoryName = "",
        bool worktreeEnabled = false,
        IDysonPlanRepository? plans = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);

        if (workDirectoryId == Guid.Empty)
            return Result<OpenAiCompatibleAgentSession, string>.AsError("Work directory is required.");

        config ??= new DysonAgentSessionConfig();
        var providerKind = DysonProviderKinds.EffectiveKind(
            provider.ProviderKind, provider.BaseUrl, provider.ApiKey);
        var suffix = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                    models, providerKind, workDirectoryAbsolutePath, cancellationToken)
                .ConfigureAwait(false),
            DysonAgentSystemPrompts.BuildWorktreePromptBlock(
                worktreeEnabled,
                worktreeAbsolutePath: null,
                worktreeBranch: null,
                workDirectoryAbsolutePath));
        var session = new OpenAiCompatibleAgentSession(
            agentMode, config, provider, http, workDirectoryAbsolutePath, store, workDirectoryId, models,
            suffix, usageAnalytics, workDirectoryName, plans: plans);
        session.WorktreeEnabled = worktreeEnabled;
        session.ConfigureRootInterAgentTools();
        session.SlugDefaultMaxTargetContextTokens = provider.DefaultMaxTargetContextTokens;
        var initialTitle = title ?? "New session";
        session.SetDisplayTitle(initialTitle);

        var create = await store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = agentMode,
                ModelSlugId = provider.SlugId,
                WorkDirectoryId = workDirectoryId,
                ReasoningEffort = provider.ReasoningEffort,
                MaxTargetContextTokens = null,
                McpAccessMode = config.McpAccessMode,
                Title = initialTitle,
                SystemPromptSnapshot = session.SystemPrompt,
                Status = DysonSessionStatus.Active,
                WorktreeEnabled = worktreeEnabled,
            },
            cancellationToken).ConfigureAwait(false);

        if (create.IsError)
            return Result<OpenAiCompatibleAgentSession, string>.AsError(create.Error);

        session.SetPersistenceId(create.Value);

        var createdLog = DysonSessionLogPayload.CreateEntry(
            create.Value,
            DysonSessionLogKind.SessionCreated,
            new DysonSessionLogSessionCreated(create.Value, agentMode, RuntimeId: 0));

        var append = await store.AppendLogAsync(createdLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<OpenAiCompatibleAgentSession, string>.AsError(append.Error);

        return Result<OpenAiCompatibleAgentSession, string>.AsValue(session);
    }

    public static async Task<Result<OpenAiCompatibleAgentSession, string>> LoadAsync(
        IDysonSessionRepository store,
        Guid sessionId,
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        string workDirectoryAbsolutePath,
        DysonAgentSessionConfig? config = null,
        IDysonModelRepository? models = null,
        bool appendResumeLog = true,
        IDysonUsageAnalyticsRepository? usageAnalytics = null,
        string workDirectoryName = "",
        string? registeredWorkDirectoryAbsolutePath = null,
        IDysonPlanRepository? plans = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);

        var full = await store.GetFullSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (full.IsError)
            return Result<OpenAiCompatibleAgentSession, string>.AsError(full.Error);

        var state = full.Value;
        config ??= new DysonAgentSessionConfig
        {
            McpAccessMode = state.Session.McpAccessMode,
        };

        var registered = registeredWorkDirectoryAbsolutePath ?? workDirectoryAbsolutePath;
        var providerKind = DysonProviderKinds.EffectiveKind(
            provider.ProviderKind, provider.BaseUrl, provider.ApiKey);
        var suffix = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                    models, providerKind, workDirectoryAbsolutePath, cancellationToken)
                .ConfigureAwait(false),
            DysonAgentSystemPrompts.BuildWorktreePromptBlock(
                state.Session.WorktreeEnabled,
                state.Session.WorktreeAbsolutePath,
                state.Session.WorktreeBranch,
                registered));
        var session = new OpenAiCompatibleAgentSession(
            state.Session.AgentMode,
            config,
            provider,
            http,
            workDirectoryAbsolutePath,
            store,
            state.Session.WorkDirectoryId ?? Guid.Empty,
            models,
            suffix,
            usageAnalytics,
            workDirectoryName,
            registeredWorkDirectoryAbsolutePath: registered,
            plans: plans);
        session.RestoreFromPersisted(state);
        session.SlugDefaultMaxTargetContextTokens = provider.DefaultMaxTargetContextTokens;
        if (state.Session.ParentSessionId is null)
            session.ConfigureRootInterAgentTools();

        if (appendResumeLog)
        {
            var resumedLog = DysonSessionLogPayload.CreateEntry(
                sessionId,
                DysonSessionLogKind.SessionResumed,
                new DysonSessionLogSessionResumed(sessionId));

            var append = await store.AppendLogAsync(resumedLog, cancellationToken).ConfigureAwait(false);
            if (append.IsError)
                return Result<OpenAiCompatibleAgentSession, string>.AsError(append.Error);
        }

        return Result<OpenAiCompatibleAgentSession, string>.AsValue(session);
    }

    public override async Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
        string agentMode,
        string task,
        string? context = null,
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
        string? modelSlug = null,
        string? reasoningEffort = null,
        IReadOnlyList<string>? contextFiles = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);

        var gate = ValidateSubagentSpawn(Mode, agentMode, Config.CustomAgents);
        if (gate.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(gate.Error);

        if (_store is null)
            return Result<DysonStartSubagentResult, string>.AsError("Session store is required to spawn subagents.");

        if (PersistenceId == Guid.Empty)
            return Result<DysonStartSubagentResult, string>.AsError("Parent session must be persisted before spawning.");

        if (_workDirectoryId == Guid.Empty)
            return Result<DysonStartSubagentResult, string>.AsError("Work directory is required to spawn subagents.");

        var resolved = await ResolveChildProviderAsync(agentMode, modelSlug, reasoningEffort, cancellationToken)
            .ConfigureAwait(false);
        if (resolved.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(resolved.Error);

        var childProvider = resolved.Value;

        var firstTurn = DysonSessionInitialization.CreateTurn(BuildChildFirstPrompt(Mode, agentMode, task, context));
        var attached = await AttachContextFilesToChildTurnAsync(
                firstTurn,
                contextFiles,
                _workDirectoryPath,
                Config.PluginContributions,
                cancellationToken)
            .ConfigureAwait(false);
        if (attached.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(attached.Error);

        var (isolateWorktree, suppressWorktree) = ResolveMetaAgentDroneWorktree(agentMode);
        var childWorktreeEnabled = suppressWorktree ? false : isolateWorktree || WorktreeEnabled;
        var childWorktreePath = suppressWorktree || isolateWorktree ? null : WorktreeAbsolutePath;
        var childWorktreeBranch = suppressWorktree || isolateWorktree ? null : WorktreeBranch;
        var childWorkDirectory = suppressWorktree
            ? ResolveMetaAgentDroneSuppressWorkDirectory(_registeredWorkDirectoryPath)
            : _workDirectoryPath;

        var providerKind = DysonProviderKinds.EffectiveKind(
            childProvider.ProviderKind, childProvider.BaseUrl, childProvider.ApiKey);
        var worktreePrompt = suppressWorktree
            ? MetaAgentDroneSuppressPromptBlock()
            : DysonAgentSystemPrompts.BuildWorktreePromptBlock(
                childWorktreeEnabled, childWorktreePath, childWorktreeBranch, _registeredWorkDirectoryPath);
        var suffix = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                    _models, providerKind, childWorkDirectory, cancellationToken)
                .ConfigureAwait(false),
            worktreePrompt);

        var child = new OpenAiCompatibleAgentSession(
            agentMode,
            Config,
            childProvider,
            _http,
            childWorkDirectory,
            _store,
            _workDirectoryId,
            _models,
            suffix,
            _usageAnalytics,
            _workDirectoryName,
            registeredWorkDirectoryAbsolutePath: _registeredWorkDirectoryPath,
            plans: _plans);
        child.WorktreeEnabled = childWorktreeEnabled;
        child.WorktreeAbsolutePath = childWorktreePath;
        child.WorktreeBranch = childWorktreeBranch;

        RegisterSubagent(child);

        async Task<Result<DysonStartSubagentResult, string>> FailRegisteredChild(string error)
        {
            await AbandonRegisteredChildAsync(child, cancellationToken).ConfigureAwait(false);
            return Result<DysonStartSubagentResult, string>.AsError(error);
        }

        var title = TitleFromTask(task);
        child.SetDisplayTitle(title);
        child.SlugDefaultMaxTargetContextTokens = childProvider.DefaultMaxTargetContextTokens;

        var create = await _store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = child.Id,
                ParentSessionId = PersistenceId,
                AgentMode = agentMode,
                ModelSlugId = childProvider.SlugId,
                WorkDirectoryId = _workDirectoryId,
                ReasoningEffort = childProvider.ReasoningEffort,
                MaxTargetContextTokens = null,
                McpAccessMode = Config.McpAccessMode,
                Title = title,
                SystemPromptSnapshot = child.SystemPrompt,
                Status = DysonSessionStatus.Active,
                WorktreeEnabled = childWorktreeEnabled,
                WorktreeAbsolutePath = childWorktreePath,
                WorktreeBranch = childWorktreeBranch,
            },
            cancellationToken).ConfigureAwait(false);

        if (create.IsError)
            return await FailRegisteredChild(create.Error).ConfigureAwait(false);

        child.SetPersistenceId(create.Value);

        if (isolateWorktree)
        {
            var bound = await child.BindOwnWorktreeAsync(_registeredWorkDirectoryPath, cancellationToken)
                .ConfigureAwait(false);
            if (bound.IsError)
                return await FailRegisteredChild(bound.Error).ConfigureAwait(false);

            var reboundSuffix = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
                await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                        _models, providerKind, child.WorkDirectoryPath, cancellationToken)
                    .ConfigureAwait(false),
                DysonAgentSystemPrompts.BuildWorktreePromptBlock(
                    true, child.WorktreeAbsolutePath, child.WorktreeBranch, _registeredWorkDirectoryPath));
            child.ReplaceSystemPromptSuffix(reboundSuffix);

            var persist = await _store.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = child.PersistenceId,
                        UpdateWorktreeEnabled = true,
                        WorktreeEnabled = true,
                        UpdateWorktreeLocation = true,
                        WorktreeAbsolutePath = child.WorktreeAbsolutePath,
                        WorktreeBranch = child.WorktreeBranch,
                        SystemPromptSnapshot = child.SystemPrompt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (persist.IsError)
                return await FailRegisteredChild(persist.Error).ConfigureAwait(false);
        }

        if (initialTodos is { Count: > 0 })
        {
            var seeded = await child.ReplaceTodosAsync(initialTodos, cancellationToken).ConfigureAwait(false);
            if (seeded.IsError)
                return await FailRegisteredChild(seeded.Error).ConfigureAwait(false);
        }

        var createdLog = DysonSessionLogPayload.CreateEntry(
            create.Value,
            DysonSessionLogKind.SessionCreated,
            new DysonSessionLogSessionCreated(create.Value, agentMode, RuntimeId: child.Id));

        var append = await _store.AppendLogAsync(createdLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return await FailRegisteredChild(append.Error).ConfigureAwait(false);

        var runCts = new CancellationTokenSource();
        child.AttachBackgroundRun(runCts);
        KickOffChildPrompt(
            child,
            firstTurn,
            runCts);

        AppendLog($"started subagent {child.Id} ({agentMode}): {title}");

        return Result<DysonStartSubagentResult, string>.AsValue(new DysonStartSubagentResult
        {
            SubagentId = child.Id,
            PersistenceId = child.PersistenceId,
            AgentMode = agentMode,
            Title = title,
            ModelSlug = childProvider.Slug,
            ModelLabel = $"{childProvider.DisplayAlias} · {childProvider.ProviderDisplayName} / {childProvider.Slug}",
        });
    }

    private async Task<Result<OpenAiCompatibleAgentProvider, string>> ResolveChildProviderAsync(
        string agentMode,
        string? modelSlug,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        if (await Config.TryGetSubagentDefaultWhenSlugOmittedAsync(modelSlug, agentMode, _models, cancellationToken)
                .ConfigureAwait(false)
            is OpenAiCompatibleAgentProvider modeDefault)
        {
            if (reasoningEffort is null)
                return Result<OpenAiCompatibleAgentProvider, string>.AsValue(modeDefault);

            return Result<OpenAiCompatibleAgentProvider, string>.AsValue(
                modeDefault.WithReasoningEffort(reasoningEffort));
        }

        if (string.IsNullOrWhiteSpace(modelSlug))
        {
            if (reasoningEffort is null)
                return Result<OpenAiCompatibleAgentProvider, string>.AsValue(OpenAiProvider);

            return Result<OpenAiCompatibleAgentProvider, string>.AsValue(
                OpenAiProvider.WithReasoningEffort(reasoningEffort));
        }

        return await ResolveOpenAiSlugProviderAsync(modelSlug, reasoningEffort, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    internal override Task<Result<OpenAiCompatibleAgentProvider, string>> ResolveSwitchProviderAsync(
        OpenAiCompatibleAgentProvider current,
        string? modelSlug,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        if (modelSlug is not null)
            return ResolveOpenAiSlugProviderAsync(modelSlug, reasoningEffort, cancellationToken);

        return Task.FromResult(Result<OpenAiCompatibleAgentProvider, string>.AsValue(
            current.WithReasoningEffort(reasoningEffort)));
    }

    /// <summary>Slug or display alias to a fresh OpenAI-compatible provider. Null effort = the slug default.</summary>
    private async Task<Result<OpenAiCompatibleAgentProvider, string>> ResolveOpenAiSlugProviderAsync(
        string modelSlug,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        if (_models is null)
        {
            return Result<OpenAiCompatibleAgentProvider, string>.AsError(
                "Model store is required to resolve modelSlug.");
        }

        var found = await _models.FindSlugByNameAsync(modelSlug.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (found.IsError)
            return Result<OpenAiCompatibleAgentProvider, string>.AsError(found.Error);

        var slug = found.Value;
        var provider = slug.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);

        if (!string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            return Result<OpenAiCompatibleAgentProvider, string>.AsError(
                $"modelSlug '{modelSlug.Trim()}' is not OpenAI-compatible (same provider kind as parent required).");
        }

        return Result<OpenAiCompatibleAgentProvider, string>.AsValue(
            new OpenAiCompatibleAgentProvider(slug, reasoningEffort));
    }

    public override Task<VoidResult<string>> LoadFunctionalContextAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptAsync(
        string prompt,
        CancellationToken cancellationToken = default) =>
        PromptAsync(prompt, [], cancellationToken);

    public override Task<VoidResult<string>> PromptAsync(
        string prompt,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(filePaths);

        var turn = TurnHistory.Count == 0
            ? DysonSessionInitialization.CreateTurn(prompt)
            : new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = prompt,
                StartedUtc = DateTime.UtcNow,
            };

        return PromptWithTurnAsync(turn, filePaths, cancellationToken);
    }

    public override Task<VoidResult<string>> PromptHarnessTurnAsync(
        DysonAgentTurn turn,
        CancellationToken cancellationToken = default) =>
        PromptHarnessTurnAsync(turn, [], cancellationToken);

    public override Task<VoidResult<string>> PromptHarnessTurnAsync(
        DysonAgentTurn turn,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(filePaths);
        return PromptWithTurnAsync(turn, filePaths, cancellationToken);
    }

    public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
        string planRelativePath,
        IReadOnlyList<string>? reportBlocks = null,
        CancellationToken cancellationToken = default)
    {
        var turn = DysonBeginBuildPlanFlow.CreateTurn(planRelativePath, reportBlocks);
        return PromptWithTurnAsync(turn, [], cancellationToken);
    }

    public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
        DysonAgentInterrupt interrupt,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        var turn = DysonSubagentReportPrompt.CreateTurn(interrupt, title);
        return PromptWithTurnAsync(turn, [], cancellationToken);
    }

    public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
        string instruction,
        CancellationToken cancellationToken = default)
    {
        var turn = DysonSubagentReportPrompt.CreateTurn(instruction);
        return PromptWithTurnAsync(turn, [], cancellationToken);
    }

    public override async Task<VoidResult<string>> PromptShellExitedAsync(
        DysonAgentInterrupt interrupt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interrupt);

        var workDir = interrupt.WorkDirectoryId ?? _workDirectoryId;
        var shellId = interrupt.LongRunningShellId
            ?? throw new ArgumentException("LongRunningShellId is required.", nameof(interrupt));
        var maxChars = interrupt.IncludeTailMaxChars > 0
            ? interrupt.IncludeTailMaxChars
            : DysonLongRunningShellExitedFlow.DefaultIncludeTailMaxChars;

        if (!DysonLongRunningShellRegistry.TryGet(workDir, shellId, out var shell) || shell is null)
            return new VoidResult<string>($"Long-running shell #{shellId} not found.");

        var info = shell.ToInfo();
        var tailResult = await DysonLongRunningShellRegistry
            .ReadTailAsync(workDir, shellId, maxChars, sinceOffset: null, timeoutMs: 0, cancellationToken)
            .ConfigureAwait(false);
        var tailText = tailResult.IsError ? null : tailResult.Value.Text;

        var turn = DysonLongRunningShellExitedFlow.CreateTurn(interrupt, info, tailText);
        return await PromptWithTurnAsync(turn, [], cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> PromptWithTurnAsync(
        DysonAgentTurn turn,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(filePaths);

        // A child turn the host runs (report processing, queued continuations) has no KickOff CTS.
        // Attach a linked one so StopSubagent / inject cancel it and liveness sees it as busy.
        CancellationTokenSource? hostRunCts = null;
        if (Parent is not null && !HasActiveBackgroundRun)
        {
            hostRunCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            AttachBackgroundRun(hostRunCts);
            cancellationToken = hostRunCts.Token;
        }

        try
        {
            return await PromptWithTurnCoreAsync(turn, filePaths, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (hostRunCts is not null)
            {
                DetachBackgroundRun(hostRunCts);
                hostRunCts.Dispose();
            }
        }
    }

    private async Task<VoidResult<string>> PromptWithTurnCoreAsync(
        DysonAgentTurn turn,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        _fallbackAppliedThisTurn = false;
        ApplyUiTheme(Config.UiTheme);
        var preserveReasoning = await RefreshPreserveReasoningTracesAsync(cancellationToken).ConfigureAwait(false);

        // Compaction before the next provider request so the new prefix stays byte-stable.
        await OptimizeContextIfEnabledAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A trip not compacted yet (failed summary, cancelled end of turn, restart mid-trip)
            // summarizes before anything of this turn is sent.
            if (DysonCompactionFlow.CanCompact(this, turn)
                && await RefreshCompactionPendingAsync(cancellationToken).ConfigureAwait(false))
            {
                var compacted = await DysonCompactionFlow
                    .RunAsync(this, endedTurn: null, resume: false, RunCompactionSummaryTurnAsync, cancellationToken)
                    .ConfigureAwait(false);
                if (compacted.IsError)
                    return compacted;
            }

            var result = await RunTurnLoopAsync(turn, filePaths, preserveReasoning, cancellationToken)
                .ConfigureAwait(false);
            return await CompactAtTurnEndAsync(turn, result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VoidResult<string>("Prompt was cancelled.");
        }
        finally
        {
            EndLiveContextTokens();
        }
    }

    /// <summary>
    /// Shared end-of-turn check for every successful exit of the tool loop (final reply, end-turn tool,
    /// budget soft pause, child report). A turn paused at a safe point summarizes and queues a Continuation.
    /// Any other turn summarizes when it ended over the limit or a trip is pending. A successful summary
    /// drops queued harness follow-ups (StartNewTurn, rethink) and queues one fresh Continuation only when
    /// something was dropped or the turn was paused. A cancelled run keeps the marker for the next prompt.
    /// </summary>
    private async Task<VoidResult<string>> CompactAtTurnEndAsync(
        DysonAgentTurn turn,
        VoidResult<string> result,
        CancellationToken cancellationToken)
    {
        var paused = _pausedForCompactionTurnIds.TryRemove(turn.Id, out _);
        if (result.IsError)
            return result;

        if (turn.Kind == DysonAgentTurnKind.FullSummarize)
        {
            // The host apply step excludes every earlier turn: this summary is the compaction.
            ClearCompactionPending();
            return result;
        }

        if (!paused)
        {
            var limit = ResolveEffectiveMaxTargetContextTokens();
            var live = LiveContextTokens;
            var over = limit > 0 && live > limit;
            if (!DysonCompactionFlow.CanCompact(this, turn)
                || !(over || IsCompactionPending)
                || HasPendingTurnOfKind(DysonAgentTurnKind.FullSummarize)
                || IsSummaryInFlight)
            {
                return result;
            }

            AppendLog($"compaction: turn ended with context over the limit or pending (live={live} max={limit})");
            if (cancellationToken.IsCancellationRequested)
            {
                RecordCompactionPending(turn.Id);
                return result;
            }
        }

        return await DysonCompactionFlow
            .RunAsync(this, turn, resume: paused, RunCompactionSummaryTurnAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> RunTurnLoopAsync(
        DysonAgentTurn turn,
        IReadOnlyList<string> filePaths,
        bool preserveReasoning,
        CancellationToken cancellationToken)
    {
        AppendLog($"prompt: {Truncate(turn.Instruction ?? turn.Kind.ToString(), 120)}");
        var wasTerminal = IsTerminal;
        AddTurn(turn);
        using var inFlightPrompt = BeginInFlightPrompt(turn);
        using var contextLimitWatch = WatchContextLimit();
        if (wasTerminal && !IsTerminal)
            await PersistReopenAsync("new in-flight prompt").ConfigureAwait(false);

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(_workDirectoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
            return new VoidResult<string>($"Workspace filesystem: {fsResult.Error}");

        var executor = new DysonWorkspaceToolExecutor(this, fsResult.Value, _http, _store, _workDirectoryId, _plans);
        var inFlight = new List<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound>();
        var useResponses = string.Equals(
            OpenAiProvider.OpenAiApiMode,
            DysonOpenAiApiModes.Responses,
            StringComparison.Ordinal);
        var supportsResponsesChaining = useResponses
            && OpenAiCompatibleHttp.SupportsResponsesServerChaining(OpenAiProvider);
        string? previousResponseId = null;
        var childReportNudged = false;
        string? harnessFollowUp = null;
        const string incompleteToolReason =
            OpenAiCacheFriendlyTranscriptBuilder.IncompleteToolResultContent;
        const string childReportNudge =
            "Harness: plain text does not finish this subagent. Call SubmitSubagentReport now with your findings (or blocker).";
        const string childReportMissing =
            "Child PromptAsync ended without SubmitSubagentReport.";
        var maxRounds = ResolveMaxToolRounds();
        var stoppedForCompaction = false;

        // Safe point 1: count the built request; when compaction is requested it is not sent.
        bool StopBeforeSend(int requestTokens)
        {
            ReportLiveContextTokens(turn, requestTokens);
            stoppedForCompaction = IsCompactionRequested(turn);
            return stoppedForCompaction;
        }

        try
        {
            for (var round = 0; round < maxRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                previousResponseId = DeliverPendingUserComments(turn, previousResponseId);

                async Task<Result<OpenAiModelReply, string>> ConsumeCurrentProviderRoundAsync()
                {
                    // Mid-turn model pick: this request resolves wire mode, chaining and limit from the new provider.
                    if (ApplyPendingModelAtRequest(turn, inFlight))
                    {
                        previousResponseId = null;
                        useResponses = string.Equals(
                            OpenAiProvider.OpenAiApiMode,
                            DysonOpenAiApiModes.Responses,
                            StringComparison.Ordinal);
                        supportsResponsesChaining = useResponses
                            && OpenAiCompatibleHttp.SupportsResponsesServerChaining(OpenAiProvider);
                    }

                    if (useResponses)
                    {
                        // Direct OpenAI: store+delta via previous_response_id when known.
                        // Managed/CLIProxy: never chain — full local reasoning→call→output replay.
                        OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest built;
                        if (supportsResponsesChaining
                            && previousResponseId is not null
                            && inFlight.Count > 0
                            && harnessFollowUp is null)
                        {
                            await EnsureResponsesBinaryFileIdsAsync(
                                    inFlight[^1].Results,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesDelta(
                                this,
                                previousResponseId,
                                inFlight[^1].Results);
                            // Delta: last live count (prior response) plus the new outputs.
                            if (StopBeforeSend(LiveContextTokens
                                    + DysonOutgoingContextTokens.CountNode(built.Input, TokenCounter)))
                                return Result<OpenAiModelReply, string>.AsError(CompactionPauseReason);
                        }
                        else
                        {
                            await EnsureRemoteUrlsForVisionAsync(inFlight, cancellationToken)
                                .ConfigureAwait(false);
                            await EnsureResponsesVisionFileIdsAsync(inFlight, cancellationToken)
                                .ConfigureAwait(false);
                            built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                                this,
                                currentUserPrompt: harnessFollowUp,
                                currentFilePaths: null,
                                inFlightRounds: inFlight,
                                previousResponseId: supportsResponsesChaining ? previousResponseId : null,
                                preserveReasoning: preserveReasoning);
                            if (round == 0 && filePaths.Count > 0)
                                AppendPathsToLastUser(built.Input, filePaths);
                            if (StopBeforeSend(DysonOutgoingContextTokens.CountBuilt(built, TokenCounter)))
                                return Result<OpenAiModelReply, string>.AsError(CompactionPauseReason);
                        }

                        var streamed = await ConsumeStreamWithTransientRetryAsync(
                            () => _responses.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                            turn,
                            cancellationToken).ConfigureAwait(false);

                        // Direct only: one full-replay retry when store chaining lost the function_call.
                        if (streamed.IsError
                            && supportsResponsesChaining
                            && OpenAiCompatibleHttp.IsMissingToolCallForOutputError(streamed.Error))
                        {
                            AppendLog("Responses: missing tool call for output — retrying with full item replay");
                            previousResponseId = null;
                            await EnsureRemoteUrlsForVisionAsync(inFlight, cancellationToken)
                                .ConfigureAwait(false);
                            await EnsureResponsesVisionFileIdsAsync(inFlight, cancellationToken)
                                .ConfigureAwait(false);
                            var retryBuilt = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                                this,
                                currentUserPrompt: harnessFollowUp,
                                currentFilePaths: null,
                                inFlightRounds: inFlight,
                                previousResponseId: null,
                                preserveReasoning: preserveReasoning);
                            if (round == 0 && filePaths.Count > 0)
                                AppendPathsToLastUser(retryBuilt.Input, filePaths);
                            if (StopBeforeSend(DysonOutgoingContextTokens.CountBuilt(retryBuilt, TokenCounter)))
                                return Result<OpenAiModelReply, string>.AsError(CompactionPauseReason);

                            streamed = await ConsumeStreamWithTransientRetryAsync(
                                () => _responses.StreamCreateAsync(OpenAiProvider, retryBuilt, cancellationToken),
                                turn,
                                cancellationToken).ConfigureAwait(false);
                        }

                        return streamed;
                    }

                    await EnsureRemoteUrlsForVisionAsync(inFlight, cancellationToken)
                        .ConfigureAwait(false);
                    var completionsBuilt = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                        this,
                        currentUserPrompt: harnessFollowUp,
                        currentFilePaths: null,
                        inFlightRounds: inFlight);
                    if (round == 0 && filePaths.Count > 0)
                        AppendPathsToLastUser(completionsBuilt.Messages, filePaths);
                    if (StopBeforeSend(DysonOutgoingContextTokens.CountBuilt(completionsBuilt, TokenCounter)))
                        return Result<OpenAiModelReply, string>.AsError(CompactionPauseReason);

                    return await ConsumeStreamWithTransientRetryAsync(
                        () => _completions.StreamCreateAsync(OpenAiProvider, completionsBuilt, cancellationToken),
                        turn,
                        cancellationToken).ConfigureAwait(false);
                }

                var replyResult = await ConsumeCurrentProviderRoundAsync().ConfigureAwait(false);
                if (!stoppedForCompaction
                    && replyResult.IsError
                    && await TryApplyChatFallbackAsync(replyResult.Error, cancellationToken)
                        .ConfigureAwait(false))
                {
                    previousResponseId = null;
                    useResponses = string.Equals(
                        OpenAiProvider.OpenAiApiMode,
                        DysonOpenAiApiModes.Responses,
                        StringComparison.Ordinal);
                    supportsResponsesChaining = useResponses
                        && OpenAiCompatibleHttp.SupportsResponsesServerChaining(OpenAiProvider);
                    replyResult = await ConsumeCurrentProviderRoundAsync().ConfigureAwait(false);
                }

                if (stoppedForCompaction)
                {
                    AppendLog($"tool round {round + 1}: not sent, paused for context compaction");
                    return PauseForCompaction(turn);
                }

                if (replyResult.IsError)
                {
                    CommitReasoningRound(turn, reply: null, round, isFinalAssistant: true);
                    turn.ClearStreamingPreview();
                    turn.ClearReasoningPreview();
                    turn.FinalizeIncompleteTools(incompleteToolReason);
                    return new VoidResult<string>(replyResult.Error);
                }

                // This round already sent the child-report nudge as currentUserPrompt.
                // Clear so later rounds do not re-emit it. (User comments are replayed by the
                // transcript builder at their delivery anchor, not via this follow-up.)
                harnessFollowUp = null;

                var reply = replyResult.Value;
                await RecordUsageAsync(reply, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply.UsageCacheHint))
                    AppendLog(reply.UsageCacheHint);
                if (reply.PromptTokens is int promptTokens)
                    LastReportedPromptTokens = promptTokens;
                ReportResponseContextTokens(turn, reply);

                if (supportsResponsesChaining && !string.IsNullOrEmpty(reply.ResponseId))
                    previousResponseId = reply.ResponseId;

                if (reply.ToolCalls.Count > 0)
                {
                    // Thought now; InterimText only if this round continues (not EndsCurrentTurn).
                    CommitReasoningRound(turn, reply, round, isFinalAssistant: true);
                    turn.ClearStreamingPreview();
                    turn.ClearReasoningPreview();

                    foreach (var call in reply.ToolCalls)
                        turn.ToolCalls.Add(call);

                    var staged = await DysonToolCallScheduler.RunStagedAsync(
                        turn,
                        executor.ExecuteAsync,
                        cancellationToken).ConfigureAwait(false);

                    if (staged.IsError)
                    {
                        turn.ClearStreamingPreview();
                        turn.ClearReasoningPreview();
                        turn.FinalizeIncompleteTools(incompleteToolReason);
                        return staged;
                    }

                    var roundResults = new List<DysonToolCallResult>(reply.ToolCalls.Count);
                    foreach (var call in reply.ToolCalls)
                    {
                        var match = turn.ResponseLog.LastOrDefault(r =>
                            string.Equals(r.CallId, call.CallId, StringComparison.Ordinal));
                        if (match is not null)
                        {
                            roundResults.Add(match);
                            continue;
                        }

                        // Pad so in-flight Completions/Responses stay paired with tool_calls.
                        roundResults.Add(new DysonToolCallResult
                        {
                            CallId = call.CallId,
                            ToolName = call.ToolName,
                            Stage = call.Stage,
                            IsError = true,
                            Content = incompleteToolReason,
                        });
                    }

                    inFlight.Add(new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound(
                        reply.ToolCalls.ToList(),
                        roundResults,
                        reply.ReasoningOutputItems.Count > 0 ? reply.ReasoningOutputItems : null));
                    turn.ToolRoundCallCounts.Add(reply.ToolCalls.Count);
                    turn.RecordReasoningItems(reply.ReasoningOutputItems, OpenAiProvider.ReasoningSourceTag, finalRound: false);

                    AppendLog($"tool round {round + 1}: {reply.ToolCalls.Count} call(s)");

                    var ending = roundResults.FirstOrDefault(r => r.EndsCurrentTurn && !r.IsError);
                    if (ending is not null)
                    {
                        AppendLog($"tool round {round + 1}: ends current turn ({ending.ToolName})");
                        return SoftCloseAfterEndsCurrentTurn(turn, ending.ToolName, reply.Content);
                    }

                    if (Parent is not null
                        && await TryAutoSubmitAfterRepeatedFailuresAsync(turn).ConfigureAwait(false))
                    {
                        AppendLog($"tool round {round + 1}: auto-submit after 2 failed SubmitSubagentReport");
                        var closed = SoftCloseAfterEndsCurrentTurn(turn, "SubmitSubagentReport", reply.Content);
                        CancelBackgroundRun();
                        return closed;
                    }

                    // Continuing tool loop — interim assistant words (not final reply).
                    CommitInterimText(turn, reply.Content, round);

                    // Safe point 2: this round's tools ran and are in inFlight; no next hop.
                    if (IsCompactionRequested(turn))
                    {
                        AppendLog($"tool round {round + 1}: paused for context compaction");
                        return PauseForCompaction(turn);
                    }

                    continue;
                }

                var text = ResolveFinalAssistantContent(reply.Content);

                if (Parent is not null && !TurnHasSubmitSubagentReport(turn))
                {
                    if (!childReportNudged)
                    {
                        // Keep AssistantText unset so history stays incomplete and tools remain inFlight-only.
                        CommitReasoningRound(turn, reply, round, isFinalAssistant: true);
                        turn.ClearStreamingPreview();
                        turn.ClearReasoningPreview();
                        previousResponseId = null;
                        harnessFollowUp =
                            $"Your previous assistant reply was not accepted as a finish:\n\n{text}\n\n{childReportNudge}";
                        childReportNudged = true;
                        AppendLog("child report gate: nudged for SubmitSubagentReport");
                        continue;
                    }

                    CommitReasoningRound(turn, reply, round, isFinalAssistant: true);
                    turn.ClearStreamingPreview();
                    turn.ClearReasoningPreview();
                    turn.FinalizeIncompleteTools(incompleteToolReason);
                    AppendLog("child report gate: missing SubmitSubagentReport after nudge");
                    return new VoidResult<string>(childReportMissing);
                }

                // Comment arrived during this stream — deliver it next round instead of finalizing.
                // Atomic with the pending check: a comment after this point is rejected and
                // PromptOrInjectAsync queues it as a new prompt turn (never silently dropped).
                if (!turn.TryCloseCommentIntake())
                {
                    CommitReasoningRound(turn, reply, round, isFinalAssistant: true);
                    turn.ClearStreamingPreview();
                    turn.ClearReasoningPreview();
                    continue;
                }

                // Title parse only at finalize — preview stays raw (incl. mid-stream H1) until then.
                CommitReasoningRound(turn, reply, round, isFinalAssistant: true);
                turn.RecordReasoningItems(reply.ReasoningOutputItems, OpenAiProvider.ReasoningSourceTag, finalRound: true);
                ApplyAssistantText(turn, text);
                turn.FinishStreaming();
                turn.FinishReasoningStreaming();
                AppendLog($"turn complete: {turn.AgentTitle ?? turn.Id.ToString("N")[..8]}");
                return VoidResult<string>.Success;
            }

            AppendLog($"tool loop soft-pause after {maxRounds} round(s)");
            if (string.Equals(Mode, DysonAgentModes.Explore, StringComparison.OrdinalIgnoreCase))
                return await ExploreBudgetRecapAsync(
                    turn,
                    inFlight,
                    useResponses,
                    incompleteToolReason,
                    cancellationToken).ConfigureAwait(false);

            return SoftPauseAfterToolLoopExhaustion(this, turn, maxRounds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CommitReasoningRound(turn, reply: null, roundIndex: inFlight.Count, isFinalAssistant: true);
            turn.ClearStreamingPreview();
            turn.ClearReasoningPreview();
            turn.FinalizeIncompleteTools(incompleteToolReason);
            return new VoidResult<string>("Prompt was cancelled.");
        }
        finally
        {
            // Every other exit (end-turn tool, soft-pause, error, cancel) closes intake before the
            // in-flight scope pops, so a late comment takes the queued-prompt fallback.
            // The live count stays until the caller's end-of-turn compaction check.
            turn.CloseCommentIntake();
        }
    }

    private const string CompactionPauseReason = "Paused for context compaction.";

    /// <summary>Per-response live count: provider input + output when usage is present, else request count + reply estimate.</summary>
    private void ReportResponseContextTokens(DysonAgentTurn turn, OpenAiModelReply reply)
    {
        if (reply.Usage is { InputTokens: > 0 } usage)
        {
            ReportLiveContextTokens(turn, usage.InputTokens + usage.WriteTokens);
            return;
        }

        var estimate = TokenCounter.CountTokens(reply.Content ?? "");
        foreach (var call in reply.ToolCalls)
            estimate += TokenCounter.CountTokens(call.ToolName) + TokenCounter.CountTokens(call.ArgumentsJson ?? "");
        ReportLiveContextTokens(turn, LiveContextTokens + estimate);
    }

    /// <summary>
    /// Finalizes <paramref name="turn"/> (tools and reasoning kept) with <see cref="DysonCompactionFlow.PausedNote"/>.
    /// <see cref="CompactAtTurnEndAsync"/> then summarizes and queues the resuming Continuation.
    /// </summary>
    private VoidResult<string> PauseForCompaction(DysonAgentTurn turn)
    {
        turn.ClearStreamingPreview();
        turn.ClearReasoningPreview();
        turn.FinalizeIncompleteTools(OpenAiCacheFriendlyTranscriptBuilder.IncompleteToolResultContent);
        ApplyAssistantText(turn, DysonCompactionFlow.PausedNote);
        turn.FinishStreaming();
        turn.FinishReasoningStreaming();
        _pausedForCompactionTurnIds[turn.Id] = 0;
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Inline FullSummarize turn for compaction: one full rebuild, no tool calls executed, no compaction re-entry.
    /// Tools stay on the wire so the request keeps the turn's prompt-cache prefix.
    /// A context-length rejection (or an empty reply) falls back to <see cref="DysonTurnSummarizer"/>
    /// over trimmed recent history.
    /// </summary>
    private async Task<VoidResult<string>> RunCompactionSummaryTurnAsync(
        DysonAgentTurn summary,
        CancellationToken cancellationToken)
    {
        AddTurn(summary);
        using var inFlightPrompt = BeginInFlightPrompt(summary);

        async Task<Result<OpenAiModelReply, string>> ConsumeSummaryAsync()
        {
            ApplyPendingModelAtRequest(summary, inFlight: null);
            if (string.Equals(OpenAiProvider.OpenAiApiMode, DysonOpenAiApiModes.Responses, StringComparison.Ordinal))
            {
                await EnsureRemoteUrlsForVisionAsync([], cancellationToken).ConfigureAwait(false);
                await EnsureResponsesVisionFileIdsAsync([], cancellationToken).ConfigureAwait(false);
                var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                    this,
                    currentUserPrompt: null,
                    currentFilePaths: null,
                    inFlightRounds: null,
                    previousResponseId: null,
                    preserveReasoning: PreserveReasoningTraces);
                ReportLiveContextTokens(summary, DysonOutgoingContextTokens.CountBuilt(built, TokenCounter));
                return await ConsumeStreamWithTransientRetryAsync(
                    () => _responses.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                    summary,
                    cancellationToken).ConfigureAwait(false);
            }

            await EnsureRemoteUrlsForVisionAsync([], cancellationToken).ConfigureAwait(false);
            var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                this,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: null);
            ReportLiveContextTokens(summary, DysonOutgoingContextTokens.CountBuilt(completions, TokenCounter));
            return await ConsumeStreamWithTransientRetryAsync(
                () => _completions.StreamCreateAsync(OpenAiProvider, completions, cancellationToken),
                summary,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var replyResult = await ConsumeSummaryAsync().ConfigureAwait(false);
            if (replyResult.IsError
                && !OpenAiCompatibleHttp.IsContextLengthError(replyResult.Error)
                && await TryApplyChatFallbackAsync(replyResult.Error, cancellationToken).ConfigureAwait(false))
            {
                replyResult = await ConsumeSummaryAsync().ConfigureAwait(false);
            }

            string? text = null;
            if (replyResult.IsSuccess)
            {
                var reply = replyResult.Value;
                await RecordUsageAsync(reply, cancellationToken).ConfigureAwait(false);
                if (reply.PromptTokens is int promptTokens)
                    LastReportedPromptTokens = promptTokens;
                CommitReasoningRound(summary, reply, roundIndex: 0, isFinalAssistant: true);
                text = reply.Content;
            }
            else if (!OpenAiCompatibleHttp.IsContextLengthError(replyResult.Error))
            {
                CommitReasoningRound(summary, reply: null, roundIndex: 0, isFinalAssistant: true);
                summary.ClearStreamingPreview();
                summary.ClearReasoningPreview();
                return new VoidResult<string>(replyResult.Error);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog(replyResult.IsError
                    ? "compaction: summary request over the model window; trimmed summarizer fallback"
                    : "compaction: summary reply was empty; trimmed summarizer fallback");
                var fallback = await SummarizeTrimmedHistoryAsync(summary, cancellationToken).ConfigureAwait(false);
                if (fallback.IsError)
                {
                    summary.ClearStreamingPreview();
                    summary.ClearReasoningPreview();
                    return new VoidResult<string>(fallback.Error);
                }

                text = "# Session summary: context compaction\n\n" + fallback.Value;
            }

            summary.ClearStreamingPreview();
            summary.ClearReasoningPreview();
            ApplyAssistantText(summary, text);
            summary.FinishStreaming();
            summary.FinishReasoningStreaming();
            AppendLog($"compaction: summary complete ({summary.AssistantText?.Length ?? 0} chars)");
            return VoidResult<string>.Success;
        }
        finally
        {
            summary.CloseCommentIntake();
        }
    }

    private Task<Result<string, string>> SummarizeTrimmedHistoryAsync(DysonAgentTurn summary, CancellationToken cancellationToken)
    {
        var history = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = DysonCompactionFlow.FormatRecentHistory(
                this,
                summary.Id,
                DysonTurnSummarizerPrompt.MaxInputChars - 200),
            StartedUtc = DateTime.UtcNow,
        };
        return DysonTurnSummarizer.SummarizeForCompactionAsync(
            OpenAiProvider,
            _http,
            history,
            DysonCompactionFlow.FallbackSummaryReason,
            TokenCounter,
            cancellationToken,
            (usage, token) => AppendUsageAsync(OpenAiProvider, usage, token));
    }

    /// <summary>
    /// Explore-only post-budget path: one Completions/Responses call with tools cleared so the
    /// model cannot burn more rounds; applies recap text or a harness incomplete-findings fallback.
    /// Does not enqueue <see cref="DysonAgentTurnKind.RethinkToolUsage"/>.
    /// Omits <c>previous_response_id</c> (fresh full rebuild after budget exhaustion).
    /// </summary>
    private async Task<VoidResult<string>> ExploreBudgetRecapAsync(
        DysonAgentTurn turn,
        List<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound> inFlight,
        bool useResponses,
        string incompleteToolReason,
        CancellationToken cancellationToken)
    {
        turn.ClearStreamingPreview();
        turn.ClearReasoningPreview();
        turn.FinalizeIncompleteTools(incompleteToolReason);

        async Task<Result<OpenAiModelReply, string>> ConsumeRecapAsync(bool recapUseResponses)
        {
            if (ApplyPendingModelAtRequest(turn, inFlight))
            {
                recapUseResponses = string.Equals(
                    OpenAiProvider.OpenAiApiMode,
                    DysonOpenAiApiModes.Responses,
                    StringComparison.Ordinal);
            }

            if (recapUseResponses)
            {
                await EnsureRemoteUrlsForVisionAsync(inFlight, cancellationToken)
                    .ConfigureAwait(false);
                await EnsureResponsesVisionFileIdsAsync(inFlight, cancellationToken)
                    .ConfigureAwait(false);
                var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                    this,
                    currentUserPrompt: DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction,
                    currentFilePaths: null,
                    inFlightRounds: inFlight,
                    previousResponseId: null,
                    preserveReasoning: PreserveReasoningTraces);
                built.Tools.Clear();
                return await ConsumeStreamWithTransientRetryAsync(
                    () => _responses.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                    turn,
                    cancellationToken).ConfigureAwait(false);
            }

            await EnsureRemoteUrlsForVisionAsync(inFlight, cancellationToken)
                .ConfigureAwait(false);
            var completionsBuilt = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                this,
                currentUserPrompt: DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction,
                currentFilePaths: null,
                inFlightRounds: inFlight);
            completionsBuilt.Tools.Clear();
            return await ConsumeStreamWithTransientRetryAsync(
                () => _completions.StreamCreateAsync(OpenAiProvider, completionsBuilt, cancellationToken),
                turn,
                cancellationToken).ConfigureAwait(false);
        }

        var replyResult = await ConsumeRecapAsync(useResponses).ConfigureAwait(false);
        if (replyResult.IsError
            && await TryApplyChatFallbackAsync(replyResult.Error, cancellationToken)
                .ConfigureAwait(false))
        {
            var recapUseResponses = string.Equals(
                OpenAiProvider.OpenAiApiMode,
                DysonOpenAiApiModes.Responses,
                StringComparison.Ordinal);
            replyResult = await ConsumeRecapAsync(recapUseResponses).ConfigureAwait(false);
        }

        if (replyResult.IsError)
        {
            CommitReasoningRound(turn, reply: null, roundIndex: 0, isFinalAssistant: true);
            turn.ClearStreamingPreview();
            turn.ClearReasoningPreview();
            ApplyAssistantText(turn, DysonRethinkToolUsageFlow.ExploreBudgetExhaustedFallback);
            turn.FinishStreaming();
            turn.FinishReasoningStreaming();
            AppendLog("explore budget recap: provider error; applied harness fallback");
            return VoidResult<string>.Success;
        }

        var reply = replyResult.Value;
        await RecordUsageAsync(reply, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(reply.UsageCacheHint))
            AppendLog(reply.UsageCacheHint);
        if (reply.PromptTokens is int promptTokens)
            LastReportedPromptTokens = promptTokens;

        // Ignore tool_calls on this no-tools recap — budget is already exhausted.
        var text = string.IsNullOrWhiteSpace(reply.Content)
            ? DysonRethinkToolUsageFlow.ExploreBudgetExhaustedFallback
            : reply.Content;

        CommitReasoningRound(turn, reply, roundIndex: 0, isFinalAssistant: true);
        ApplyAssistantText(turn, text);
        turn.FinishStreaming();
        turn.FinishReasoningStreaming();
        AppendLog($"explore budget recap complete: {turn.AgentTitle ?? turn.Id.ToString("N")[..8]}");
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Provider-request boundary for a mid-turn <see cref="DysonAgentSession.SetModel"/> pick. True when the
    /// provider changed. A new reasoning source drops this turn's stored reasoning blobs (in-flight rounds and
    /// <see cref="DysonAgentTurn.ToolRoundReasoningItems"/>) so the new provider never gets foreign blobs.
    /// </summary>
    private bool ApplyPendingModelAtRequest(
        DysonAgentTurn turn,
        List<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound>? inFlight)
    {
        var previousSource = OpenAiProvider.ReasoningSourceTag;
        if (!ApplyPendingModel())
            return false;

        if (!string.Equals(previousSource, OpenAiProvider.ReasoningSourceTag, StringComparison.Ordinal))
        {
            for (var i = 0; i < (inFlight?.Count ?? 0); i++)
                inFlight![i] = inFlight[i] with { ReasoningItems = null };
            for (var i = 0; i < turn.ToolRoundReasoningItems.Count; i++)
                turn.ToolRoundReasoningItems[i] = null;
        }

        return true;
    }

    private Task RecordUsageAsync(OpenAiModelReply reply, CancellationToken cancellationToken) =>
        AppendUsageAsync(OpenAiProvider, reply.Usage, cancellationToken);

    /// <summary>
    /// Appends one <c>usage_requests</c> row when <paramref name="usage"/> is non-null.
    /// A missing usage object writes nothing. A real usage object of zeros still appends.
    /// Model identity is <paramref name="provider"/> (the model that produced the usage).
    /// </summary>
    public async Task AppendUsageAsync(
        OpenAiCompatibleAgentProvider provider,
        DysonParsedUsage? usage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_usageAnalytics is null || usage is null)
            return;

        var alias = string.IsNullOrWhiteSpace(provider.DisplayAlias)
            ? provider.Slug
            : provider.DisplayAlias;
        var row = new DysonUsageRequestEntity
        {
            Id = Guid.NewGuid(),
            WorkDirectoryName = _workDirectoryName,
            SessionId = PersistenceId,
            RootSessionId = ResolveRootPersistenceId(),
            ModelSlug = provider.Slug,
            ModelDisplayAlias = alias,
            ReasoningEffort = provider.ReasoningEffort ?? "",
            OccurredUtc = DateTime.UtcNow,
            InputTokens = usage.InputTokens,
            CacheTokens = usage.CacheTokens,
            WriteTokens = usage.WriteTokens,
            CacheWriteTokens = usage.CacheWriteTokens,
            InputTokensAfterCache = usage.InputTokensAfterCache,
            WriteTokensAfterCache = usage.WriteTokensAfterCache,
        };

        try
        {
            var result = await _usageAnalytics.AppendAsync(row, cancellationToken).ConfigureAwait(false);
            if (result.IsError)
                AppendLog($"usage analytics: {result.Error}");
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                AppendLog($"usage analytics: {ex.Message}");
        }
    }

    private Guid ResolveRootPersistenceId()
    {
        DysonAgentSession current = this;
        while (current.Parent is not null)
            current = current.Parent;
        return current.PersistenceId == Guid.Empty ? PersistenceId : current.PersistenceId;
    }

    private static bool TurnHasSubmitSubagentReport(DysonAgentTurn turn) =>
        turn.ResponseLog.Any(r =>
            string.Equals(r.ToolName, "SubmitSubagentReport", StringComparison.Ordinal));

    public override async Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
        CancellationToken cancellationToken = default)
    {
        var interrupt = await WaitForInterruptAsync(cancellationToken).ConfigureAwait(false);
        if (interrupt.IsError)
            return Result<DysonAgentSessionEvent, string>.AsError(interrupt.Error);

        return Result<DysonAgentSessionEvent, string>.AsValue(
            new DysonSubagentInterruptEvent
            {
                Interrupt = interrupt.Value,
            });
    }

    /// <summary>
    /// Upload in-context UserImages and every image BinaryAttachment (in-flight + completed
    /// history) when <see cref="DysonAgentSessionConfig.FileStorage"/> is set. No-op when
    /// the in-flight wire is already captured or RemoteUrl is still valid. Failures are
    /// logged; data-URL remains a last-resort for images that still have local bytes.
    /// A captured wire is not rewritten with a new presign.
    /// </summary>
    private async Task EnsureRemoteUrlsForVisionAsync(
        IReadOnlyList<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound> inFlight,
        CancellationToken cancellationToken)
    {
        var storage = Config.FileStorage;
        if (storage is null)
            return;

        foreach (var historyTurn in Turns)
        {
            if (historyTurn.IsExcludedFromContext)
                continue;

            foreach (var image in historyTurn.UserImages)
                await EnsureOneRemoteUrlAsync(storage, image, cancellationToken).ConfigureAwait(false);

            foreach (var result in historyTurn.ResponseLog)
            {
                if (result.BinaryAttachment is { IsImage: true } attachment)
                    await EnsureOneRemoteUrlAsync(storage, attachment, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var round in inFlight)
        {
            foreach (var result in round.Results)
            {
                if (result.BinaryAttachment is { IsImage: true } attachment)
                    await EnsureOneRemoteUrlAsync(storage, attachment, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureOneRemoteUrlAsync(
        DysonS3FileStorage storage,
        DysonBinaryAttachment image,
        CancellationToken cancellationToken)
    {
        var ensured = await storage.EnsureRemoteUrlAsync(image, cancellationToken).ConfigureAwait(false);
        if (ensured.IsError)
            AppendLog($"File storage: {ensured.Error}");
    }

    /// <summary>
    /// Responses vision: upload in-context <see cref="DysonAgentTurn.UserImages"/> plus one-shot
    /// tool BinaryAttachment on the last in-flight round (data-URL fallback on failure).
    /// Images that already have <see cref="DysonBinaryAttachment.RemoteUrl"/> skip Files API.
    /// Non-image Files uploads are unchanged.
    /// </summary>
    private async Task EnsureResponsesVisionFileIdsAsync(
        IReadOnlyList<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound> inFlight,
        CancellationToken cancellationToken)
    {
        var images = new List<DysonBinaryAttachment>();
        foreach (var turn in Turns)
        {
            if (turn.IsExcludedFromContext)
                continue;
            foreach (var image in turn.UserImages)
            {
                if (SkipResponsesFilesUpload(image))
                    continue;
                images.Add(image);
            }
        }

        if (images.Count > 0)
        {
            await OpenAiFilesClient.EnsureBinaryFileIdsAsync(
                    _http,
                    OpenAiProvider,
                    images,
                    note => AppendLog(note),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (inFlight.Count == 0)
            return;

        await EnsureResponsesBinaryFileIdsAsync(inFlight[^1].Results, cancellationToken)
            .ConfigureAwait(false);
    }

    private Task EnsureResponsesBinaryFileIdsAsync(
        IReadOnlyList<DysonToolCallResult> results,
        CancellationToken cancellationToken)
    {
        var needingUpload = new List<DysonBinaryAttachment>();
        foreach (var result in results)
        {
            if (result.IsError || result.BinaryAttachment is null)
                continue;
            if (SkipResponsesFilesUpload(result.BinaryAttachment))
                continue;
            needingUpload.Add(result.BinaryAttachment);
        }

        return OpenAiFilesClient.EnsureBinaryFileIdsAsync(
            _http,
            OpenAiProvider,
            needingUpload,
            note => AppendLog(note),
            cancellationToken);
    }

    /// <summary>Images with a RemoteUrl go on the wire as HTTPS; skip Files API for those.</summary>
    private static bool SkipResponsesFilesUpload(DysonBinaryAttachment attachment) =>
        attachment.IsImage && !string.IsNullOrWhiteSpace(attachment.RemoteUrl);

    /// <summary>
    /// Round-0 local paths. History already includes the in-flight turn
    /// (<c>excludeLastIfCurrent</c> is false), and that text already contains
    /// <see cref="DysonAgentTurn.HiddenInstruction"/>. Skip when the same block
    /// is present so the model does not see it twice. A multimodal user message
    /// is the current turn: edit its text part or stop. Do not paint an older turn.
    /// </summary>
    internal static void AppendPathsToLastUser(
        System.Text.Json.Nodes.JsonArray messagesOrInput,
        IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
            return;

        var block = FormatAttachedPathsBlock(filePaths);
        for (var i = messagesOrInput.Count - 1; i >= 0; i--)
        {
            if (messagesOrInput[i] is not System.Text.Json.Nodes.JsonObject msg)
                continue;
            if (msg["role"]?.GetValue<string>() != "user")
                continue;

            if (msg["content"] is System.Text.Json.Nodes.JsonValue contentVal
                && contentVal.TryGetValue<string>(out var text))
            {
                if (text.Contains(block, StringComparison.Ordinal))
                    return;

                var sb = new System.Text.StringBuilder(text);
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(block);
                msg["content"] = sb.ToString().TrimEnd();
                return;
            }

            if (msg["content"] is not System.Text.Json.Nodes.JsonArray parts)
                continue;

            foreach (var part in parts)
            {
                if (part is not System.Text.Json.Nodes.JsonObject obj)
                    continue;
                var type = obj["type"]?.GetValue<string>();
                if (type is not ("text" or "input_text"))
                    continue;

                var partText = obj["text"]?.GetValue<string>() ?? "";
                if (partText.Contains(block, StringComparison.Ordinal))
                    return;

                var sb = new System.Text.StringBuilder(partText);
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(block);
                obj["text"] = sb.ToString().TrimEnd();
                return;
            }

            return;
        }
    }

    private static string FormatAttachedPathsBlock(IReadOnlyList<string> filePaths)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Attached paths:");
        foreach (var path in filePaths)
            sb.AppendLine($"- {path}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Backoff between transient stream retries (ms). 10 delays ⇒ 11 attempts total.
    /// Tests may replace with zeros; restore defaults afterward.
    /// </summary>
    internal static int[] TransientRetryBackoffMs { get; set; } =
        [2000, 5000, 10000, 10000, 10000, 10000, 10000, 10000, 10000, 10000];

    /// <summary>Max 429 retries (independent of <see cref="TransientRetryBackoffMs"/>).</summary>
    internal const int Transient429MaxRetries = 2;

    /// <summary>
    /// Delay between 429 retries (ms). Tests may replace with zero; restore default afterward.
    /// </summary>
    internal static int Transient429RetryDelayMs { get; set; } = 10_000;

    /// <summary>
    /// Consumes one inference stream round, retrying transient OpenAI errors except 401/403/cancel
    /// and 4xx other than 429. Session log records <c>lastError</c> on failure and on first retry
    /// for still-retryable errors (compact countdown lines remain for 5xx/429).
    /// 429 is capped at <see cref="Transient429MaxRetries"/> retries of <see cref="Transient429RetryDelayMs"/>;
    /// other transients use <see cref="TransientRetryBackoffMs"/>. Clears streaming/reasoning previews before each retry.
    /// </summary>
    private async Task<Result<OpenAiModelReply, string>> ConsumeStreamWithTransientRetryAsync(
        Func<IAsyncEnumerable<Result<OpenAiStreamChunk, string>>> streamFactory,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(turn);

        var delays = TransientRetryBackoffMs;
        var generalRetries = 0;
        var rateLimitRetries = 0;
        string? lastError = null;

        void AppendLastErrorIfAny()
        {
            if (!string.IsNullOrEmpty(lastError))
                AppendLog(lastError);
        }

        while (true)
        {
            var result = await ConsumeStreamAsync(streamFactory(), turn, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsError)
                return result;

            lastError = result.Error;
            if (!OpenAiCompatibleHttp.IsTransientServerError(lastError))
            {
                AppendLastErrorIfAny();
                return result;
            }

            int delayMs, retryN, retryDenom;
            if (OpenAiCompatibleHttp.IsRateLimitError(lastError))
            {
                if (rateLimitRetries >= Transient429MaxRetries)
                {
                    AppendLastErrorIfAny();
                    return result;
                }
                delayMs = Transient429RetryDelayMs;
                rateLimitRetries++;
                retryN = rateLimitRetries;
                retryDenom = Transient429MaxRetries;
            }
            else
            {
                if (generalRetries >= delays.Length)
                {
                    AppendLastErrorIfAny();
                    return result;
                }
                delayMs = delays[generalRetries];
                generalRetries++;
                retryN = generalRetries;
                retryDenom = delays.Length;
            }

            AppendLog(FormatTransientRetryLog(lastError, retryN, retryDenom, delayMs));
            if (retryN == 1)
                AppendLastErrorIfAny();
            turn.ClearStreamingPreview();
            turn.ClearReasoningPreview();
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryApplyChatFallbackAsync(
        string? error,
        CancellationToken cancellationToken)
    {
        if (_fallbackAppliedThisTurn
            || cancellationToken.IsCancellationRequested
            || LooksLikeUserOrStreamCancel(error)
            || Config.FallbackChatProvider is not OpenAiCompatibleAgentProvider fallback
            || fallback.SlugId == OpenAiProvider.SlugId)
        {
            return false;
        }

        var previousSlug = OpenAiProvider.Slug;
        Provider = fallback;
        SlugDefaultMaxTargetContextTokens = fallback.DefaultMaxTargetContextTokens;
        _fallbackAppliedThisTurn = true;

        if (PersistenceId != Guid.Empty && _store is not null)
        {
            var persist = await _store.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = PersistenceId,
                    ModelSlugId = fallback.SlugId,
                    ClearModelSlug = fallback.SlugId is null,
                    UpdateReasoningEffort = true,
                    ReasoningEffort = fallback.ReasoningEffort,
                },
                cancellationToken).ConfigureAwait(false);
            if (persist.IsError)
                AppendLog($"fallback: persist failed: {persist.Error}");
        }

        AppendLog(
            $"fallback: switched session model {previousSlug} → {fallback.Slug} after provider error");
        return true;
    }

    private static bool LooksLikeUserOrStreamCancel(string? error) =>
        !string.IsNullOrEmpty(error)
        && error.Contains("cancelled", StringComparison.OrdinalIgnoreCase);

    private static string FormatTransientRetryLog(
        string? error,
        int retryIndex,
        int retryCount,
        int delayMs)
    {
        var code = "error";
        const string prefix = "OpenAI API ";
        if (!string.IsNullOrEmpty(error) && error.StartsWith(prefix, StringComparison.Ordinal))
        {
            var rest = error.AsSpan(prefix.Length);
            if (rest.Length >= 3
                && char.IsDigit(rest[0])
                && char.IsDigit(rest[1])
                && char.IsDigit(rest[2]))
            {
                code = rest[..3].ToString();
            }
        }

        return $"OpenAI transient {code} — retry {retryIndex}/{retryCount} after {delayMs / 1000}s";
    }

    private async Task<Result<OpenAiModelReply, string>> ConsumeStreamAsync(
        IAsyncEnumerable<Result<OpenAiStreamChunk, string>> stream,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        OpenAiModelReply? completed = null;
        var started = System.Diagnostics.Stopwatch.StartNew();
        long charsReceived = 0;

        // Metadata only (model, size, elapsed), never the arguments themselves.
        void LogIncomplete(string reason) =>
            AppendLog(
                $"stream incomplete: {reason}; model={OpenAiProvider.Slug}; {charsReceived} chars received in {started.Elapsed.TotalSeconds:0.#}s");

        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (item.IsError)
                {
                    if (item.Error.StartsWith(OpenAiCompatibleHttp.StreamEndedErrorPrefix, StringComparison.Ordinal))
                        LogIncomplete(OpenAiCompatibleHttp.DescribeIncompleteStream(null)!);
                    return Result<OpenAiModelReply, string>.AsError(item.Error);
                }

                var chunk = item.Value;
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                {
                    charsReceived += chunk.TextDelta.Length;
                    turn.AppendStreamingDelta(chunk.TextDelta);
                }

                if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                {
                    charsReceived += chunk.ReasoningDelta.Length;
                    turn.AppendReasoningDelta(chunk.ReasoningDelta);
                }

                if (chunk.ToolCallDeltas is { } toolDeltas)
                {
                    foreach (var toolDelta in toolDeltas)
                        charsReceived += toolDelta.ArgumentsDelta?.Length ?? 0;
                }

                if (chunk.IsRoundComplete)
                    completed = chunk.CompletedReply;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<OpenAiModelReply, string>.AsError("OpenAI stream was cancelled.");
        }

        if (completed is null)
            return Result<OpenAiModelReply, string>.AsError(OpenAiCompatibleHttp.StreamEndedErrorPrefix);

        if (completed.IncompleteReason is { } incompleteReason)
            LogIncomplete(incompleteReason);

        return Result<OpenAiModelReply, string>.AsValue(completed);
    }

    private static void ApplyAssistantText(DysonAgentTurn turn, string text)
    {
        var prior = turn.AssistantText;
        var parsed = DysonAgentTurn.TryParseAgentTitle(text);
        var body = parsed.IsSuccess ? parsed.Value.Body : text;
        if (parsed.IsSuccess)
            turn.AgentTitle = parsed.Value.Title;

        turn.AssistantText = DysonWorkspaceToolExecutor.MergeAssistantTextPreservingDropResume(prior, body);
    }

    /// <summary>
    /// Round start: drains pending user comments and stamps them with the turn's tool-call count
    /// (<see cref="DysonAgentTurn.TryDequeueUserComments"/>); the transcript builder replays them
    /// at that anchor in this and every later request. Returns null when any were delivered so
    /// the Responses delta path (previous_response_id + new outputs only) is skipped and the full
    /// rebuild carries the comment; otherwise returns <paramref name="previousResponseId"/>.
    /// </summary>
    internal static string? DeliverPendingUserComments(DysonAgentTurn turn, string? previousResponseId) =>
        turn.TryDequeueUserComments().Length > 0 ? null : previousResponseId;

    /// <summary>
    /// Final no-tool-call round body: drop pure compact-history echoes; otherwise empty → harness note.
    /// </summary>
    internal static string ResolveFinalAssistantContent(string? replyContent)
    {
        if (DysonContextOptimizer.IsOnlyCompactToolHistoryEcho(replyContent))
            return "";

        return string.IsNullOrWhiteSpace(replyContent)
            ? "# Empty reply\n\nThe model returned no content."
            : replyContent;
    }

    /// <summary>
    /// Commits Thought (and optional InterimText) for a tool-loop round from reply / streaming preview.
    /// When <paramref name="isFinalAssistant"/> is true, skips InterimText (final body is AssistantText).
    /// </summary>
    internal static void CommitReasoningRound(
        DysonAgentTurn turn,
        OpenAiModelReply? reply,
        int roundIndex,
        bool isFinalAssistant)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var thought = reply?.ReasoningContent;
        if (string.IsNullOrWhiteSpace(thought))
            thought = turn.ReasoningStreamingPreview;

        var interim = isFinalAssistant ? null : reply?.Content;
        turn.AppendReasoningRound(
            roundIndex,
            thoughtText: thought,
            interimText: interim,
            includeInterimText: !isFinalAssistant);
    }

    /// <summary>Appends InterimText only (after tools when the round continues).</summary>
    internal static void CommitInterimText(DysonAgentTurn turn, string? content, int roundIndex)
    {
        ArgumentNullException.ThrowIfNull(turn);
        turn.AppendReasoningRound(
            roundIndex,
            thoughtText: null,
            interimText: content,
            includeInterimText: true);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max] + "…";
    }
}
