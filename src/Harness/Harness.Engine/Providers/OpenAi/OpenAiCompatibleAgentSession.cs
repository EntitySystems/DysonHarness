namespace DysonHarness;

/// <summary>
/// OpenAI-compatible agent session: Completions or Responses tool loop with cache-friendly requests.
/// </summary>
public sealed class OpenAiCompatibleAgentSession : DysonAgentSession
{
    public const int MaxToolRounds = 20;

    private readonly DysonSessionStore? _store;
    private readonly HttpClient _http;
    private readonly string _workDirectoryPath;
    private Guid _workDirectoryId;
    private readonly OpenAiCompletionsClient _completions;
    private readonly OpenAiResponsesClient _responses;

    public OpenAiCompatibleAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        string workDirectoryAbsolutePath,
        DysonSessionStore? store = null,
        Guid workDirectoryId = default)
        : base(agentMode, config, provider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workDirectoryPath = Path.GetFullPath(workDirectoryAbsolutePath);
        _store = store;
        _workDirectoryId = workDirectoryId;
        _completions = new OpenAiCompletionsClient(_http);
        _responses = new OpenAiResponsesClient(_http);
    }

    public OpenAiCompatibleAgentProvider OpenAiProvider => (OpenAiCompatibleAgentProvider)Provider;

    public string WorkDirectoryPath => _workDirectoryPath;

    public Guid WorkDirectoryId => _workDirectoryId;

    public static async Task<Result<OpenAiCompatibleAgentSession, string>> CreateAsync(
        DysonSessionStore store,
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        Guid workDirectoryId,
        string workDirectoryAbsolutePath,
        string agentMode = DysonAgentModes.Work,
        DysonAgentSessionConfig? config = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(http);

        if (workDirectoryId == Guid.Empty)
            return Result<OpenAiCompatibleAgentSession, string>.AsError("Work directory is required.");

        config ??= new DysonAgentSessionConfig();
        var session = new OpenAiCompatibleAgentSession(
            agentMode, config, provider, http, workDirectoryAbsolutePath, store, workDirectoryId);
        var initialTitle = title ?? "New session";
        session.SetDisplayTitle(initialTitle);

        var create = await store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = agentMode,
                ModelSlugId = provider.SlugId,
                WorkDirectoryId = workDirectoryId,
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
        DysonSessionStore store,
        Guid sessionId,
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        string workDirectoryAbsolutePath,
        DysonAgentSessionConfig? config = null,
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

        var session = new OpenAiCompatibleAgentSession(
            state.Session.AgentMode,
            config,
            provider,
            http,
            workDirectoryAbsolutePath,
            store,
            state.Session.WorkDirectoryId ?? Guid.Empty);
        session.RestoreFromPersisted(state);

        var resumedLog = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.SessionResumed,
            new DysonSessionLogSessionResumed(sessionId));

        var append = await store.AppendLogAsync(resumedLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<OpenAiCompatibleAgentSession, string>.AsError(append.Error);

        return Result<OpenAiCompatibleAgentSession, string>.AsValue(session);
    }

    public override async Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
        string agentMode,
        string task,
        string? context = null,
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

        var child = new OpenAiCompatibleAgentSession(
            agentMode,
            Config,
            OpenAiProvider,
            _http,
            _workDirectoryPath,
            _store,
            _workDirectoryId);

        RegisterSubagent(child);

        var title = TitleFromTask(task);
        child.SetDisplayTitle(title);

        var create = await _store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = child.Id,
                ParentSessionId = PersistenceId,
                AgentMode = agentMode,
                ModelSlugId = OpenAiProvider.SlugId,
                WorkDirectoryId = _workDirectoryId,
                McpAccessMode = Config.McpAccessMode,
                Title = title,
                SystemPromptSnapshot = child.SystemPrompt,
                Status = DysonSessionStatus.Active,
            },
            cancellationToken).ConfigureAwait(false);

        if (create.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(create.Error);

        child.SetPersistenceId(create.Value);

        var createdLog = DysonSessionLogPayload.CreateEntry(
            create.Value,
            DysonSessionLogKind.SessionCreated,
            new DysonSessionLogSessionCreated(create.Value, agentMode, RuntimeId: child.Id));

        var append = await _store.AppendLogAsync(createdLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<DysonStartSubagentResult, string>.AsError(append.Error);

        var runCts = new CancellationTokenSource();
        child.AttachBackgroundRun(runCts);
        KickOffChildPrompt(child, BuildChildFirstPrompt(agentMode, task, context), runCts);

        AppendLog($"started subagent {child.Id} ({agentMode}): {title}");

        return Result<DysonStartSubagentResult, string>.AsValue(new DysonStartSubagentResult
        {
            SubagentId = child.Id,
            PersistenceId = child.PersistenceId,
            AgentMode = agentMode,
            Title = title,
        });
    }

    public override Task<VoidResult<string>> LoadFunctionalContextAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(VoidResult<string>.Success);

    public override Task<VoidResult<string>> PromptAsync(
        string prompt,
        CancellationToken cancellationToken = default) =>
        PromptAsync(prompt, [], cancellationToken);

    public override async Task<VoidResult<string>> PromptAsync(
        string prompt,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(filePaths);

        // Compaction before the next provider request so the new prefix stays byte-stable.
        OptimizeContextIfNeeded();

        AppendLog($"prompt: {Truncate(prompt, 120)}");

        var turn = TurnHistory.Count == 0
            ? DysonSessionInitialization.CreateTurn(prompt)
            : new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = prompt,
                StartedUtc = DateTime.UtcNow,
            };
        AddTurn(turn);

        var executor = new DysonWorkspaceToolExecutor(this, _workDirectoryPath, _http, _store);
        var inFlight = new List<OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound>();
        var useResponses = string.Equals(
            OpenAiProvider.OpenAiApiMode,
            DysonOpenAiApiModes.Responses,
            StringComparison.Ordinal);
        string? previousResponseId = null;
        var childReportNudged = false;
        string? harnessFollowUp = null;
        const string incompleteToolReason =
            OpenAiCacheFriendlyTranscriptBuilder.IncompleteToolResultContent;
        const string childReportNudge =
            "Harness: plain text does not finish this subagent. Call SubmitSubagentReport now with your findings (or blocker).";
        const string childReportMissing =
            "Child PromptAsync ended without SubmitSubagentReport.";

        try
        {
            for (var round = 0; round < MaxToolRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Result<OpenAiModelReply, string> replyResult;
                if (useResponses)
                {
                    // Local-first: rebuild full input from compacted history + in-flight rounds
                    // (store: false). Use previous_response_id only for same-PromptAsync deltas
                    // when we already have a response id from this loop.
                    OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest built;
                    if (previousResponseId is not null && inFlight.Count > 0 && harnessFollowUp is null)
                    {
                        built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesDelta(
                            this,
                            previousResponseId,
                            inFlight[^1].Results);
                    }
                    else
                    {
                        built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                            this,
                            currentUserPrompt: harnessFollowUp,
                            currentFilePaths: null,
                            inFlightRounds: inFlight);
                        if (round == 0 && filePaths.Count > 0)
                            AppendPathsToLastUser(built.Input, filePaths);
                    }

                    replyResult = await ConsumeStreamAsync(
                        _responses.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                        turn,
                        cancellationToken).ConfigureAwait(false);
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

                    replyResult = await ConsumeStreamAsync(
                        _completions.StreamCreateAsync(OpenAiProvider, built, cancellationToken),
                        turn,
                        cancellationToken).ConfigureAwait(false);
                }

                if (replyResult.IsError)
                {
                    turn.ClearStreamingPreview();
                    turn.FinalizeIncompleteTools(incompleteToolReason);
                    return new VoidResult<string>(replyResult.Error);
                }

                var reply = replyResult.Value;
                if (!string.IsNullOrEmpty(reply.UsageCacheHint))
                    AppendLog(reply.UsageCacheHint);

                if (!string.IsNullOrEmpty(reply.ResponseId))
                    previousResponseId = reply.ResponseId;

                if (reply.ToolCalls.Count > 0)
                {
                    turn.ClearStreamingPreview();

                    foreach (var call in reply.ToolCalls)
                        turn.ToolCalls.Add(call);

                    var staged = await DysonToolCallScheduler.RunStagedAsync(
                        turn,
                        executor.ExecuteAsync,
                        cancellationToken).ConfigureAwait(false);

                    if (staged.IsError)
                    {
                        turn.ClearStreamingPreview();
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
                        roundResults));

                    AppendLog($"tool round {round + 1}: {reply.ToolCalls.Count} call(s)");
                    continue;
                }

                var text = string.IsNullOrWhiteSpace(reply.Content)
                    ? "# Empty reply\n\nThe model returned no content."
                    : reply.Content;

                if (Parent is not null && !TurnHasSubmitSubagentReport(turn))
                {
                    if (!childReportNudged)
                    {
                        // Keep AssistantText unset so history stays incomplete and tools remain inFlight-only.
                        turn.ClearStreamingPreview();
                        previousResponseId = null;
                        harnessFollowUp =
                            $"Your previous assistant reply was not accepted as a finish:\n\n{text}\n\n{childReportNudge}";
                        childReportNudged = true;
                        AppendLog("child report gate: nudged for SubmitSubagentReport");
                        continue;
                    }

                    turn.ClearStreamingPreview();
                    turn.FinalizeIncompleteTools(incompleteToolReason);
                    AppendLog("child report gate: missing SubmitSubagentReport after nudge");
                    return new VoidResult<string>(childReportMissing);
                }

                // Title parse only at finalize — preview stays raw (incl. mid-stream H1) until then.
                ApplyAssistantText(turn, text);
                turn.FinishStreaming();
                AppendLog($"turn complete: {turn.AgentTitle ?? turn.Id.ToString("N")[..8]}");
                return VoidResult<string>.Success;
            }

            turn.ClearStreamingPreview();
            turn.FinalizeIncompleteTools(incompleteToolReason);
            return new VoidResult<string>($"Tool loop exceeded {MaxToolRounds} rounds without a final reply.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn.ClearStreamingPreview();
            turn.FinalizeIncompleteTools(incompleteToolReason);
            return new VoidResult<string>("Prompt was cancelled.");
        }
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

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max] + "…";
    }
}
