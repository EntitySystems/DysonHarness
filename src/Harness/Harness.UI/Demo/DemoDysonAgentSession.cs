using System.Text.Json;
using DysonHarness;

namespace Harness.UI.Demo;

public sealed class DemoDysonAgentSession : DysonAgentSession
{
    private readonly DysonSessionStore? _store;
    private Guid _workDirectoryId;

    public DemoDysonAgentSession(
        string agentMode,
        DysonAgentSessionConfig config,
        DysonAgentProvider provider,
        DysonSessionStore? store = null,
        Guid workDirectoryId = default)
        : base(agentMode, config, provider)
    {
        _store = store;
        SessionStore = store;
        _workDirectoryId = workDirectoryId;
    }

    public Guid WorkDirectoryId => _workDirectoryId;

    /// <summary>
    /// Creates a new persisted root session and assigns <see cref="DysonAgentSession.PersistenceId"/>.
    /// </summary>
    public static async Task<Result<DemoDysonAgentSession, string>> CreateAsync(
        DysonSessionStore store,
        DemoDysonAgentProvider provider,
        Guid workDirectoryId,
        string agentMode = DysonAgentModes.Work,
        DysonAgentSessionConfig? config = null,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        if (workDirectoryId == Guid.Empty)
            return Result<DemoDysonAgentSession, string>.AsError("Work directory is required.");

        config ??= new DysonAgentSessionConfig();
        var session = new DemoDysonAgentSession(agentMode, config, provider, store, workDirectoryId);
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
            return Result<DemoDysonAgentSession, string>.AsError(create.Error);

        session.SetPersistenceId(create.Value);

        var createdLog = DysonSessionLogPayload.CreateEntry(
            create.Value,
            DysonSessionLogKind.SessionCreated,
            new DysonSessionLogSessionCreated(create.Value, agentMode, RuntimeId: 0));

        var append = await store.AppendLogAsync(createdLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(append.Error);

        return Result<DemoDysonAgentSession, string>.AsValue(session);
    }

    /// <summary>
    /// Loads a persisted session, hydrates turns/logs, and appends a SessionResumed log.
    /// </summary>
    public static async Task<Result<DemoDysonAgentSession, string>> LoadAsync(
        DysonSessionStore store,
        Guid sessionId,
        DemoDysonAgentProvider provider,
        DysonAgentSessionConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);

        var full = await store.GetFullSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (full.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(full.Error);

        var state = full.Value;
        config ??= new DysonAgentSessionConfig
        {
            McpAccessMode = state.Session.McpAccessMode,
        };

        var session = new DemoDysonAgentSession(
            state.Session.AgentMode,
            config,
            provider,
            store,
            state.Session.WorkDirectoryId ?? Guid.Empty);
        session.RestoreFromPersisted(state);

        var resumedLog = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.SessionResumed,
            new DysonSessionLogSessionResumed(sessionId));

        var append = await store.AppendLogAsync(resumedLog, cancellationToken).ConfigureAwait(false);
        if (append.IsError)
            return Result<DemoDysonAgentSession, string>.AsError(append.Error);

        return Result<DemoDysonAgentSession, string>.AsValue(session);
    }

    public override async Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
        string agentMode,
        string task,
        string? context = null,
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
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

        var child = new DemoDysonAgentSession(agentMode, Config, Provider, _store, _workDirectoryId);
        RegisterSubagent(child);

        var title = TitleFromTask(task);
        child.SetDisplayTitle(title);

        Guid? modelSlugId = Provider is DemoDysonAgentProvider demo ? demo.SlugId : null;

        var create = await _store.CreateSessionAsync(
            new DysonSessionCreateRequest
            {
                RuntimeId = child.Id,
                ParentSessionId = PersistenceId,
                AgentMode = agentMode,
                ModelSlugId = modelSlugId,
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

        OptimizeContextIfNeeded();

        AppendLog($"prompt: {Truncate(prompt, 120)}");

        var isRenameReview = TurnHistory.Count % DysonSessionInitialization.RenameSessionReviewInterval == 0;
        var turn = TurnHistory.Count == 0
            ? DysonSessionInitialization.CreateTurn(prompt)
            : new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = prompt,
                StartedUtc = DateTime.UtcNow,
            };

        // Mock RenameSession only on review cadence (turns 1, 9, 17, …) — not every turn.
        if (isRenameReview)
        {
            var renameTitle = Truncate(prompt.Trim(), 64);
            turn.ToolCalls.Add(new DysonToolCall
            {
                CallId = "",
                ToolName = "RenameSession",
                Stage = 0,
                ArgumentsJson = JsonSerializer.Serialize(new { title = renameTitle }),
            });
        }

        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "",
            ToolName = "read_file",
            Stage = 0,
            ArgumentsJson = """{"path":"README.md"}""",
        });
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "",
            ToolName = "grep",
            Stage = 0,
            ArgumentsJson = """{"pattern":"Dyson","path":"."}""",
        });
        turn.ToolCalls.Add(new DysonToolCall
        {
            CallId = "",
            ToolName = "list_dir",
            Stage = 1,
            ArgumentsJson = """{"path":"src"}""",
        });

        AddTurn(turn);

        try
        {
            var staged = await DysonToolCallScheduler.RunStagedAsync(
                turn,
                ExecuteMockToolAsync,
                cancellationToken).ConfigureAwait(false);

            if (staged.IsError)
                return staged;

            var reply =
                $"# Demo turn\n\nProcessed: {Truncate(prompt, 200)}\n\n" +
                $"Tools completed: {turn.TrackedToolCalls.Count} " +
                $"(provider: {DescribeProvider()}).";

            foreach (var chunk in ChunkForStreaming(reply, chunkSize: 12))
            {
                turn.AppendStreamingDelta(chunk);
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            // Title parse only at finalize — preview stays raw until FinishStreaming.
            var parsed = DysonAgentTurn.TryParseAgentTitle(reply);
            if (parsed.IsSuccess)
            {
                turn.AgentTitle = parsed.Value.Title;
                turn.AssistantText = parsed.Value.Body;
            }
            else
            {
                turn.AssistantText = reply;
            }

            turn.FinishStreaming();

            AppendLog($"turn complete: {turn.AgentTitle ?? turn.Id.ToString("N")[..8]}");
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn.ClearStreamingPreview();
            return new VoidResult<string>("Prompt was cancelled.");
        }
    }

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

    private string DescribeProvider() =>
        Provider is DemoDysonAgentProvider demo
            ? $"{demo.ProviderKind}/{demo.Slug}"
            : Provider.GetType().Name;

    private async Task<DysonToolCallResult> ExecuteMockToolAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (string.Equals(call.ToolName, "RenameSession", StringComparison.OrdinalIgnoreCase))
            return await ExecuteRenameSessionAsync(call, cancellationToken).ConfigureAwait(false);

        if (string.Equals(call.ToolName, "StartSubagent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(call.ToolName, "WaitForSubagent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(call.ToolName, "InspectSubagentLog", StringComparison.OrdinalIgnoreCase)
            || string.Equals(call.ToolName, "StopSubagent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(call.ToolName, "SubmitSubagentReport", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteSubagentToolAsync(call, cancellationToken).ConfigureAwait(false);
        }

        var delayMs = 180 + (Math.Abs(call.ToolName.GetHashCode(StringComparison.Ordinal)) % 220);
        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

        return new DysonToolCallResult
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"[demo] {call.ToolName} ok — args={Truncate(call.ArgumentsJson, 80)}",
        };
    }

    private async Task<DysonToolCallResult> ExecuteSubagentToolAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var root = doc.RootElement;

            if (string.Equals(call.ToolName, "StartSubagent", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("agentMode", out var modeProp)
                    || string.IsNullOrWhiteSpace(modeProp.GetString()))
                {
                    return ToolError(call, "StartSubagent: agentMode is required.");
                }

                if (!root.TryGetProperty("task", out var taskProp)
                    || string.IsNullOrWhiteSpace(taskProp.GetString()))
                {
                    return ToolError(call, "StartSubagent: task is required.");
                }

                var context = root.TryGetProperty("context", out var ctxProp)
                    ? ctxProp.GetString()
                    : null;
                var todos = DysonWorkspaceToolExecutor.TryParseTodoSeedItems(root, "todos");
                if (todos.IsError)
                    return ToolError(call, todos.Error);

                var started = await CreateChildAsync(
                        modeProp.GetString()!,
                        taskProp.GetString()!,
                        context,
                        todos.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (started.IsError)
                    return ToolError(call, started.Error);

                var r = started.Value;
                return ToolOk(call, JsonSerializer.Serialize(new
                {
                    subagentId = r.SubagentId,
                    persistenceId = r.PersistenceId,
                    agentMode = r.AgentMode,
                    title = r.Title,
                }));
            }

            if (string.Equals(call.ToolName, "WaitForSubagent", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSubagentId(root, out var waitId))
                    return ToolError(call, "WaitForSubagent: subagentId (≥ 1) is required.");

                int? timeoutMs = null;
                if (root.TryGetProperty("timeoutMs", out var tProp) && tProp.TryGetInt32(out var tVal))
                    timeoutMs = tVal;

                var waited = await WaitForSubagentAsync(waitId, timeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                return waited.IsError ? ToolError(call, waited.Error) : ToolOk(call, waited.Value);
            }

            if (string.Equals(call.ToolName, "InspectSubagentLog", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSubagentId(root, out var inspectId))
                    return ToolError(call, "InspectSubagentLog: subagentId (≥ 1) is required.");

                int? maxLines = null;
                if (root.TryGetProperty("maxLines", out var mProp) && mProp.TryGetInt32(out var mVal))
                    maxLines = mVal;

                var inspected = InspectSubagentLog(inspectId, maxLines);
                return inspected.IsError ? ToolError(call, inspected.Error) : ToolOk(call, inspected.Value);
            }

            if (string.Equals(call.ToolName, "StopSubagent", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadSubagentId(root, out var stopId))
                    return ToolError(call, "StopSubagent: subagentId (≥ 1) is required.");

                var reason = root.TryGetProperty("reason", out var reasonProp)
                    ? reasonProp.GetString()
                    : null;
                if (!TryGetSubagent(stopId, out var child))
                    return ToolError(call, $"Unknown subagentId {stopId}.");

                var stopped = await StopSubagentAsync(stopId, reason, cancellationToken)
                    .ConfigureAwait(false);
                if (stopped.IsError)
                    return ToolError(call, stopped.Error);

                await PersistChildStatusAsync(child, child.Status, reason, cancellationToken)
                    .ConfigureAwait(false);
                return ToolOk(call, stopped.Value);
            }

            // SubmitSubagentReport
            if (!root.TryGetProperty("summary", out var summaryProp)
                || string.IsNullOrWhiteSpace(summaryProp.GetString()))
            {
                return ToolError(call, "SubmitSubagentReport: summary is required.");
            }

            var failed = false;
            if (root.TryGetProperty("status", out var statusProp)
                && statusProp.ValueKind == JsonValueKind.String)
            {
                var status = statusProp.GetString();
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    failed = true;
                else if (!string.IsNullOrWhiteSpace(status)
                         && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return ToolError(call, "SubmitSubagentReport: status must be 'completed' or 'failed'.");
                }
            }

            var skipTasksCheck = root.TryGetProperty("skipTasksCheck", out var skipProp)
                && skipProp.ValueKind == JsonValueKind.True;

            var summary = summaryProp.GetString()!;
            var submitted = await SubmitSubagentReportAsync(
                    summary,
                    failed,
                    skipTasksCheck,
                    cancellationToken)
                .ConfigureAwait(false);
            if (submitted.IsError)
                return ToolError(call, submitted.Error);

            await PersistChildStatusAsync(this, Status, summary, cancellationToken).ConfigureAwait(false);
            return ToolOk(call, submitted.Value);
        }
        catch (JsonException)
        {
            return ToolError(call, $"{call.ToolName}: invalid JSON arguments.");
        }
    }

    private async Task PersistChildStatusAsync(
        DysonAgentSession session,
        DysonSessionStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (_store is null || session.PersistenceId == Guid.Empty)
            return;

        await _store.UpdateSessionMetaAsync(
            new DysonSessionMetaUpdate
            {
                SessionId = session.PersistenceId,
                Status = status,
            },
            cancellationToken).ConfigureAwait(false);

        var statusLog = DysonSessionLogPayload.CreateEntry(
            session.PersistenceId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(status, reason));

        await _store.AppendLogAsync(statusLog, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadSubagentId(JsonElement root, out int subagentId)
    {
        subagentId = 0;
        if (!root.TryGetProperty("subagentId", out var prop))
            return false;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n) && n >= 1)
        {
            subagentId = n;
            return true;
        }

        return false;
    }

    private static DysonToolCallResult ToolOk(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = content,
        };

    private static DysonToolCallResult ToolError(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = true,
            Content = content,
        };

    private async Task<DysonToolCallResult> ExecuteRenameSessionAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? title = null;
        try
        {
            using var doc = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
                title = titleProp.GetString();
        }
        catch (JsonException)
        {
            return new DysonToolCallResult
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Stage = call.Stage,
                IsError = true,
                Content = "RenameSession: invalid JSON arguments.",
            };
        }

        var rename = await RenameAsync(title ?? "", cancellationToken).ConfigureAwait(false);
        if (rename.IsError)
        {
            return new DysonToolCallResult
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Stage = call.Stage,
                IsError = true,
                Content = rename.Error,
            };
        }

        if (_store is not null && PersistenceId != Guid.Empty)
        {
            var persist = await _store.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = PersistenceId,
                    Title = DisplayTitle,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                return new DysonToolCallResult
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    Stage = call.Stage,
                    IsError = true,
                    Content = persist.Error,
                };
            }

            var renamedLog = DysonSessionLogPayload.CreateEntry(
                PersistenceId,
                DysonSessionLogKind.SessionRenamed,
                new DysonSessionLogSessionRenamed(DisplayTitle!));

            await _store.AppendLogAsync(renamedLog, cancellationToken).ConfigureAwait(false);
        }

        return new DysonToolCallResult
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"Renamed session to \"{DisplayTitle}\".",
        };
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value;
        return value[..max] + "…";
    }

    private static IEnumerable<string> ChunkForStreaming(string text, int chunkSize)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        for (var i = 0; i < text.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, text.Length - i);
            yield return text.Substring(i, len);
        }
    }
}
