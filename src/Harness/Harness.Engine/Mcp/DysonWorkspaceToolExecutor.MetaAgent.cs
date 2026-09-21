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
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var taskResult = RequireString(root, "task");
            if (taskResult.IsError)
                return Error(call, taskResult.Error);
            task = taskResult.Value;
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
                cancellationToken)
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
        string message;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var messageResult = RequireString(doc.RootElement, "message");
            if (messageResult.IsError)
                return Error(call, messageResult.Error);
            message = messageResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "PostConversationMessage: invalid JSON arguments.");
        }

        _session.AppendDisplayInfoTurn(message);
        return Ok(call, """{"ok":true}""");
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

    private Task<Result<DysonStartSubagentResult, string>> CreateMetaAgentDroneChildAsync(
        string task,
        string? context,
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos,
        string? modelSlug,
        string? reasoningEffort,
        CancellationToken cancellationToken) =>
        _session.CreateChildAsync(
            DysonAgentModes.MetaAgentDrone,
            task,
            context,
            initialTodos,
            modelSlug,
            reasoningEffort,
            contextFiles: null,
            cancellationToken);

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
}
