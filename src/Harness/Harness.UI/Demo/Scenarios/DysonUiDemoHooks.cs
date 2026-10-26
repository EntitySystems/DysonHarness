#if DYSON_PREVIEW
using DysonHarness;
using Microsoft.JSInterop;

namespace Harness.UI.Demo;

public sealed class DysonUiDemoHooks : IDysonDemoHooks
{
    private const int FocusPollMs = 100;
    private const int FocusPollAttempts = 100;

    private readonly DysonUiHost _host;
    private readonly IDysonWorkDirectoryRepository _workDirectories;
    private readonly IDysonSessionRepository _sessions;
    private readonly IDysonPlanRepository _plans;
    private readonly DysonMessageBus _bus;
    private readonly IJSRuntime _js;
    private readonly IDysonModelRepository _models;

    private Guid _workDirectoryId;
    private Guid _sessionId;
    private Guid? _previousWorkDirectoryId;
    private string? _tempPath;
    private readonly List<long> _planIds = [];
    private DysonAgentSession? _scriptSession;

    public DysonUiDemoHooks(
        DysonUiHost host,
        IDysonWorkDirectoryRepository workDirectories,
        IDysonSessionRepository sessions,
        IDysonPlanRepository plans,
        DysonMessageBus bus,
        IJSRuntime js,
        IDysonModelRepository models)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(workDirectories);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(models);
        _host = host;
        _workDirectories = workDirectories;
        _sessions = sessions;
        _plans = plans;
        _bus = bus;
        _js = js;
        _models = models;
    }

    public async Task<Result<DysonDemoStart, string>> StartAsync(string workDirectoryName, CancellationToken ct)
    {
        _previousWorkDirectoryId = _host.ActiveWorkDirectoryId ?? _host.Focus?.ActiveId;

        var tempPath = await Task.Run(() =>
        {
            var path = Path.Combine(Path.GetTempPath(), "dyson-demo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }, ct).ConfigureAwait(true);
        _tempPath = tempPath;

        var createdDir = await _workDirectories.CreateAsync(tempPath, workDirectoryName, ct).ConfigureAwait(true);
        if (createdDir.IsError)
        {
            await DeleteTempAsync(tempPath).ConfigureAwait(true);
            _tempPath = null;
            return Result<DysonDemoStart, string>.AsError(createdDir.Error);
        }

        var id = createdDir.Value;
        _workDirectoryId = id;

        var touched = await _workDirectories.TouchOpenedAsync(id, ct).ConfigureAwait(true);
        if (touched.IsError)
            return Result<DysonDemoStart, string>.AsError(touched.Error);

        _host.Focus?.SetActive(id);
        await DysonCircuitJs.InvokeVoidAsync(_js, "dysonWorkdir.set", id.ToString()).ConfigureAwait(true);
        // Same circuit. A force-load would drop this scoped host and the scripted session with it.
        // Do not CreateAsync here: MetaAgent.EnsureMetaSessionAsync opens the one session this page shows.
        await DysonCircuitJs.NavigateAsync(_js, $"/meta/{id}").ConfigureAwait(true);

        for (var i = 0; i < FocusPollAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(FocusPollMs, ct).ConfigureAwait(true);
            if (_host.Session is DemoDysonAgentSession focused
                && string.Equals(focused.Mode, DysonAgentModes.MetaAgent, StringComparison.OrdinalIgnoreCase)
                && focused.WorkDirectoryId == id
                && focused.PersistenceId != Guid.Empty)
            {
                _scriptSession = focused;
                _sessionId = focused.PersistenceId;
                break;
            }
        }

        if (_scriptSession is null || _sessionId == Guid.Empty)
            return Result<DysonDemoStart, string>.AsError("Meta Agent page did not open the sandbox session.");

        return Result<DysonDemoStart, string>.AsValue(
            new DysonDemoStart(id, _sessionId, _previousWorkDirectoryId));
    }

    public Task<VoidResult<string>> AppendUserTurnAsync(string instruction, string assistantText, CancellationToken ct)
    {
        var session = ResolveScriptSession();
        if (session is null)
            return Task.FromResult(VoidResult<string>.AsError("Demo session is not running."));

        session.AppendScriptedExchange(instruction, assistantText);
        return Task.FromResult(VoidResult<string>.Success);
    }

    public Task<Result<Guid?, string>> AppendAssistantMessageAsync(
        string message,
        DysonDemoQuestion? question,
        CancellationToken ct)
    {
        var session = ResolveScriptSession();
        if (session is null)
            return Task.FromResult(Result<Guid?, string>.AsError("Demo session is not running."));

        if (question is null)
        {
            session.AppendDisplayInfoTurn(message);
            return Task.FromResult(Result<Guid?, string>.AsValue((Guid?)null));
        }

        var q = new DysonUserQuestion(
            Guid.NewGuid(),
            question.Question,
            question.Choices,
            MultiSelect: false,
            AllowCustomAnswer: false,
            Answer: null);
        session.AppendDisplayInfoTurn(message, userQuestion: q);
        return Task.FromResult(Result<Guid?, string>.AsValue((Guid?)q.Id));
    }

    public Task<VoidResult<string>> AnswerQuestionAsync(Guid questionId, string selectedChoice, CancellationToken ct)
    {
        var session = ResolveScriptSession();
        if (session is null)
            return Task.FromResult(VoidResult<string>.AsError("Demo session is not running."));

        var recorded = session.TryRecordUserQuestionAnswer(questionId, [selectedChoice], customText: null);
        return Task.FromResult(
            recorded.IsError ? VoidResult<string>.AsError(recorded.Error) : VoidResult<string>.Success);
    }

    public async Task<Result<long, string>> CreatePlanAsync(string title, string markdown, CancellationToken ct)
    {
        if (_workDirectoryId == Guid.Empty)
            return Result<long, string>.AsError("Demo work directory is not running.");

        var created = await _plans.CreateAsync(
            _workDirectoryId,
            DysonPlanKind.MetaPlan,
            title,
            markdown,
            planRelativePath: null,
            DysonPlanStatus.Draft,
            cancellationToken: ct).ConfigureAwait(true);
        if (created.IsError)
            return created;

        _planIds.Add(created.Value);
        _bus.Publish(DysonBusScopes.WorkDirectory(_workDirectoryId), new DysonPlansChangedEvent(_workDirectoryId, created.Value));
        return created;
    }

    public Task<VoidResult<string>> OpenPlanAsync(long planId, CancellationToken ct)
    {
        if (_workDirectoryId == Guid.Empty)
            return Task.FromResult(VoidResult<string>.AsError("Demo work directory is not running."));

        return _host.OpenMetaPlanAsync(planId, _workDirectoryId, ct);
    }

    public async Task<VoidResult<string>> SetPlanStatusAsync(long planId, DysonPlanStatus status, CancellationToken ct)
    {
        if (_workDirectoryId == Guid.Empty)
            return VoidResult<string>.AsError("Demo work directory is not running.");

        var updated = await _plans.UpdateAsync(
            planId,
            _workDirectoryId,
            status: status,
            cancellationToken: ct).ConfigureAwait(true);
        if (updated.IsError)
            return updated;

        _bus.Publish(DysonBusScopes.WorkDirectory(_workDirectoryId), new DysonPlansChangedEvent(_workDirectoryId, planId));
        return VoidResult<string>.Success;
    }

    public async Task<Result<DysonDemoAgent, string>> AddAgentAsync(
        Guid? parentPersistenceId,
        string title,
        string mode,
        CancellationToken ct)
    {
        var parent = ResolveParent(parentPersistenceId);
        if (parent is null)
            return Result<DysonDemoAgent, string>.AsError("Demo parent session was not found.");

        // CreateChildAsync kicks a local demo echo. Cancel that run so it cannot mark the
        // child Completed before the script does. The child stays Active (Stopped would lock it).
        // Title is assigned after the spawn event, so match the demo type, not DisplayTitle.
        void Quiet(object? _, DysonAgentSession child)
        {
            if (child is not DemoDysonAgentSession demo)
                return;
            parent.SubagentSpawned -= Quiet;
            demo.CancelDemoBackgroundRun();
        }

        parent.SubagentSpawned += Quiet;
        Result<DysonStartSubagentResult, string> created;
        try
        {
            created = await parent.CreateChildAsync(mode, task: title, context: null, cancellationToken: ct)
                .ConfigureAwait(true);
        }
        finally
        {
            parent.SubagentSpawned -= Quiet;
        }
        if (created.IsError)
            return Result<DysonDemoAgent, string>.AsError(created.Error);

        return Result<DysonDemoAgent, string>.AsValue(
            new DysonDemoAgent(created.Value.PersistenceId, created.Value.SubagentId));
    }

    public Task<VoidResult<string>> SetAgentStatusAsync(
        Guid persistenceId,
        DysonSessionStatus status,
        string? activity,
        CancellationToken ct)
    {
        if (status != DysonSessionStatus.Completed)
            return Task.FromResult(VoidResult<string>.AsError($"Demo status '{status}' is not supported."));

        var child = FindAgent(persistenceId);
        if (child is null)
            return Task.FromResult(VoidResult<string>.AsError("Demo agent was not found."));

        if (child.Status != DysonSessionStatus.Completed)
            child.TryMarkTerminal(DysonSessionStatus.Completed, activity);

        if (!string.IsNullOrEmpty(activity))
        {
            var turn = child.AppendScriptedExchange(activity, activity);
            turn.AgentTitle = activity;
        }

        return Task.FromResult(VoidResult<string>.Success);
    }

    public async Task<VoidResult<string>> CleanupAsync(CancellationToken ct)
    {
        var sessionId = _sessionId;
        var workDir = _workDirectoryId;
        var tempPath = _tempPath;
        var previous = _previousWorkDirectoryId;
        var planIds = _planIds.ToArray();

        _sessionId = Guid.Empty;
        _workDirectoryId = Guid.Empty;
        _previousWorkDirectoryId = null;
        _tempPath = null;
        _planIds.Clear();
        _scriptSession = null;

        if (sessionId == Guid.Empty && workDir == Guid.Empty && tempPath is null)
            return VoidResult<string>.Success;

        string? firstError = null;

        if (sessionId != Guid.Empty)
        {
            var deleted = await _sessions.DeleteSessionAsync(sessionId, ct).ConfigureAwait(true);
            if (deleted.IsError && !IsNotFound(deleted.Error))
                firstError ??= deleted.Error;
        }

        if (workDir != Guid.Empty)
        {
            if (planIds.Length == 0)
            {
                var listed = await _plans.ListAsync(workDir, includeArchived: true, ct).ConfigureAwait(true);
                if (listed.IsError)
                    firstError ??= listed.Error;
                else
                    planIds = listed.Value.Select(plan => plan.Id).ToArray();
            }

            foreach (var planId in planIds)
            {
                var deleted = await _plans.DeleteAsync(planId, workDir, ct).ConfigureAwait(true);
                if (deleted.IsError && !IsNotFound(deleted.Error))
                    firstError ??= deleted.Error;
            }

            var removed = await _workDirectories.DeleteAsync(workDir, ct).ConfigureAwait(true);
            if (removed.IsError && !IsNotFound(removed.Error))
                firstError ??= removed.Error;
        }

        if (tempPath is not null)
            await DeleteTempAsync(tempPath).ConfigureAwait(true);

        if (previous is Guid previousId && previousId != Guid.Empty)
        {
            _host.Focus?.SetActive(previousId);
            await DysonCircuitJs.InvokeVoidAsync(_js, "dysonWorkdir.set", previousId.ToString()).ConfigureAwait(true);
            await DysonCircuitJs.NavigateAsync(_js, $"/meta/{previousId}").ConfigureAwait(true);
        }
        else
        {
            _host.Focus?.SetActive(null);
            await DysonCircuitJs.InvokeVoidAsync(_js, "dysonWorkdir.set", (string?)null).ConfigureAwait(true);
            await DysonCircuitJs.NavigateAsync(_js, "/").ConfigureAwait(true);
        }

        return firstError is null ? VoidResult<string>.Success : VoidResult<string>.AsError(firstError);
    }

    private DysonAgentSession? ResolveScriptSession()
    {
        if (_sessionId == Guid.Empty)
            return null;
        if (_host.Session is { } focused && focused.PersistenceId == _sessionId)
            return focused;
        return _scriptSession;
    }

    private DysonAgentSession? ResolveParent(Guid? parentPersistenceId)
    {
        var root = ResolveScriptSession();
        if (root is null)
            return null;
        if (parentPersistenceId is null || parentPersistenceId == _sessionId || parentPersistenceId == Guid.Empty)
            return root;
        return FindAgent(parentPersistenceId.Value);
    }

    private DysonAgentSession? FindAgent(Guid persistenceId)
    {
        var root = ResolveScriptSession();
        if (root is null)
            return null;
        if (root.PersistenceId == persistenceId)
            return root;
        foreach (var child in root.SubSessions)
        {
            if (child.PersistenceId == persistenceId)
                return child;
            foreach (var nested in child.SubSessions)
            {
                if (nested.PersistenceId == persistenceId)
                    return nested;
            }
        }

        return null;
    }

    private static bool IsNotFound(string? error) =>
        error is not null && error.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static Task DeleteTempAsync(string path) =>
        Task.Run(() =>
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        });
}
#endif
