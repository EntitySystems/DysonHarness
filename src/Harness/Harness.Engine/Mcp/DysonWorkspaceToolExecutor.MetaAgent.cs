using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private readonly IDysonPlanRepository? _plans;

    private async Task<DysonToolCallResult> CreateAsyncMetaAgentDroneAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string task;
        string? context;
        string? modelSlug;
        string? reasoningEffort;
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos;
        var purpose = "build";
        bool useWorktree;
        string? existingWorktreePath;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var taskResult = RequireString(root, "task");
            if (taskResult.IsError)
                return Error(call, taskResult.Error);
            task = taskResult.Value;
            var useWorktreeResult = RequireBool(root, "useWorktree");
            if (useWorktreeResult.IsError)
                return Error(call, "CreateAsyncMetaAgentDrone: " + useWorktreeResult.Error);
            useWorktree = useWorktreeResult.Value;
            existingWorktreePath = GetOptionalString(root, "existingWorktreePath");
            context = GetOptionalString(root, "context");
            modelSlug = GetOptionalString(root, "modelSlug");
            reasoningEffort = GetOptionalString(root, "reasoningEffort");

            var purposeRaw = GetOptionalString(root, "purpose");
            if (purposeRaw is not null)
            {
                if (string.Equals(purposeRaw, "plan", StringComparison.OrdinalIgnoreCase))
                    purpose = "plan";
                else if (string.Equals(purposeRaw, "build", StringComparison.OrdinalIgnoreCase))
                    purpose = "build";
                else
                    return Error(call, "CreateAsyncMetaAgentDrone: purpose must be 'build' or 'plan'.");
            }

            var todos = TryParseTodoSeedItems(root, "todos");
            if (todos.IsError)
                return Error(call, todos.Error);
            initialTodos = todos.Value;
        }
        catch (JsonException)
        {
            return Error(call, "CreateAsyncMetaAgentDrone: invalid JSON arguments.");
        }

        if (existingWorktreePath is not null && useWorktree)
        {
            return Error(
                call,
                "CreateAsyncMetaAgentDrone: existingWorktreePath requires useWorktree false.");
        }

        if (existingWorktreePath is not null)
        {
            var listed = RequireListedWorktree(existingWorktreePath);
            if (listed.IsError)
                return Error(call, listed.Error);
            existingWorktreePath = listed.Value;
        }

        if (string.Equals(purpose, "plan", StringComparison.OrdinalIgnoreCase))
        {
            task =
                "This brief asks you to write a plan. Explore first, then SubmitMetaPlan, then SubmitSubagentReport with the planId. Do not implement and do not commit.\n\n"
                + task;
        }

        var started = await CreateMetaAgentDroneChildAsync(
                task,
                context,
                initialTodos,
                modelSlug,
                reasoningEffort,
                useWorktree,
                cancellationToken,
                existingWorktreePath)
            .ConfigureAwait(false);
        if (started.IsError)
            return Error(call, started.Error);

        var r = started.Value;
        _session.TryGetSubagent(r.SubagentId, out var child);
        return Ok(call, JsonSerializer.Serialize(new
        {
            droneId = r.SubagentId,
            persistenceId = r.PersistenceId,
            worktreeBranch = child?.WorktreeBranch,
        }));
    }

    private async Task<DysonToolCallResult> StartAsyncExploreAgentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string task;
        string? context;
        IReadOnlyList<string>? contextFiles;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var taskResult = RequireString(root, "task");
            if (taskResult.IsError)
                return Error(call, taskResult.Error);
            task = taskResult.Value;
            context = GetOptionalString(root, "context");
            var files = TryParseOptionalStringArray(root, "contextFiles");
            if (files.IsError)
                return Error(call, files.Error);
            contextFiles = files.Value;
        }
        catch (JsonException)
        {
            return Error(call, "StartAsyncExploreAgent: invalid JSON arguments.");
        }

        var started = await _session.CreateChildAsync(
                DysonAgentModes.Explore,
                task,
                context,
                initialTodos: null,
                modelSlug: null,
                reasoningEffort: null,
                contextFiles,
                cancellationToken)
            .ConfigureAwait(false);
        if (started.IsError)
            return Error(call, started.Error);

        var r = started.Value;
        return Ok(call, JsonSerializer.Serialize(new
        {
            agentId = r.SubagentId,
            persistenceId = r.PersistenceId,
        }));
    }

    private Task<DysonToolCallResult> ListMetaAgentDronesAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ok(call, _session.FormatChildRosterJson(includeReports: true)));
    }

    private Task<DysonToolCallResult> ReadMetaAgentDroneLogAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int agentId;
        int? maxLines;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "agentId");
            if (id is null or < 1)
                return Task.FromResult(Error(call, "ReadMetaAgentDroneLog: agentId (≥ 1) is required."));
            agentId = id.Value;
            maxLines = GetInt(doc.RootElement, "maxLines");
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, "ReadMetaAgentDroneLog: invalid JSON arguments."));
        }

        var inspected = _session.InspectSubagentLog(agentId, maxLines);
        return Task.FromResult(
            inspected.IsError ? Error(call, inspected.Error) : Ok(call, inspected.Value));
    }

    private async Task<DysonToolCallResult> StopMetaAgentDroneAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int agentId;
        string? reason;
        var discardWorktree = false;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "agentId");
            if (id is null or < 1)
                return Error(call, "StopMetaAgentDrone: agentId (≥ 1) is required.");
            agentId = id.Value;
            reason = GetOptionalString(doc.RootElement, "reason");
            discardWorktree = GetBool(doc.RootElement, "discardWorktree");
        }
        catch (JsonException)
        {
            return Error(call, "StopMetaAgentDrone: invalid JSON arguments.");
        }

        if (!_session.TryGetSubagent(agentId, out var child))
            return Error(call, $"Unknown agentId {agentId}.");

        var stopped = await _session.StopSubagentAsync(agentId, reason, cancellationToken)
            .ConfigureAwait(false);
        if (stopped.IsError)
            return Error(call, stopped.Error);

        await PersistSessionStatusAsync(child, child.Status, reason, cancellationToken)
            .ConfigureAwait(false);

        if (discardWorktree && !string.IsNullOrWhiteSpace(child.WorktreeAbsolutePath))
        {
            var removed = DysonSessionWorktree.Remove(WorkRoot, child.WorktreeAbsolutePath, force: true);
            if (removed.IsError)
                return Error(call, removed.Error);

            child.WorktreeAbsolutePath = null;
            child.WorktreeBranch = null;
            child.WorktreeEnabled = false;

            if (_store is not null && child.PersistenceId != Guid.Empty)
            {
                await _store.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = child.PersistenceId,
                        UpdateWorktreeEnabled = true,
                        WorktreeEnabled = false,
                        UpdateWorktreeLocation = true,
                        WorktreeAbsolutePath = null,
                        WorktreeBranch = null,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return Ok(call, stopped.Value);
    }

    private async Task<DysonToolCallResult> MessageMetaAgentDroneAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int agentId;
        string message;
        var interrupt = false;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "agentId");
            if (id is null or < 1)
                return Error(call, "MessageMetaAgentDrone: agentId (≥ 1) is required.");
            agentId = id.Value;

            var messageResult = RequireString(doc.RootElement, "message");
            if (messageResult.IsError)
                return Error(call, messageResult.Error);
            message = messageResult.Value;
            interrupt = GetBool(doc.RootElement, "interrupt");
        }
        catch (JsonException)
        {
            return Error(call, "MessageMetaAgentDrone: invalid JSON arguments.");
        }

        var triggered = await _session
            .TriggerSubagentEventAsync(agentId, message, interrupt, cancellationToken)
            .ConfigureAwait(false);
        return triggered.IsError ? Error(call, triggered.Error) : Ok(call, triggered.Value);
    }

    private async Task<DysonToolCallResult> DeleteMetaAgentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int agentId;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "agentId");
            if (id is null or < 1)
                return Error(call, "DeleteMetaAgent: agentId (≥ 1) is required.");
            agentId = id.Value;

            var reasonResult = RequireString(doc.RootElement, "reason");
            if (reasonResult.IsError)
                return Error(call, reasonResult.Error);
            reason = reasonResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "DeleteMetaAgent: invalid JSON arguments.");
        }

        if (!_session.TryGetSubagent(agentId, out var target))
            return Error(call, $"Agent #{agentId} is not a child of this session.");

        var tree = new List<DysonAgentSession>();
        CollectDescendantsThenSelf(target, tree);
        foreach (var node in tree)
        {
            if (!node.IsTerminal)
                return Error(call, $"Agent #{node.Id} is still {node.Status}. Stop it first.");
        }

        var worktreeBranch = target.WorktreeBranch;
        var deletedIds = tree.Select(n => n.Id).ToArray();

        if (_store is not null && target.PersistenceId != Guid.Empty)
        {
            var deleted = await _store.DeleteSessionAsync(target.PersistenceId, cancellationToken)
                .ConfigureAwait(false);
            if (deleted.IsError)
                return Error(call, deleted.Error);
        }

        _session.UnregisterSubagent(agentId);
        _session.AppendLog($"DeleteMetaAgent #{agentId}: {reason}");

        return Ok(call, JsonSerializer.Serialize(new
        {
            ok = true,
            deleted = deletedIds,
            worktreeBranch,
        }));
    }

    private static void CollectDescendantsThenSelf(DysonAgentSession node, List<DysonAgentSession> acc)
    {
        foreach (var child in node.SubSessions.ToArray())
            CollectDescendantsThenSelf(child, acc);
        acc.Add(node);
    }

    private DysonToolCallResult PostConversationMessage(DysonToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var messageResult = RequireString(doc.RootElement, "message");
            if (messageResult.IsError)
                return Error(call, messageResult.Error);

            var actions = ParseConversationActions(doc.RootElement);
            if (actions.IsError)
                return Error(call, actions.Error);

            var visualizationId = ParseVisualizationId(doc.RootElement);
            if (visualizationId.IsError)
                return Error(call, visualizationId.Error);

            if (visualizationId.Value is Guid id && FindSessionVisualization(id) is null)
                return Error(call, "PostConversationMessage: unknown visualizationId.");

            _session.AppendDisplayInfoTurn(
                messageResult.Value,
                actions.Value.Count == 0 ? null : actions.Value,
                visualizationId.Value);
        }
        catch (JsonException)
        {
            return Error(call, "PostConversationMessage: invalid JSON arguments.");
        }

        return Ok(call, """{"ok":true}""");
    }

    private const int MaxConversationActions = 8;

    private static Result<List<DysonConversationAction>, string> ParseConversationActions(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind == JsonValueKind.Null)
            return Result<List<DysonConversationAction>, string>.AsValue([]);

        if (actions.ValueKind != JsonValueKind.Array)
            return Result<List<DysonConversationAction>, string>.AsError(
                "PostConversationMessage: actions must be an array.");

        if (actions.GetArrayLength() > MaxConversationActions)
            return Result<List<DysonConversationAction>, string>.AsError(
                "PostConversationMessage: actions cannot exceed 8.");

        var list = new List<DysonConversationAction>();
        var index = 0;
        foreach (var item in actions.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                return Result<List<DysonConversationAction>, string>.AsError(
                    $"PostConversationMessage: actions[{index}] must be an object.");

            if (!item.TryGetProperty("name", out var nameEl)
                || nameEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(nameEl.GetString()))
            {
                return Result<List<DysonConversationAction>, string>.AsError(
                    $"PostConversationMessage: actions[{index}].name is required.");
            }

            if (!item.TryGetProperty("func", out var funcEl)
                || funcEl.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(funcEl.GetString()))
            {
                return Result<List<DysonConversationAction>, string>.AsError(
                    $"PostConversationMessage: actions[{index}].func is required.");
            }

            list.Add(new DysonConversationAction(nameEl.GetString()!.Trim(), funcEl.GetString()!.Trim()));
            index++;
        }

        return Result<List<DysonConversationAction>, string>.AsValue(list);
    }

    private static Result<Guid?, string> ParseVisualizationId(JsonElement root)
    {
        if (!root.TryGetProperty("visualizationId", out var prop) || prop.ValueKind == JsonValueKind.Null)
            return Result<Guid?, string>.AsValue(null);

        if (prop.ValueKind != JsonValueKind.String || !Guid.TryParse(prop.GetString(), out var id))
            return Result<Guid?, string>.AsError("PostConversationMessage: visualizationId must be a GUID.");

        return Result<Guid?, string>.AsValue(id);
    }

    private DysonHtmlVisualization? FindSessionVisualization(Guid id)
    {
        foreach (var turn in _session.Turns)
        {
            foreach (var tracked in turn.TrackedToolCalls)
            {
                if (tracked.Status != DysonToolCallStatus.Completed)
                    continue;
                if (tracked.Result is { IsError: false, HtmlVisualization: { } visualization }
                    && visualization.Id == id)
                {
                    return visualization;
                }
            }
        }

        return null;
    }

    private DysonToolCallResult CompactConversation(DysonToolCall call)
    {
        var turn = DysonFullSummarizeFlow.CreateTurn();
        _session.EnqueuePendingTurn(turn);
        return Ok(
            call,
            JsonSerializer.Serialize(new
            {
                ok = true,
                queued = true,
                nextTurnKind = DysonAgentTurnKind.FullSummarize.ToString(),
            }),
            endsCurrentTurn: true);
    }

    private async Task<DysonToolCallResult> RemoveTodosAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "RemoveTodos is only available in Meta Agent mode.");
        }

        IReadOnlyList<string> taskCodes;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var codes = TryParseOptionalStringArray(doc.RootElement, "taskCodes");
            if (codes.IsError)
                return Error(call, codes.Error);
            if (codes.Value is null || codes.Value.Count == 0)
                return Error(call, "RemoveTodos: taskCodes is required.");

            taskCodes = codes.Value;
            var reasonResult = RequireString(doc.RootElement, "reason");
            if (reasonResult.IsError)
                return Error(call, reasonResult.Error);
            reason = reasonResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "RemoveTodos: invalid JSON arguments.");
        }

        foreach (var code in taskCodes)
        {
            if (string.IsNullOrWhiteSpace(code))
                return Error(call, "RemoveTodos: taskCodes items must be non-empty.");

            var deleted = await _session.DeleteTodoAsync(code.Trim(), cancellationToken).ConfigureAwait(false);
            if (deleted.IsError)
                return Error(call, deleted.Error);
        }

        _session.AppendLog($"RemoveTodos ({reason}): {string.Join(", ", taskCodes)}");
        return Ok(call, JsonSerializer.Serialize(new
        {
            ok = true,
            removed = taskCodes,
            reason,
        }));
    }

    private async Task<DysonToolCallResult> ReadMetaPlanAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgentDrone, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "ReadMetaPlan is only available in Meta Agent Drone mode.");
        }

        long planId;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt64(doc.RootElement, "planId");
            if (id is null || id.Value <= 0)
                return Error(call, DysonMetaAgentTools.PlanIdMustBePositiveMessage);
            planId = id.Value;
        }
        catch (JsonException)
        {
            return Error(call, "ReadMetaPlan: invalid JSON arguments.");
        }

        if (_plans is null)
            return Error(call, "Plan repository is not available.");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to read a plan.");

        var loaded = await _plans.GetAsync(planId, _workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
            return Error(call, loaded.Error);

        var plan = loaded.Value;
        return Ok(call, JsonSerializer.Serialize(new
        {
            planId = plan.Id,
            title = plan.Title,
            markdown = plan.Markdown,
            status = plan.Status.ToString().ToLowerInvariant(),
        }));
    }

    private async Task<DysonToolCallResult> ListPlansAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "ListPlans is only available in Meta Agent mode.");
        }

        if (_plans is null)
            return Error(call, "Plan repository is not available.");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to list plans.");

        var listed = await _plans.ListAsync(_workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return Error(call, listed.Error);

        var payload = listed.Value.Select(p => new
        {
            planId = p.Id,
            title = p.Title,
            status = FormatPlanStatus(p.Status),
            buildAgentId = p.BuildAgentId,
            updatedUtc = p.UpdatedUtc,
        }).ToArray();
        return Ok(call, JsonSerializer.Serialize(payload));
    }

    private async Task<DysonToolCallResult> SetPlanStatusAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "SetPlanStatus is only available in Meta Agent mode.");
        }

        long planId;
        DysonPlanStatus status;
        string? note;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt64(doc.RootElement, "planId");
            if (id is null || id.Value <= 0)
                return Error(call, DysonMetaAgentTools.PlanIdMustBePositiveMessage);
            planId = id.Value;

            var statusResult = RequireString(doc.RootElement, "status");
            if (statusResult.IsError)
                return Error(call, statusResult.Error);
            var parsed = TryParseSettablePlanStatus(statusResult.Value);
            if (parsed.IsError)
                return Error(call, parsed.Error);
            status = parsed.Value;
            note = GetOptionalString(doc.RootElement, "note");
        }
        catch (JsonException)
        {
            return Error(call, "SetPlanStatus: invalid JSON arguments.");
        }

        if (_plans is null)
            return Error(call, "Plan repository is not available.");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to update a plan.");

        var updated = await _plans.UpdateAsync(
                planId,
                _workDirectoryId,
                status: status,
                note: note,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (updated.IsError)
            return Error(call, updated.Error);

        PublishPlansChanged(planId);
        _session.AppendLog($"SetPlanStatus #{planId} → {FormatPlanStatus(status)}");
        return Ok(call, JsonSerializer.Serialize(new
        {
            planId,
            status = FormatPlanStatus(status),
        }));
    }

    private async Task<DysonToolCallResult> BeginBuildPlanAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "BeginBuildPlan is only available in Meta Agent mode.");
        }

        long planId;
        string? extraInstructions;
        int? agentId;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt64(doc.RootElement, "planId");
            if (id is null || id.Value <= 0)
                return Error(call, DysonMetaAgentTools.PlanIdMustBePositiveMessage);
            planId = id.Value;
            extraInstructions = GetOptionalString(doc.RootElement, "extraInstructions");
            agentId = GetInt(doc.RootElement, "agentId");
            if (agentId is < 1)
                return Error(call, "BeginBuildPlan: agentId (≥ 1) is required when provided.");
        }
        catch (JsonException)
        {
            return Error(call, "BeginBuildPlan: invalid JSON arguments.");
        }

        if (_plans is null)
            return Error(call, "Plan repository is not available.");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to build a plan.");

        var loaded = await _plans.GetAsync(planId, _workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
            return Error(call, loaded.Error);

        var plan = loaded.Value;
        var liveBuilder = FindLiveBuilder(plan.BuildAgentId);
        if (liveBuilder is not null
            && (agentId is null || agentId.Value != liveBuilder.Id))
        {
            return Error(
                call,
                $"Plan {planId} is already being built by agent #{liveBuilder.Id}. " +
                "Pass that agentId to extend the build, or SetPlanStatus stale first.");
        }

        var brief = DysonMetaBuildBrief.Build(plan.Id, plan.Title, extraInstructions);
        int runtimeId;
        Guid persistenceId;
        if (agentId is int reuseId)
        {
            if (!_session.TryGetSubagent(reuseId, out var existing))
                return Error(call, $"Unknown agentId {reuseId}.");

            var triggered = await _session
                .TriggerSubagentEventAsync(reuseId, brief, interruptSubagent: false, cancellationToken)
                .ConfigureAwait(false);
            if (triggered.IsError)
                return Error(call, triggered.Error);

            runtimeId = existing.Id;
            persistenceId = existing.PersistenceId;
        }
        else
        {
            var started = await CreateMetaAgentDroneChildAsync(
                    brief,
                    context: null,
                    initialTodos: null,
                    modelSlug: null,
                    reasoningEffort: null,
                    useWorktree: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (started.IsError)
                return Error(call, started.Error);

            var r = started.Value;
            runtimeId = r.SubagentId;
            persistenceId = r.PersistenceId;
        }

        var updated = await _plans.UpdateAsync(
                planId,
                _workDirectoryId,
                status: DysonPlanStatus.Building,
                buildAgentId: persistenceId == Guid.Empty ? null : persistenceId,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (updated.IsError)
            return Error(call, updated.Error);

        PublishPlansChanged(planId);
        return Ok(call, JsonSerializer.Serialize(new
        {
            planId,
            status = FormatPlanStatus(DysonPlanStatus.Building),
            agentId = runtimeId,
            persistenceId,
        }));
    }

    private async Task<DysonToolCallResult> DeletePlanAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "DeletePlan is only available in Meta Agent mode.");
        }

        long planId;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt64(doc.RootElement, "planId");
            if (id is null || id.Value <= 0)
                return Error(call, DysonMetaAgentTools.PlanIdMustBePositiveMessage);
            planId = id.Value;

            var reasonResult = RequireString(doc.RootElement, "reason");
            if (reasonResult.IsError)
                return Error(call, reasonResult.Error);
            reason = reasonResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "DeletePlan: invalid JSON arguments.");
        }

        if (_plans is null)
            return Error(call, "Plan repository is not available.");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to delete a plan.");

        var loaded = await _plans.GetAsync(planId, _workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
            return Error(call, loaded.Error);

        if (loaded.Value.Status == DysonPlanStatus.Building)
        {
            return Error(
                call,
                $"DeletePlan: plan {planId} is Building. Call SetPlanStatus with stale, or stop the builder first.");
        }

        var deleted = await _plans.DeleteAsync(planId, _workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (deleted.IsError)
            return Error(call, deleted.Error);

        PublishPlansChanged(planId);
        _session.AppendLog($"DeletePlan #{planId}: {reason}");
        return Ok(call, JsonSerializer.Serialize(new
        {
            ok = true,
            planId,
        }));
    }

    private async Task<DysonToolCallResult> SubmitMetaPlanAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(_session.Mode, DysonAgentModes.MetaAgentDrone, StringComparison.OrdinalIgnoreCase))
        {
            return Error(
                call,
                "SubmitMetaPlan is only available in Meta Agent Drone mode.");
        }

        string title;
        string markdown;
        long? revisePlanId;
        string? summary;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var titleResult = RequireString(doc.RootElement, "title");
            if (titleResult.IsError)
                return Error(call, titleResult.Error);
            title = titleResult.Value;

            var markdownResult = RequireString(doc.RootElement, "markdown");
            if (markdownResult.IsError)
                return Error(call, markdownResult.Error);
            markdown = markdownResult.Value;

            if (doc.RootElement.TryGetProperty("planId", out _))
            {
                var id = GetInt64(doc.RootElement, "planId");
                if (id is null || id.Value <= 0)
                    return Error(call, DysonMetaAgentTools.PlanIdMustBePositiveMessage);
                revisePlanId = id.Value;
            }
            else
            {
                revisePlanId = null;
            }

            summary = GetOptionalString(doc.RootElement, "summary");
        }
        catch (JsonException)
        {
            return Error(call, "SubmitMetaPlan: invalid JSON arguments.");
        }

        if (_plans is null)
            return Error(call, "Plan repository is not available.");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to submit a plan.");

        long planId;
        DysonPlanStatus status;
        if (revisePlanId is long existingId)
        {
            var loaded = await _plans.GetAsync(existingId, _workDirectoryId, cancellationToken)
                .ConfigureAwait(false);
            if (loaded.IsError)
                return Error(call, loaded.Error);

            var updated = await _plans.UpdateAsync(
                    existingId,
                    _workDirectoryId,
                    title: title,
                    markdown: markdown,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (updated.IsError)
                return Error(call, updated.Error);

            planId = existingId;
            status = loaded.Value.Status;
        }
        else
        {
            var created = await _plans.CreateAsync(
                    _workDirectoryId,
                    DysonPlanKind.MetaPlan,
                    title,
                    markdown,
                    planRelativePath: null,
                    DysonPlanStatus.Draft,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return Error(call, created.Error);

            planId = created.Value;
            status = DysonPlanStatus.Draft;
        }

        PublishPlansChanged(planId);
        var log = summary is null
            ? $"SubmitMetaPlan #{planId} ({title})"
            : $"SubmitMetaPlan #{planId} ({title}): {summary}";
        _session.AppendLog(log);

        return Ok(call, JsonSerializer.Serialize(new
        {
            planId,
            title,
            status = FormatPlanStatus(status),
        }));
    }

    private void PublishPlansChanged(long planId)
    {
        var bus = _session.Config.Bus;
        if (bus is null || _workDirectoryId == Guid.Empty)
            return;

        bus.Publish(
            DysonBusScopes.WorkDirectory(_workDirectoryId),
            new DysonPlansChangedEvent(_workDirectoryId, planId));
    }

    private async Task<Result<DysonStartSubagentResult, string>> CreateMetaAgentDroneChildAsync(
        string task,
        string? context,
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos,
        string? modelSlug,
        string? reasoningEffort,
        bool useWorktree,
        CancellationToken cancellationToken,
        string? existingWorktreePath = null)
    {
        // Explicit per call. AsyncLocal so both session implementations share one spawn path.
        DysonAgentSession.MetaAgentDroneUseWorktree.Value = useWorktree;
        DysonAgentSession.MetaAgentDroneExistingWorktreePath.Value =
            !useWorktree && !string.IsNullOrWhiteSpace(existingWorktreePath)
                ? existingWorktreePath
                : null;
        try
        {
            return await _session.CreateChildAsync(
                    DysonAgentModes.MetaAgentDrone,
                    task,
                    context,
                    initialTodos,
                    modelSlug,
                    reasoningEffort,
                    contextFiles: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            DysonAgentSession.MetaAgentDroneUseWorktree.Value = null;
            DysonAgentSession.MetaAgentDroneExistingWorktreePath.Value = null;
        }
    }

    private Result<string, string> RequireListedWorktree(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(
                "CreateAsyncMetaAgentDrone: existingWorktreePath is invalid: " + ex.Message);
        }

        var anchor = _session.RegisteredWorkDirectoryAbsolutePath;
        if (string.IsNullOrWhiteSpace(anchor))
            anchor = WorkRoot;

        var listed = DysonGitInfo.TryListWorktrees(anchor);
        if (listed.IsError)
            return Result<string, string>.AsError("CreateAsyncMetaAgentDrone: " + listed.Error);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var entry in listed.Value)
        {
            string entryPath;
            try
            {
                entryPath = Path.GetFullPath(entry.Path);
            }
            catch
            {
                continue;
            }

            if (string.Equals(entryPath, full, comparison))
                return Result<string, string>.AsValue(full);
        }

        return Result<string, string>.AsError(
            "CreateAsyncMetaAgentDrone: existingWorktreePath is not an existing worktree.");
    }

    private DysonAgentSession? FindLiveBuilder(Guid? buildAgentId)
    {
        if (buildAgentId is not Guid id || id == Guid.Empty)
            return null;

        foreach (var child in _session.SubSessions)
        {
            if (child.PersistenceId == id && !child.IsTerminal)
                return child;
        }

        return null;
    }

    private static Result<DysonPlanStatus, string> TryParseSettablePlanStatus(string raw)
    {
        if (string.Equals(raw, "building", StringComparison.OrdinalIgnoreCase))
            return Result<DysonPlanStatus, string>.AsValue(DysonPlanStatus.Building);
        if (string.Equals(raw, "completed", StringComparison.OrdinalIgnoreCase))
            return Result<DysonPlanStatus, string>.AsValue(DysonPlanStatus.Completed);
        if (string.Equals(raw, "stale", StringComparison.OrdinalIgnoreCase))
            return Result<DysonPlanStatus, string>.AsValue(DysonPlanStatus.Stale);

        return Result<DysonPlanStatus, string>.AsError(DysonMetaAgentTools.SetPlanStatusAcceptedMessage);
    }

    private static string FormatPlanStatus(DysonPlanStatus status) =>
        status.ToString().ToLowerInvariant();

    private DysonToolCallResult? RejectUnlessMetaAgent(DysonToolCall call, string tool)
    {
        if (string.Equals(_session.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase))
            return null;

        return Error(call, $"{tool} is only available in Meta Agent mode.");
    }

    // ponytail: per-note WriteFile gate does not serialize the 20-note cap across different names; upgrade = one gate for the scratch directory.
    private async Task<DysonToolCallResult> WithNoteGateAsync(
        DysonToolCall call,
        string relative,
        Func<CancellationToken, Task<DysonToolCallResult>> body,
        CancellationToken cancellationToken)
    {
        var native = _fs.ResolvePath(relative);
        if (native.IsError)
            return Error(call, DysonScratchNotes.RejectedNameMessage);

        var gate = WriteFileGates.GetOrAdd(native.Value, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await body(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<DysonToolCallResult> ListNotesAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "ListNotes");
        if (denied is not null)
            return denied;

        var check = await DysonScratchNotes.CheckWriteAsync(
            _fs,
            new DysonTiktokenTokenCounter(),
            leafName: null,
            resultingText: null,
            DysonScratchNotes.Kind.Probe,
            cancellationToken).ConfigureAwait(false);
        if (check.IsError)
            return Error(call, check.Error);

        var notes = check.Value.Notes.Select(static n => new { name = n.Name, tokens = n.Tokens }).ToArray();
        return Ok(call, JsonSerializer.Serialize(new { notes }));
    }

    private async Task<DysonToolCallResult> CanCreateNoteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "CanCreateNote");
        if (denied is not null)
            return denied;

        string? name;
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var nameResult = OptionalStringAllowEmpty(doc.RootElement, "name");
            if (nameResult.IsError)
                return Error(call, nameResult.Error);
            var contentResult = OptionalStringAllowEmpty(doc.RootElement, "content");
            if (contentResult.IsError)
                return Error(call, contentResult.Error);
            name = nameResult.Value;
            content = contentResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "CanCreateNote: invalid JSON arguments.");
        }

        var check = await DysonScratchNotes.CheckWriteAsync(
            _fs,
            new DysonTiktokenTokenCounter(),
            string.IsNullOrWhiteSpace(name) ? null : name,
            string.IsNullOrWhiteSpace(name) ? null : content,
            DysonScratchNotes.Kind.Probe,
            cancellationToken).ConfigureAwait(false);
        if (check.IsError)
            return Error(call, check.Error);

        var value = check.Value;
        return Ok(call, JsonSerializer.Serialize(new
        {
            allowed = value.Allowed,
            reason = value.Reason,
            noteCount = value.NoteCount,
            totalTokens = value.TotalTokens,
            noteTokens = value.NoteTokens,
            maxNotes = DysonScratchNotes.MaxNotes,
            maxTokensPerNote = DysonScratchNotes.MaxTokensPerNote,
            maxTokensTotal = DysonScratchNotes.MaxTokensTotal,
        }));
    }

    private async Task<DysonToolCallResult> CreateNoteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "CreateNote");
        if (denied is not null)
            return denied;

        string name;
        string content;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var nameResult = RequireString(doc.RootElement, "name");
            if (nameResult.IsError)
                return Error(call, nameResult.Error);
            var contentResult = RequireStringAllowEmpty(doc.RootElement, "content");
            if (contentResult.IsError)
                return Error(call, contentResult.Error);
            name = nameResult.Value;
            content = contentResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "CreateNote: invalid JSON arguments.");
        }

        var leaf = DysonScratchNotes.ResolveLeaf(_fs, name);
        if (leaf.IsError)
            return Error(call, leaf.Error);

        return await WithNoteGateAsync(call, leaf.Value, async ct =>
        {
            var check = await DysonScratchNotes.CheckWriteAsync(
                _fs,
                new DysonTiktokenTokenCounter(),
                name,
                content,
                DysonScratchNotes.Kind.Create,
                ct).ConfigureAwait(false);
            if (check.IsError)
                return Error(call, check.Error);
            if (!check.Value.Allowed)
                return Error(call, check.Value.Reason ?? DysonScratchNotes.CountReason);

            var written = await _fs.WriteAllTextAsync(leaf.Value, content, ct).ConfigureAwait(false);
            if (written.IsError)
                return Error(call, DysonScratchNotes.HideStorageFailure(written.Error));

            return Ok(call, JsonSerializer.Serialize(new
            {
                name,
                tokens = check.Value.NoteTokens ?? 0,
            }));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> UpdateNoteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "UpdateNote");
        if (denied is not null)
            return denied;

        string noteName;
        var contentOnly = false;
        string fullContent = "";
        var edits = new List<(string Old, string New, bool ReplaceAll)>();
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var path = RequireString(root, "path");
            if (path.IsError)
                return Error(call, path.Error);
            noteName = path.Value;

            var hasOld = root.TryGetProperty("old_text", out _);
            var hasEdits = root.TryGetProperty("edits", out var editsProp)
                && editsProp.ValueKind == JsonValueKind.Array;
            var hasContent = root.TryGetProperty("content", out var contentProp)
                && contentProp.ValueKind == JsonValueKind.String;
            if (hasContent && !hasOld && !hasEdits)
            {
                contentOnly = true;
                fullContent = contentProp.GetString() ?? "";
            }
            else
            {
                var defaultReplaceAll = GetBool(root, "replace_all");
                if (root.TryGetProperty("old_text", out var oldProp)
                    && root.TryGetProperty("new_text", out var newProp))
                {
                    edits.Add((oldProp.GetString() ?? "", newProp.GetString() ?? "", defaultReplaceAll));
                }

                if (hasEdits)
                {
                    foreach (var edit in editsProp.EnumerateArray())
                    {
                        if (!edit.TryGetProperty("old_text", out var o) || !edit.TryGetProperty("new_text", out var n))
                            continue;
                        var itemReplaceAll = edit.TryGetProperty("replace_all", out var ra)
                            ? ra.ValueKind == JsonValueKind.True
                            : defaultReplaceAll;
                        edits.Add((o.GetString() ?? "", n.GetString() ?? "", itemReplaceAll));
                    }
                }
            }
        }
        catch (JsonException)
        {
            return Error(call, "UpdateNote: invalid JSON arguments.");
        }

        var leaf = DysonScratchNotes.ResolveLeaf(_fs, noteName);
        if (leaf.IsError)
            return Error(call, leaf.Error);

        return await WithNoteGateAsync(call, leaf.Value, async ct =>
        {
            var exists = await _fs.FileExistsAsync(leaf.Value, ct).ConfigureAwait(false);
            if (exists.IsError)
                return Error(call, DysonScratchNotes.HideStorageFailure(exists.Error));
            if (!exists.Value)
                return Error(call, DysonScratchNotes.MissingReason);

            string newText;
            if (contentOnly)
            {
                newText = fullContent;
            }
            else
            {
                if (edits.Count == 0)
                    return Error(call, "UpdateNote: provide content, or old_text/new_text, or edits[].");

                var read = await _fs.ReadAllTextAsync(leaf.Value, ct).ConfigureAwait(false);
                if (read.IsError)
                    return Error(call, DysonScratchNotes.HideStorageFailure(read.Error));

                newText = read.Value;
                foreach (var (oldText, replacement, replaceAll) in edits)
                {
                    if (string.IsNullOrEmpty(oldText))
                        return Error(call, "UpdateNote: old_text must be non-empty.");

                    var replaced = DysonTextEditApplier.TryReplace(newText, oldText, replacement, replaceAll);
                    if (replaced.IsError)
                        return Error(call, "UpdateNote: " + replaced.Error.Message);

                    newText = replaced.Value.Content;
                }
            }

            var check = await DysonScratchNotes.CheckWriteAsync(
                _fs,
                new DysonTiktokenTokenCounter(),
                noteName,
                newText,
                DysonScratchNotes.Kind.Update,
                ct).ConfigureAwait(false);
            if (check.IsError)
                return Error(call, check.Error);
            if (!check.Value.Allowed)
                return Error(call, check.Value.Reason ?? DysonScratchNotes.PerNoteReason);

            var written = await _fs.WriteAllTextAsync(leaf.Value, newText, ct).ConfigureAwait(false);
            if (written.IsError)
                return Error(call, DysonScratchNotes.HideStorageFailure(written.Error));

            return Ok(call, JsonSerializer.Serialize(new
            {
                name = noteName,
                tokens = check.Value.NoteTokens ?? 0,
            }));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> DeleteNoteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "DeleteNote");
        if (denied is not null)
            return denied;

        string name;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var nameResult = RequireString(doc.RootElement, "name");
            if (nameResult.IsError)
                return Error(call, nameResult.Error);
            name = nameResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "DeleteNote: invalid JSON arguments.");
        }

        var leaf = DysonScratchNotes.ResolveLeaf(_fs, name);
        if (leaf.IsError)
            return Error(call, leaf.Error);

        return await WithNoteGateAsync(call, leaf.Value, async ct =>
        {
            var exists = await _fs.FileExistsAsync(leaf.Value, ct).ConfigureAwait(false);
            if (exists.IsError)
                return Error(call, DysonScratchNotes.HideStorageFailure(exists.Error));
            if (!exists.Value)
                return Error(call, DysonScratchNotes.MissingReason);

            var deleted = await _fs.DeleteFileAsync(leaf.Value, ct).ConfigureAwait(false);
            if (deleted.IsError)
                return Error(call, DysonScratchNotes.HideStorageFailure(deleted.Error));

            return Ok(call, JsonSerializer.Serialize(new { name, deleted = true }));
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Result<string, string> RequireStringAllowEmpty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return Result<string, string>.AsError($"Missing required string field '{name}'.");

        return Result<string, string>.AsValue(prop.GetString() ?? "");
    }

    private static Result<string?, string> OptionalStringAllowEmpty(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return Result<string?, string>.AsValue(null);
        if (prop.ValueKind != JsonValueKind.String)
            return Result<string?, string>.AsError($"Field '{name}' must be a string.");

        return Result<string?, string>.AsValue(prop.GetString() ?? "");
    }
}
