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
    /// (never an exhaustion error string). Does not enqueue rethink when already on rethink, or when
    /// session mode is Explore (Explore uses a no-tools recap instead).
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
        else
        {
            text =
                $"# Tool rounds paused\n\nTool loop reached the {maxRounds}-round budget without a final reply. A rethink turn will analyze whether to call ResumeCurrentTask or stop.";
        }

        ApplyAssistantText(turn, text);
        turn.FinishStreaming();
        turn.FinishReasoningStreaming();

        if (!isRethink && !isExplore)
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
            _ =>
                "# Turn ended\n\n" +
                "An end-turn tool was called; this turn ends.",
        };

    private readonly IDysonSessionRepository? _store;
    private readonly HttpClient _http;
    private readonly string _workDirectoryPath;
    private Guid _workDirectoryId;
    private readonly OpenAiCompletionsClient _completions;
    private readonly OpenAiResponsesClient _responses;
    private readonly IDysonModelRepository? _models;
    private readonly IDysonUsageAnalyticsRepository? _usageAnalytics;
    private readonly string _workDirectoryName;

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
        string workDirectoryName = "")
        : base(agentMode, config, provider, systemPromptSuffix)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workDirectoryPath = Path.GetFullPath(workDirectoryAbsolutePath);
        _store = store;
        SessionStore = store;
        _workDirectoryId = workDirectoryId;
        _models = models;
        _usageAnalytics = usageAnalytics;
        _workDirectoryName = workDirectoryName ?? "";
        _completions = new OpenAiCompletionsClient(_http);
        _responses = new OpenAiResponsesClient(_http);
    }

    public OpenAiCompatibleAgentProvider OpenAiProvider => (OpenAiCompatibleAgentProvider)Provider;

    public string WorkDirectoryPath => _workDirectoryPath;

    public Guid WorkDirectoryId => _workDirectoryId;

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
        var suffix = await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                models, providerKind, workDirectoryAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        var session = new OpenAiCompatibleAgentSession(
            agentMode, config, provider, http, workDirectoryAbsolutePath, store, workDirectoryId, models,
            suffix, usageAnalytics, workDirectoryName);
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

        var providerKind = DysonProviderKinds.EffectiveKind(
            provider.ProviderKind, provider.BaseUrl, provider.ApiKey);
        var suffix = await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                models, providerKind, workDirectoryAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
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
            workDirectoryName);
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

        var providerKind = DysonProviderKinds.EffectiveKind(
            childProvider.ProviderKind, childProvider.BaseUrl, childProvider.ApiKey);
        var suffix = await DysonAgentSystemPrompts.BuildSessionSystemPromptSuffixAsync(
                _models, providerKind, _workDirectoryPath, cancellationToken)
            .ConfigureAwait(false);

        var child = new OpenAiCompatibleAgentSession(
            agentMode,
            Config,
            childProvider,
            _http,
            _workDirectoryPath,
            _store,
            _workDirectoryId,
            _models,
            suffix,
            _usageAnalytics,
            _workDirectoryName);

        RegisterSubagent(child);

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
            },
            cancellationToken).ConfigureAwait(false);

        if (create.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(create.Error);

        child.SetPersistenceId(create.Value);

        if (initialTodos is { Count: > 0 })
        {
            var seeded = await child.ReplaceTodosAsync(initialTodos, cancellationToken).ConfigureAwait(false);
            if (seeded.IsError)
                return Result<DysonStartSubagentResult, string>.AsError(seeded.Error);
        }

        var createdLog = DysonSessionLogPayload.CreateEntry(
            create.Value,
            DysonSessionLogKind.SessionCreated,
            new DysonSessionLogSessionCreated(create.Value, agentMode, RuntimeId: child.Id));

        var append = await _store.AppendLogAsync(createdLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(append.Error);

        var runCts = new CancellationTokenSource();
        child.AttachBackgroundRun(runCts);
        KickOffChildPrompt(
            child,
            DysonSessionInitialization.CreateTurn(BuildChildFirstPrompt(agentMode, task, context)),
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
        if (Config.TryGetSubagentDefaultWhenSlugOmitted(modelSlug, agentMode)
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

        if (_models is null)
            return Result<OpenAiCompatibleAgentProvider, string>.AsError(
                "Model store is required to resolve modelSlug.");

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
        CancellationToken cancellationToken,
        bool allowDropContextInject = true)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(filePaths);

        ApplyUiTheme(Config.UiTheme);

        // Compaction before the next provider request so the new prefix stays byte-stable.
        OptimizeContextIfNeeded();

        if (allowDropContextInject
            && turn.Kind != DysonAgentTurnKind.DropContext
            && turn.Kind != DysonAgentTurnKind.FullSummarize
            && DysonDropContextFlow.TryBeginInject(this))
        {
            var drop = await PromptWithTurnAsync(
                    CreateDropContextTurn(),
                    [],
                    cancellationToken,
                    allowDropContextInject: false)
                .ConfigureAwait(false);
            if (drop.IsError)
                return drop;
        }

        AppendLog($"prompt: {Truncate(turn.Instruction ?? turn.Kind.ToString(), 120)}");
        AddTurn(turn);
        using var inFlightPrompt = BeginInFlightPrompt(turn);

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(_workDirectoryPath, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
            return new VoidResult<string>($"Workspace filesystem: {fsResult.Error}");

        var executor = new DysonWorkspaceToolExecutor(this, fsResult.Value, _http, _store, _workDirectoryId);
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

        try
        {
            for (var round = 0; round < maxRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Result<OpenAiModelReply, string> replyResult;
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
                    }
                    else
                    {
                        await EnsureResponsesVisionFileIdsAsync(inFlight, cancellationToken)
                            .ConfigureAwait(false);
                        built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                            this,
                            currentUserPrompt: harnessFollowUp,
                            currentFilePaths: null,
                            inFlightRounds: inFlight,
                            previousResponseId: supportsResponsesChaining ? previousResponseId : null);
                        if (round == 0 && filePaths.Count > 0)
                            AppendPathsToLastUser(built.Input, filePaths);
                    }

                    replyResult = await ConsumeStreamWithTransientRetryAsync(
                        () => _responses.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                        turn,
                        cancellationToken).ConfigureAwait(false);

                    // Direct only: one full-replay retry when store chaining lost the function_call.
                    if (replyResult.IsError
                        && supportsResponsesChaining
                        && OpenAiCompatibleHttp.IsMissingToolCallForOutputError(replyResult.Error))
                    {
                        AppendLog("Responses: missing tool call for output — retrying with full item replay");
                        previousResponseId = null;
                        await EnsureResponsesVisionFileIdsAsync(inFlight, cancellationToken)
                            .ConfigureAwait(false);
                        var retryBuilt = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                            this,
                            currentUserPrompt: harnessFollowUp,
                            currentFilePaths: null,
                            inFlightRounds: inFlight,
                            previousResponseId: null);
                        if (round == 0 && filePaths.Count > 0)
                            AppendPathsToLastUser(retryBuilt.Input, filePaths);

                        replyResult = await ConsumeStreamWithTransientRetryAsync(
                            () => _responses.StreamCreateAsync(OpenAiProvider, retryBuilt, cancellationToken),
                            turn,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                        this,
                        currentUserPrompt: harnessFollowUp,
                        currentFilePaths: null,
                        inFlightRounds: inFlight);
                    if (round == 0 && filePaths.Count > 0)
                        AppendPathsToLastUser(built.Messages, filePaths);

                    replyResult = await ConsumeStreamWithTransientRetryAsync(
                        () => _completions.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                        turn,
                        cancellationToken).ConfigureAwait(false);
                }

                if (replyResult.IsError)
                {
                    CommitReasoningRound(turn, reply: null, round, isFinalAssistant: true);
                    turn.ClearStreamingPreview();
                    turn.ClearReasoningPreview();
                    turn.FinalizeIncompleteTools(incompleteToolReason);
                    return new VoidResult<string>(replyResult.Error);
                }

                var reply = replyResult.Value;
                await RecordUsageAsync(reply, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply.UsageCacheHint))
                    AppendLog(reply.UsageCacheHint);
                if (reply.PromptTokens is int promptTokens)
                    LastReportedPromptTokens = promptTokens;

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

                    AppendLog($"tool round {round + 1}: {reply.ToolCalls.Count} call(s)");

                    var ending = roundResults.FirstOrDefault(r => r.EndsCurrentTurn && !r.IsError);
                    if (ending is not null)
                    {
                        AppendLog($"tool round {round + 1}: ends current turn ({ending.ToolName})");
                        return SoftCloseAfterEndsCurrentTurn(turn, ending.ToolName, reply.Content);
                    }

                    // Continuing tool loop — interim assistant words (not final reply).
                    CommitInterimText(turn, reply.Content, round);
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

                // Title parse only at finalize — preview stays raw (incl. mid-stream H1) until then.
                CommitReasoningRound(turn, reply, round, isFinalAssistant: true);
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

        Result<OpenAiModelReply, string> replyResult;
        if (useResponses)
        {
            await EnsureResponsesVisionFileIdsAsync(inFlight, cancellationToken)
                .ConfigureAwait(false);
            var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                this,
                currentUserPrompt: DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction,
                currentFilePaths: null,
                inFlightRounds: inFlight,
                previousResponseId: null);
            built.Tools.Clear();
            replyResult = await ConsumeStreamWithTransientRetryAsync(
                () => _responses.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                turn,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                this,
                currentUserPrompt: DysonRethinkToolUsageFlow.ExploreBudgetRecapInstruction,
                currentFilePaths: null,
                inFlightRounds: inFlight);
            built.Tools.Clear();
            replyResult = await ConsumeStreamWithTransientRetryAsync(
                () => _completions.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                turn,
                cancellationToken).ConfigureAwait(false);
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

    private async Task RecordUsageAsync(OpenAiModelReply reply, CancellationToken cancellationToken)
    {
        if (_usageAnalytics is null)
            return;

        var parsed = reply.Usage ?? new DysonParsedUsage();
        var alias = string.IsNullOrWhiteSpace(OpenAiProvider.DisplayAlias)
            ? OpenAiProvider.Slug
            : OpenAiProvider.DisplayAlias;
        var row = new DysonUsageRequestEntity
        {
            Id = Guid.NewGuid(),
            WorkDirectoryName = _workDirectoryName,
            SessionId = PersistenceId,
            RootSessionId = ResolveRootPersistenceId(),
            ModelSlug = OpenAiProvider.Slug,
            ModelDisplayAlias = alias,
            ReasoningEffort = OpenAiProvider.ReasoningEffort ?? "",
            OccurredUtc = DateTime.UtcNow,
            InputTokens = parsed.InputTokens,
            CacheTokens = parsed.CacheTokens,
            WriteTokens = parsed.WriteTokens,
            CacheWriteTokens = parsed.CacheWriteTokens,
            InputTokensAfterCache = parsed.InputTokensAfterCache,
            WriteTokensAfterCache = parsed.WriteTokensAfterCache,
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
    /// Responses vision: upload turn <see cref="DysonAgentTurn.UserImages"/> plus one-shot
    /// tool BinaryAttachment on the last in-flight round (data-URL fallback on failure).
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
                images.Add(image);
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
        CancellationToken cancellationToken) =>
        OpenAiFilesClient.EnsureBinaryFileIdsAsync(
            _http,
            OpenAiProvider,
            results,
            note => AppendLog(note),
            cancellationToken);

    private static void AppendPathsToLastUser(
        System.Text.Json.Nodes.JsonArray messagesOrInput,
        IReadOnlyList<string> filePaths)
    {
        for (var i = messagesOrInput.Count - 1; i >= 0; i--)
        {
            if (messagesOrInput[i] is not System.Text.Json.Nodes.JsonObject msg)
                continue;
            if (msg["role"]?.GetValue<string>() != "user")
                continue;
            if (msg["content"] is not System.Text.Json.Nodes.JsonValue contentVal
                || !contentVal.TryGetValue<string>(out var text))
            {
                continue;
            }

            var sb = new System.Text.StringBuilder(text);
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("Attached paths:");
            foreach (var path in filePaths)
                sb.AppendLine($"- {path}");
            msg["content"] = sb.ToString().TrimEnd();
            return;
        }
    }

    /// <summary>
    /// Backoff between transient stream retries (ms). 4 delays ⇒ 5 attempts total.
    /// Tests may replace with zeros; restore defaults afterward.
    /// </summary>
    internal static int[] TransientRetryBackoffMs { get; set; } = [2000, 5000, 10000, 10000];

    /// <summary>
    /// Consumes one inference stream round, reopening the request on transient 429/502/503/504
    /// (fixed backoff). Clears streaming/reasoning previews before each retry.
    /// </summary>
    private async Task<Result<OpenAiModelReply, string>> ConsumeStreamWithTransientRetryAsync(
        Func<IAsyncEnumerable<Result<OpenAiStreamChunk, string>>> streamFactory,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(turn);

        var delays = TransientRetryBackoffMs;
        var maxAttempts = delays.Length + 1;
        string? lastError = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = delays[attempt - 1];
                AppendLog(FormatTransientRetryLog(lastError, attempt, delays.Length, delayMs));
                turn.ClearStreamingPreview();
                turn.ClearReasoningPreview();
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            var result = await ConsumeStreamAsync(streamFactory(), turn, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsError)
                return result;

            lastError = result.Error;
            if (!OpenAiCompatibleHttp.IsTransientServerError(lastError) || attempt == maxAttempts - 1)
                return result;
        }

        return Result<OpenAiModelReply, string>.AsError(lastError ?? "OpenAI request failed.");
    }

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

    private static async Task<Result<OpenAiModelReply, string>> ConsumeStreamAsync(
        IAsyncEnumerable<Result<OpenAiStreamChunk, string>> stream,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        OpenAiModelReply? completed = null;

        try
        {
            await foreach (var item in stream.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (item.IsError)
                    return Result<OpenAiModelReply, string>.AsError(item.Error);

                var chunk = item.Value;
                if (!string.IsNullOrEmpty(chunk.TextDelta))
                    turn.AppendStreamingDelta(chunk.TextDelta);

                if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                    turn.AppendReasoningDelta(chunk.ReasoningDelta);

                if (chunk.IsRoundComplete)
                    completed = chunk.CompletedReply;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<OpenAiModelReply, string>.AsError("OpenAI stream was cancelled.");
        }

        if (completed is null)
            return Result<OpenAiModelReply, string>.AsError("OpenAI stream ended without a completed reply.");

        return Result<OpenAiModelReply, string>.AsValue(completed);
    }

    private static void ApplyAssistantText(DysonAgentTurn turn, string text)
    {
        var parsed = DysonAgentTurn.TryParseAgentTitle(text);
        if (parsed.IsSuccess)
        {
            turn.AgentTitle = parsed.Value.Title;
            turn.AssistantText = parsed.Value.Body;
        }
        else
        {
            turn.AssistantText = text;
        }
    }

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
