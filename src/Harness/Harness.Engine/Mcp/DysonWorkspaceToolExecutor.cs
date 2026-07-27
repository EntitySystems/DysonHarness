using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Executes workspace-scoped MCP tools against a work directory root, plus RenameSession,
/// GetDateTime, WaitForSeconds, ShellExecute, long-running shell tools, subagent spawn/list/report tools,
/// inter-agent events / AskQuestion, session todo CRUD, task completion tools, ResumeCurrentTask,
/// ExpandThoughtProcess / StartNewTurn / DropTurnContext / RestoreTurnContext, in-process web search/fetch tools, and browser
/// control tools (when <see cref="DysonAgentSessionConfig.BrowserControl"/> is set).
/// Other catalog tools return a not-implemented stub result.
/// </summary>
public sealed partial class DysonWorkspaceToolExecutor
{
    private readonly DysonAgentSession _session;
    private readonly string _workRoot;
    private readonly Guid _workDirectoryId;
    private readonly HttpClient _http;
    private readonly DysonSessionStore? _store;

    public DysonWorkspaceToolExecutor(
        DysonAgentSession session,
        string workDirectoryAbsolutePath,
        HttpClient http,
        DysonSessionStore? store = null,
        Guid workDirectoryId = default)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workRoot = Path.GetFullPath(workDirectoryAbsolutePath);
        _workDirectoryId = workDirectoryId;
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store;
    }

    public async Task<DysonToolCallResult> ExecuteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!_session.McpPipeline.Tools.ContainsKey(call.ToolName))
        {
            return Error(
                call,
                $"Tool '{call.ToolName}' is not available in this session's catalog " +
                "(disabled by agent-mode policy or structural gate).");
        }

        try
        {
            return call.ToolName switch
            {
                "RenameSession" => await RenameSessionAsync(call, cancellationToken).ConfigureAwait(false),
                "GetDateTime" => await GetDateTimeAsync(call, cancellationToken).ConfigureAwait(false),
                "SubmitPlan" => await SubmitPlanAsync(call, cancellationToken).ConfigureAwait(false),
                "StartSubagent" => await StartSubagentAsync(call, cancellationToken).ConfigureAwait(false),
                "ListSubagents" => await ListSubagentsAsync(call, cancellationToken).ConfigureAwait(false),
                "WaitForSubagent" => await WaitForSubagentAsync(call, cancellationToken).ConfigureAwait(false),
                "InspectSubagentLog" => await InspectSubagentLogAsync(call, cancellationToken).ConfigureAwait(false),
                "StopSubagent" => await StopSubagentAsync(call, cancellationToken).ConfigureAwait(false),
                "SubmitSubagentReport" => await SubmitSubagentReportAsync(call, cancellationToken).ConfigureAwait(false),
                "AskQuestion" => await AskQuestionAsync(call, cancellationToken).ConfigureAwait(false),
                "AskQuestionFromParent" => await AskQuestionFromParentAsync(call, cancellationToken).ConfigureAwait(false),
                "TriggerParentEvent" => await TriggerParentEventAsync(call, cancellationToken).ConfigureAwait(false),
                "RespondToSubagentEvent" => RespondToSubagentEvent(call),
                "TriggerSubagentEvent" => await TriggerSubagentEventAsync(call, cancellationToken).ConfigureAwait(false),
                "CompleteTask" => CompleteTask(call),
                "ConfirmTaskComplete" => ConfirmTaskComplete(call),
                "ContinueWork" => ContinueWork(call),
                "ResumeCurrentTask" => ResumeCurrentTask(call),
                "ExpandThoughtProcess" => ExpandThoughtProcess(call),
                "StartNewTurn" => StartNewTurn(call),
                "DropTurnContext" => await DropTurnContextAsync(call, cancellationToken).ConfigureAwait(false),
                "RestoreTurnContext" => await RestoreTurnContextAsync(call, cancellationToken).ConfigureAwait(false),
                "WaitForSeconds" => await WaitForSecondsAsync(call, cancellationToken).ConfigureAwait(false),
                "ListTodos" => await ListTodosAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateTodo" => await CreateTodoAsync(call, cancellationToken).ConfigureAwait(false),
                "UpdateTodo" => await UpdateTodoAsync(call, cancellationToken).ConfigureAwait(false),
                "DeleteTodo" => await DeleteTodoAsync(call, cancellationToken).ConfigureAwait(false),
                "ReadFile" => await ReadFileAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateFile" => await CreateFileAsync(call, cancellationToken).ConfigureAwait(false),
                "WriteFile" => await WriteFileAsync(call, cancellationToken).ConfigureAwait(false),
                "Grep" => await GrepAsync(call, cancellationToken).ConfigureAwait(false),
                "LoadBinary" => await LoadBinaryAsync(call, cancellationToken).ConfigureAwait(false),
                "ListDirectory" => await ListDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateDirectory" => await CreateDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "ShellExecute" => await ShellExecuteAsync(call, cancellationToken).ConfigureAwait(false),
                "StartLongRunningShell" => await StartLongRunningShellAsync(call, cancellationToken).ConfigureAwait(false),
                "ListLongRunningShells" => ListLongRunningShells(call),
                "ReadLongRunningShellTail" => await ReadLongRunningShellTailAsync(call, cancellationToken).ConfigureAwait(false),
                "AbortLongRunningShell" => await AbortLongRunningShellAsync(call, cancellationToken).ConfigureAwait(false),
                "RequestLongRunningShellCancellation" => await RequestLongRunningShellCancellationAsync(call, cancellationToken).ConfigureAwait(false),
                "LongRunningShellInteract" => await LongRunningShellInteractAsync(call, cancellationToken).ConfigureAwait(false),
                "SubscribeToLongRunningShellCompletion" => SubscribeToLongRunningShellCompletion(call),
                "FreeSearch" => await FreeSearchAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeSearchAdvanced" => await FreeSearchAdvancedAsync(call, cancellationToken).ConfigureAwait(false),
                "SearchWithSynthesis" => await SearchWithSynthesisAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeExtract" => await FreeExtractAsync(call, cancellationToken).ConfigureAwait(false),
                "WebFetch" => await WebFetchAsync(call, cancellationToken).ConfigureAwait(false),
                "FetchGithubReadme" => await FetchGithubReadmeAsync(call, cancellationToken).ConfigureAwait(false),
                "OpenBrowser" or "ListBrowserWindows" or "CloseBrowser" or "ResizeBrowser"
                    or "ListBrowserTabs" or "NewBrowserTab" or "CloseBrowserTab" or "ActivateBrowserTab"
                    or "BrowserNavigate" or "BrowserGoBack" or "BrowserGoForward" or "BrowserReload"
                    or "BrowserClick" or "BrowserType" or "BrowserFill" or "BrowserHover" or "BrowserPressKey"
                    or "BrowserWaitForSelector" or "BrowserWaitForNavigation"
                    or "BrowserExecuteJavaScript" or "BrowserGetHtml" or "BrowserTakeScreenshot"
                    or "BrowserReadConsoleLog" or "BrowserReadNetworkLog"
                    => await ExecuteBrowserToolAsync(call, cancellationToken).ConfigureAwait(false),
                _ => Stub(call),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Error(call, "Tool execution was cancelled.");
        }
        catch (Exception ex)
        {
            return Error(call, $"{call.ToolName} failed: {ex.Message}");
        }
    }

    private static DysonToolCallResult Stub(DysonToolCall call) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = $"{call.ToolName} is not implemented yet.",
        };

    private static DysonToolCallResult Ok(
        DysonToolCall call,
        string content,
        DysonBinaryAttachment? binaryAttachment = null,
        bool endsCurrentTurn = false) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = content,
            BinaryAttachment = binaryAttachment,
            EndsCurrentTurn = endsCurrentTurn,
        };

    private static DysonToolCallResult Error(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = true,
            Content = content,
        };

    private async Task<DysonToolCallResult> RenameSessionAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? title = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (doc.RootElement.TryGetProperty("title", out var titleProp))
                title = titleProp.GetString();
        }
        catch (JsonException)
        {
            return Error(call, "RenameSession: invalid JSON arguments.");
        }

        var rename = await _session.RenameAsync(title ?? "", cancellationToken).ConfigureAwait(false);
        if (rename.IsError)
            return Error(call, rename.Error);

        if (_store is not null && _session.PersistenceId != Guid.Empty)
        {
            var persist = await _store.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    Title = _session.DisplayTitle,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
                return Error(call, persist.Error);

            var renamedLog = DysonSessionLogPayload.CreateEntry(
                _session.PersistenceId,
                DysonSessionLogKind.SessionRenamed,
                new DysonSessionLogSessionRenamed(_session.DisplayTitle!));

            await _store.AppendLogAsync(renamedLog, cancellationToken).ConfigureAwait(false);
        }

        return Ok(call, $"Renamed session to \"{_session.DisplayTitle}\".");
    }

    private Task<DysonToolCallResult> GetDateTimeAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var timezone = "utc";
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (doc.RootElement.TryGetProperty("timezone", out var tzProp)
                && tzProp.ValueKind == JsonValueKind.String)
            {
                var tz = tzProp.GetString();
                if (string.Equals(tz, "local", StringComparison.OrdinalIgnoreCase))
                    timezone = "local";
                else if (!string.IsNullOrWhiteSpace(tz)
                         && !string.Equals(tz, "utc", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(Error(call, "GetDateTime: timezone must be 'utc' or 'local'."));
            }
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, "GetDateTime: invalid JSON arguments."));
        }

        var now = timezone == "local" ? DateTimeOffset.Now : DateTimeOffset.UtcNow;
        var iso = timezone == "utc"
            ? now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
            : now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);
        var display = now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        var content = $"timezone: {timezone}\ndatetime: {iso}\ndisplay: {display}";
        return Task.FromResult(Ok(call, content));
    }

    private Task<DysonToolCallResult> SubmitPlanAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(_session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(Error(
                call,
                "SubmitPlan is only available in Plan mode. Start a Plan session to publish plan artifacts."));
        }

        string? title;
        string? markdown;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var titleResult = RequireString(doc.RootElement, "title");
            if (titleResult.IsError)
                return Task.FromResult(Error(call, titleResult.Error));
            title = titleResult.Value;

            var markdownResult = RequireString(doc.RootElement, "markdown");
            if (markdownResult.IsError)
                return Task.FromResult(Error(call, markdownResult.Error));
            markdown = markdownResult.Value;
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, "SubmitPlan: invalid JSON arguments."));
        }

        if (string.IsNullOrWhiteSpace(markdown))
            return Task.FromResult(Error(call, "SubmitPlan: markdown must be non-empty."));

        var fm = new DysonFileManager(_workRoot);
        var written = fm.WriteNewPlan(title, markdown);
        if (written.IsError)
            return Task.FromResult(Error(call, written.Error));

        var planPath = written.Value;
        _session.AppendPlanResultTurn(planPath, title);

        var abs = Path.GetFullPath(Path.Combine(_workRoot, planPath.Replace('/', Path.DirectorySeparatorChar)));
        var payload = $$"""
            {
              "planPath": {{JsonSerializer.Serialize(planPath)}},
              "absolutePath": {{JsonSerializer.Serialize(abs)}},
              "title": {{JsonSerializer.Serialize(title.Trim())}}
            }
            """;

        return Task.FromResult(Ok(call, payload.Trim()));
    }

    private async Task<DysonToolCallResult> StartSubagentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? agentMode;
        string? task;
        string? context;
        string? modelSlug;
        string? reasoningEffort;
        IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var mode = RequireString(root, "agentMode");
            if (mode.IsError)
                return Error(call, mode.Error);
            var taskResult = RequireString(root, "task");
            if (taskResult.IsError)
                return Error(call, taskResult.Error);

            agentMode = mode.Value;
            task = taskResult.Value;
            context = GetOptionalString(root, "context");
            modelSlug = GetOptionalString(root, "modelSlug");
            reasoningEffort = GetOptionalString(root, "reasoningEffort");

            var todos = TryParseTodoSeedItems(root, "todos");
            if (todos.IsError)
                return Error(call, todos.Error);
            initialTodos = todos.Value;
        }
        catch (JsonException)
        {
            return Error(call, "StartSubagent: invalid JSON arguments.");
        }

        var started = await _session.CreateChildAsync(
                agentMode,
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
        return Ok(call, JsonSerializer.Serialize(new
        {
            subagentId = r.SubagentId,
            persistenceId = r.PersistenceId,
            agentMode = r.AgentMode,
            title = r.Title,
            modelSlug = r.ModelSlug,
            modelLabel = r.ModelLabel,
        }));
    }

    private Task<DysonToolCallResult> ListSubagentsAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Ok(call, _session.FormatListSubagentsJson()));
    }

    private async Task<DysonToolCallResult> ListTodosAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var listed = await _session.ListTodosAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return Error(call, listed.Error);

        return Ok(call, SerializeTodos(listed.Value));
    }

    private async Task<DysonToolCallResult> CreateTodoAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string displayName;
        string taskCode;
        DysonSessionTodoStatus status;
        IReadOnlyList<string>? comments;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var name = RequireString(root, "displayName");
            if (name.IsError)
                return Error(call, name.Error);
            var code = RequireString(root, "taskCode");
            if (code.IsError)
                return Error(call, code.Error);

            displayName = name.Value;
            taskCode = code.Value;

            var statusParse = TryParseOptionalTodoStatus(root, "status");
            if (statusParse.IsError)
                return Error(call, statusParse.Error);
            status = statusParse.Value ?? DysonSessionTodoStatus.Pending;

            var commentsParse = TryParseOptionalStringArray(root, "comments");
            if (commentsParse.IsError)
                return Error(call, commentsParse.Error);
            comments = commentsParse.Value;
        }
        catch (JsonException)
        {
            return Error(call, "CreateTodo: invalid JSON arguments.");
        }

        var created = await _session.CreateTodoAsync(
                taskCode,
                displayName,
                status,
                comments,
                cancellationToken)
            .ConfigureAwait(false);
        if (created.IsError)
            return Error(call, created.Error);

        return Ok(call, SerializeTodo(created.Value));
    }

    private async Task<DysonToolCallResult> UpdateTodoAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string taskCode;
        string? displayName;
        DysonSessionTodoStatus? status;
        IReadOnlyList<string>? comments;
        string? appendComment;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var code = RequireString(root, "taskCode");
            if (code.IsError)
                return Error(call, code.Error);
            taskCode = code.Value;
            displayName = GetOptionalString(root, "displayName");

            var statusParse = TryParseOptionalTodoStatus(root, "status");
            if (statusParse.IsError)
                return Error(call, statusParse.Error);
            status = statusParse.Value;

            var hasComments = root.TryGetProperty("comments", out _);
            comments = null;
            if (hasComments)
            {
                var commentsParse = TryParseOptionalStringArray(root, "comments");
                if (commentsParse.IsError)
                    return Error(call, commentsParse.Error);
                comments = commentsParse.Value ?? [];
            }

            appendComment = GetOptionalString(root, "appendComment");
        }
        catch (JsonException)
        {
            return Error(call, "UpdateTodo: invalid JSON arguments.");
        }

        var updated = await _session.UpdateTodoAsync(
                taskCode,
                displayName,
                status,
                comments,
                appendComment,
                cancellationToken)
            .ConfigureAwait(false);
        if (updated.IsError)
            return Error(call, updated.Error);

        return Ok(call, SerializeTodo(updated.Value));
    }

    private async Task<DysonToolCallResult> DeleteTodoAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string taskCode;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var code = RequireString(doc.RootElement, "taskCode");
            if (code.IsError)
                return Error(call, code.Error);
            taskCode = code.Value;
        }
        catch (JsonException)
        {
            return Error(call, "DeleteTodo: invalid JSON arguments.");
        }

        var deleted = await _session.DeleteTodoAsync(taskCode, cancellationToken).ConfigureAwait(false);
        if (deleted.IsError)
            return Error(call, deleted.Error);

        return Ok(call, $"Deleted todo '{taskCode}'.");
    }

    /// <summary>
    /// Parses optional <paramref name="propertyName"/> array of todo seed objects
    /// (<c>displayName</c>, <c>taskCode</c>, optional <c>status</c>/<c>comments</c>).
    /// </summary>
    public static Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string> TryParseTodoSeedItems(
        JsonElement root,
        string propertyName = "todos")
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsValue(null);

        if (prop.ValueKind == JsonValueKind.Null)
            return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsValue(null);

        if (prop.ValueKind != JsonValueKind.Array)
        {
            return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsError(
                $"Field '{propertyName}' must be an array.");
        }

        var items = new List<DysonSessionTodoReplaceItem>();
        var index = 0;
        foreach (var el in prop.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsError(
                    $"{propertyName}[{index}] must be an object.");
            }

            var displayName = RequireString(el, "displayName");
            if (displayName.IsError)
            {
                return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsError(
                    $"{propertyName}[{index}]: {displayName.Error}");
            }

            var taskCode = RequireString(el, "taskCode");
            if (taskCode.IsError)
            {
                return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsError(
                    $"{propertyName}[{index}]: {taskCode.Error}");
            }

            var statusParse = TryParseOptionalTodoStatus(el, "status");
            if (statusParse.IsError)
            {
                return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsError(
                    $"{propertyName}[{index}]: {statusParse.Error}");
            }

            var commentsParse = TryParseOptionalStringArray(el, "comments");
            if (commentsParse.IsError)
            {
                return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsError(
                    $"{propertyName}[{index}]: {commentsParse.Error}");
            }

            items.Add(new DysonSessionTodoReplaceItem
            {
                DisplayName = displayName.Value,
                TaskCode = taskCode.Value,
                Status = statusParse.Value ?? DysonSessionTodoStatus.Pending,
                Comments = commentsParse.Value,
            });
            index++;
        }

        return Result<IReadOnlyList<DysonSessionTodoReplaceItem>?, string>.AsValue(items);
    }

    private async Task<DysonToolCallResult> WaitForSubagentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int subagentId;
        int? timeoutMs;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "subagentId");
            if (id is null or < 1)
                return Error(call, "WaitForSubagent: subagentId (≥ 1) is required.");
            subagentId = id.Value;
            timeoutMs = GetInt(doc.RootElement, "timeoutMs");
        }
        catch (JsonException)
        {
            return Error(call, "WaitForSubagent: invalid JSON arguments.");
        }

        var waited = await _session.WaitForSubagentAsync(subagentId, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        return waited.IsError ? Error(call, waited.Error) : Ok(call, waited.Value);
    }

    private Task<DysonToolCallResult> InspectSubagentLogAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int subagentId;
        int? maxLines;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "subagentId");
            if (id is null or < 1)
                return Task.FromResult(Error(call, "InspectSubagentLog: subagentId (≥ 1) is required."));
            subagentId = id.Value;
            maxLines = GetInt(doc.RootElement, "maxLines");
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, "InspectSubagentLog: invalid JSON arguments."));
        }

        var inspected = _session.InspectSubagentLog(subagentId, maxLines);
        return Task.FromResult(
            inspected.IsError ? Error(call, inspected.Error) : Ok(call, inspected.Value));
    }

    private async Task<DysonToolCallResult> StopSubagentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int subagentId;
        string? reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "subagentId");
            if (id is null or < 1)
                return Error(call, "StopSubagent: subagentId (≥ 1) is required.");
            subagentId = id.Value;
            reason = GetOptionalString(doc.RootElement, "reason");
        }
        catch (JsonException)
        {
            return Error(call, "StopSubagent: invalid JSON arguments.");
        }

        if (!_session.TryGetSubagent(subagentId, out var child))
            return Error(call, $"Unknown subagentId {subagentId}.");

        var stopped = await _session.StopSubagentAsync(subagentId, reason, cancellationToken)
            .ConfigureAwait(false);
        if (stopped.IsError)
            return Error(call, stopped.Error);

        await PersistSessionStatusAsync(child, child.Status, reason, cancellationToken)
            .ConfigureAwait(false);

        return Ok(call, stopped.Value);
    }

    private async Task<DysonToolCallResult> SubmitSubagentReportAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string summary;
        var failed = false;
        var skipTasksCheck = false;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var summaryResult = RequireString(doc.RootElement, "summary");
            if (summaryResult.IsError)
                return Error(call, summaryResult.Error);
            summary = summaryResult.Value;

            if (doc.RootElement.TryGetProperty("status", out var statusProp)
                && statusProp.ValueKind == JsonValueKind.String)
            {
                var status = statusProp.GetString();
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                    failed = true;
                else if (!string.IsNullOrWhiteSpace(status)
                         && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                {
                    return Error(call, "SubmitSubagentReport: status must be 'completed' or 'failed'.");
                }
            }

            skipTasksCheck = GetBool(doc.RootElement, "skipTasksCheck");
        }
        catch (JsonException)
        {
            return Error(call, "SubmitSubagentReport: invalid JSON arguments.");
        }

        var submitted = await _session
            .SubmitSubagentReportAsync(summary, failed, skipTasksCheck, cancellationToken)
            .ConfigureAwait(false);
        if (submitted.IsError)
            return Error(call, submitted.Error);

        var idempotent = ReportResultIsIdempotent(submitted.Value);

        await PersistSessionStatusAsync(
                _session,
                _session.Status,
                idempotent ? _session.LastReportSummary : summary,
                cancellationToken)
            .ConfigureAwait(false);

        // Idempotent retries must not re-notify / re-append parent interrupt.
        if (!idempotent
            && _session.Parent is not null
            && _store is not null
            && _session.Parent.PersistenceId != Guid.Empty)
        {
            var interruptLog = DysonSessionLogPayload.CreateEntry(
                _session.Parent.PersistenceId,
                DysonSessionLogKind.Interrupt,
                new DysonSessionLogInterrupt(
                    failed
                        ? DysonAgentInterruptKind.SubagentFailed.ToString()
                        : DysonAgentInterruptKind.SubagentCompleted.ToString(),
                    SubagentId: _session.Id,
                    Summary: summary,
                    PersistenceId: _session.PersistenceId == Guid.Empty ? null : _session.PersistenceId));

            await _store.AppendLogAsync(interruptLog, cancellationToken).ConfigureAwait(false);
        }

        return Ok(call, submitted.Value);
    }

    private static bool ReportResultIsIdempotent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("idempotent", out var prop)
                   && prop.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<DysonToolCallResult> AskQuestionAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var questionsJson = ExtractQuestionsJson(call);
        if (questionsJson.IsError)
            return Error(call, questionsJson.Error);

        var asked = await _session.AskQuestionAsync(questionsJson.Value, cancellationToken)
            .ConfigureAwait(false);
        return asked.IsError ? Error(call, asked.Error) : Ok(call, asked.Value);
    }

    private async Task<DysonToolCallResult> AskQuestionFromParentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var questionsJson = ExtractQuestionsJson(call);
        if (questionsJson.IsError)
            return Error(call, questionsJson.Error);

        var asked = await _session.AskQuestionFromParentAsync(questionsJson.Value, cancellationToken)
            .ConfigureAwait(false);
        return asked.IsError ? Error(call, asked.Error) : Ok(call, asked.Value);
    }

    private async Task<DysonToolCallResult> TriggerParentEventAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string kind;
        string payload;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var kindResult = RequireString(doc.RootElement, "kind");
            if (kindResult.IsError)
                return Error(call, kindResult.Error);
            kind = kindResult.Value;

            var payloadResult = RequireString(doc.RootElement, "payload");
            if (payloadResult.IsError)
                return Error(call, payloadResult.Error);
            payload = payloadResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "TriggerParentEvent: invalid JSON arguments.");
        }

        var triggered = await _session.TriggerParentEventAsync(kind, payload, cancellationToken)
            .ConfigureAwait(false);
        return triggered.IsError ? Error(call, triggered.Error) : Ok(call, triggered.Value);
    }

    private DysonToolCallResult RespondToSubagentEvent(DysonToolCall call)
    {
        int subagentId;
        Guid eventId;
        string reply;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "subagentId");
            if (id is null or < 1)
                return Error(call, "RespondToSubagentEvent: subagentId (≥ 1) is required.");
            subagentId = id.Value;

            var eventIdResult = RequireString(doc.RootElement, "eventId");
            if (eventIdResult.IsError)
                return Error(call, eventIdResult.Error);
            if (!Guid.TryParse(eventIdResult.Value, out eventId) || eventId == Guid.Empty)
                return Error(call, "RespondToSubagentEvent: eventId must be a non-empty Guid.");

            var replyResult = RequireString(doc.RootElement, "reply");
            if (replyResult.IsError)
                return Error(call, replyResult.Error);
            reply = replyResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "RespondToSubagentEvent: invalid JSON arguments.");
        }

        var responded = _session.RespondToSubagentEvent(subagentId, eventId, reply);
        return responded.IsError ? Error(call, responded.Error) : Ok(call, responded.Value);
    }

    private async Task<DysonToolCallResult> TriggerSubagentEventAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int subagentId;
        string payload;
        var interrupt = false;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var id = GetInt(doc.RootElement, "subagentId");
            if (id is null or < 1)
                return Error(call, "TriggerSubagentEvent: subagentId (≥ 1) is required.");
            subagentId = id.Value;

            var payloadResult = RequireString(doc.RootElement, "payload");
            if (payloadResult.IsError)
                return Error(call, payloadResult.Error);
            payload = payloadResult.Value;

            interrupt = GetBool(doc.RootElement, "interruptSubagent");
        }
        catch (JsonException)
        {
            return Error(call, "TriggerSubagentEvent: invalid JSON arguments.");
        }

        var triggered = await _session
            .TriggerSubagentEventAsync(subagentId, payload, interrupt, cancellationToken)
            .ConfigureAwait(false);
        return triggered.IsError ? Error(call, triggered.Error) : Ok(call, triggered.Value);
    }

    private DysonToolCallResult CompleteTask(DysonToolCall call)
    {
        if (_session.Parent is not null)
            return Error(call, "CompleteTask: root sessions only.");

        if (_session.IsTerminal)
            return Error(call, $"CompleteTask: session already {_session.Status}.");

        string summary;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var summaryResult = RequireString(doc.RootElement, "summary");
            if (summaryResult.IsError)
                return Error(call, summaryResult.Error);
            summary = summaryResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "CompleteTask: invalid JSON arguments.");
        }

        var incomplete = _session.Todos
            .Where(t => t.Status is DysonSessionTodoStatus.Pending or DysonSessionTodoStatus.Ongoing)
            .Select(t => $"{t.TaskCode} ({t.DisplayName})={t.Status}")
            .ToArray();
        if (incomplete.Length > 0)
        {
            return Error(call,
                "CompleteTask: incomplete todos: " + string.Join("; ", incomplete));
        }

        var turn = DysonTaskCompletionFlow.CreateCompletionConfirmTurn(summary);
        _session.EnqueuePendingTurn(turn);

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = "queued",
            nextTurnKind = DysonAgentTurnKind.TaskCompletionConfirm.ToString(),
            summary,
        }));
    }

    private DysonToolCallResult ConfirmTaskComplete(DysonToolCall call)
    {
        if (_session.Parent is not null)
            return Error(call, "ConfirmTaskComplete: root sessions only.");

        if (!_session.IsInTaskCompletionConfirmPhase)
        {
            return Error(call,
                "ConfirmTaskComplete: only valid during a TaskCompletionConfirm turn after CompleteTask.");
        }

        string? rationale = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            rationale = GetOptionalString(doc.RootElement, "rationale");
        }
        catch (JsonException)
        {
            return Error(call, "ConfirmTaskComplete: invalid JSON arguments.");
        }

        var turn = DysonTaskCompletionFlow.CreateReportSummaryTurn(rationale);
        _session.EnqueuePendingTurn(turn);

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = "queued",
            nextTurnKind = DysonAgentTurnKind.ReportSummary.ToString(),
            rationale,
        }));
    }

    private DysonToolCallResult ContinueWork(DysonToolCall call)
    {
        if (_session.Parent is not null)
            return Error(call, "ContinueWork: root sessions only.");

        if (!_session.IsInTaskCompletionConfirmPhase)
        {
            return Error(call,
                "ContinueWork: only valid during a TaskCompletionConfirm turn after CompleteTask.");
        }

        string? reason = null;
        string? remainingWork = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            reason = GetOptionalString(doc.RootElement, "reason");
            remainingWork = GetOptionalString(doc.RootElement, "remainingWork");
        }
        catch (JsonException)
        {
            return Error(call, "ContinueWork: invalid JSON arguments.");
        }

        if (string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(remainingWork))
            return Error(call, "ContinueWork: reason or remainingWork is required.");

        var turn = DysonTaskCompletionFlow.CreateContinuationTurn(reason, remainingWork);
        _session.EnqueuePendingTurn(turn);

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = "queued",
            nextTurnKind = DysonAgentTurnKind.Continuation.ToString(),
            reason,
            remainingWork,
        }));
    }

    private DysonToolCallResult ResumeCurrentTask(DysonToolCall call)
    {
        if (!_session.IsInRethinkToolUsagePhase)
        {
            return Error(call,
                "ResumeCurrentTask: only valid during a RethinkToolUsage turn after a tool-round soft-pause.");
        }

        string? rationale = null;
        string? continuationInstructions = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            rationale = GetOptionalString(doc.RootElement, "rationale");
            continuationInstructions = GetOptionalString(doc.RootElement, "continuationInstructions");
        }
        catch (JsonException)
        {
            return Error(call, "ResumeCurrentTask: invalid JSON arguments.");
        }

        if (string.IsNullOrWhiteSpace(rationale) && string.IsNullOrWhiteSpace(continuationInstructions))
            return Error(call, "ResumeCurrentTask: rationale or continuationInstructions is required.");

        var turn = DysonRethinkToolUsageFlow.CreateResumeTurn(rationale, continuationInstructions);
        _session.EnqueuePendingTurn(turn);

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = "queued",
            nextTurnKind = DysonAgentTurnKind.Normal.ToString(),
            rationale,
            continuationInstructions,
        }));
    }

    private DysonToolCallResult ExpandThoughtProcess(DysonToolCall call)
    {
        if (_session.IsInExpandThoughtProcessPhase)
        {
            return Error(call,
                "ExpandThoughtProcess: already on an ExpandThoughtProcess turn; recursion is not allowed.");
        }

        string? focus = null;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            focus = GetOptionalString(doc.RootElement, "focus");
        }
        catch (JsonException)
        {
            return Error(call, "ExpandThoughtProcess: invalid JSON arguments.");
        }

        var turn = _session.CreateExpandThoughtProcessTurn(focus);
        _session.EnqueuePendingTurn(turn);

        return Ok(
            call,
            JsonSerializer.Serialize(new
            {
                status = "queued",
                nextTurnKind = DysonAgentTurnKind.ExpandThoughtProcess.ToString(),
                focus,
            }),
            endsCurrentTurn: true);
    }

    private DysonToolCallResult StartNewTurn(DysonToolCall call)
    {
        string promptInstructions;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var required = RequireString(doc.RootElement, "promptInstructions");
            if (required.IsError)
                return Error(call, "StartNewTurn: promptInstructions (non-empty string) is required.");
            promptInstructions = required.Value;
        }
        catch (JsonException)
        {
            return Error(call, "StartNewTurn: invalid JSON arguments.");
        }

        var turn = DysonAgentSession.CreateNormalTurn(promptInstructions);
        _session.EnqueuePendingTurn(turn);

        return Ok(
            call,
            JsonSerializer.Serialize(new
            {
                status = "queued",
                nextTurnKind = DysonAgentTurnKind.Normal.ToString(),
                promptInstructions = turn.Instruction,
            }),
            endsCurrentTurn: true);
    }

    private async Task<DysonToolCallResult> DropTurnContextAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? turnIdStrings;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var parsed = TryParseOptionalStringArray(doc.RootElement, "turnIds");
            if (parsed.IsError)
                return Error(call, parsed.Error);
            turnIdStrings = parsed.Value;

            var reasonResult = RequireString(doc.RootElement, "reason");
            if (reasonResult.IsError)
                return Error(call, "DropTurnContext: reason (non-empty string) is required.");
            reason = reasonResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "DropTurnContext: invalid JSON arguments.");
        }

        if (turnIdStrings is null || turnIdStrings.Count == 0)
            return Error(call, "DropTurnContext: turnIds (non-empty array) is required.");

        var currentId = _session.Turns.Count > 0 ? _session.Turns[^1].Id : Guid.Empty;
        var dropped = new List<string>();
        var skipped = new List<string>();

        foreach (var raw in turnIdStrings)
        {
            if (!Guid.TryParse(raw, out var id))
            {
                skipped.Add($"{raw}: invalid guid");
                continue;
            }

            if (id == currentId)
            {
                skipped.Add($"{id:D}: cannot drop the in-flight turn");
                continue;
            }

            var match = _session.Turns.FirstOrDefault(t => t.Id == id);
            if (match is null)
            {
                skipped.Add($"{id:D}: unknown turn id");
                continue;
            }

            if (match.IsExcludedFromContext)
            {
                dropped.Add(id.ToString("D"));
                continue;
            }

            match.IsExcludedFromContext = true;
            dropped.Add(id.ToString("D"));
            _session.AppendLog($"Turn {id:D} dropped, reason: {reason}");

            if (_store is not null && _session.PersistenceId != Guid.Empty)
            {
                var sequence = IndexOfTurn(match);
                var entity = DysonTurnPersistence.ToEntity(match, _session.PersistenceId, sequence);
                var upserted = await _store.UpsertTurnAsync(entity, cancellationToken).ConfigureAwait(false);
                if (upserted.IsError)
                    return Error(call, upserted.Error);
            }
        }

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = skipped.Count == 0 ? "ok" : "partial",
            reason,
            dropped,
            skipped,
        }));
    }

    private async Task<DysonToolCallResult> RestoreTurnContextAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? turnIdStrings;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var parsed = TryParseOptionalStringArray(doc.RootElement, "turnIds");
            if (parsed.IsError)
                return Error(call, parsed.Error);
            turnIdStrings = parsed.Value;

            var reasonResult = RequireString(doc.RootElement, "reason");
            if (reasonResult.IsError)
                return Error(call, "RestoreTurnContext: reason (non-empty string) is required.");
            reason = reasonResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "RestoreTurnContext: invalid JSON arguments.");
        }

        if (turnIdStrings is null || turnIdStrings.Count == 0)
            return Error(call, "RestoreTurnContext: turnIds (non-empty array) is required.");

        var restored = new List<string>();
        var skipped = new List<string>();

        foreach (var raw in turnIdStrings)
        {
            if (!Guid.TryParse(raw, out var id))
            {
                skipped.Add($"{raw}: invalid guid");
                continue;
            }

            var match = _session.Turns.FirstOrDefault(t => t.Id == id);
            if (match is null)
            {
                skipped.Add($"{id:D}: unknown turn id");
                continue;
            }

            if (!match.IsExcludedFromContext)
            {
                restored.Add(id.ToString("D"));
                continue;
            }

            match.IsExcludedFromContext = false;
            restored.Add(id.ToString("D"));
            _session.AppendLog($"Turn {id:D} restored, reason: {reason}");

            if (_store is not null && _session.PersistenceId != Guid.Empty)
            {
                var sequence = IndexOfTurn(match);
                var entity = DysonTurnPersistence.ToEntity(match, _session.PersistenceId, sequence);
                var upserted = await _store.UpsertTurnAsync(entity, cancellationToken).ConfigureAwait(false);
                if (upserted.IsError)
                    return Error(call, upserted.Error);
            }
        }

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = skipped.Count == 0 ? "ok" : "partial",
            reason,
            restored,
            skipped,
        }));
    }

    private int IndexOfTurn(DysonAgentTurn match)
    {
        for (var i = 0; i < _session.Turns.Count; i++)
        {
            if (ReferenceEquals(_session.Turns[i], match) || _session.Turns[i].Id == match.Id)
                return i;
        }

        return -1;
    }

    private async Task<DysonToolCallResult> WaitForSecondsAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        int seconds;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (!doc.RootElement.TryGetProperty("seconds", out var secondsProp)
                || secondsProp.ValueKind != JsonValueKind.Number
                || !secondsProp.TryGetInt32(out seconds))
            {
                return Error(call, "WaitForSeconds: seconds (integer) is required.");
            }
        }
        catch (JsonException)
        {
            return Error(call, "WaitForSeconds: invalid JSON arguments.");
        }

        if (seconds < 1 || seconds > 300)
            return Error(call, "WaitForSeconds: seconds must be between 1 and 300.");

        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);

        return Ok(call, JsonSerializer.Serialize(new
        {
            status = "ok",
            waitedSeconds = seconds,
        }));
    }

    private static Result<string, string> ExtractQuestionsJson(DysonToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (!doc.RootElement.TryGetProperty("questions", out var questions))
                return Result<string, string>.AsError("questions is required.");

            return Result<string, string>.AsValue(questions.GetRawText());
        }
        catch (JsonException)
        {
            return Result<string, string>.AsError("invalid JSON arguments.");
        }
    }

    private async Task PersistSessionStatusAsync(
        DysonAgentSession session,
        DysonSessionStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (_store is null || session.PersistenceId == Guid.Empty)
            return;

        var persist = await _store.UpdateSessionMetaAsync(
            new DysonSessionMetaUpdate
            {
                SessionId = session.PersistenceId,
                Status = status,
            },
            cancellationToken).ConfigureAwait(false);

        if (persist.IsError)
            return;

        var statusLog = DysonSessionLogPayload.CreateEntry(
            session.PersistenceId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(status, reason));

        await _store.AppendLogAsync(statusLog, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> ReadFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value))
            return Error(call, $"File not found: {path.Value}");

        var lines = await File.ReadAllLinesAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var offset = GetInt(doc.RootElement, "offset") ?? 1;
        var limit = GetInt(doc.RootElement, "limit");
        if (offset < 1)
            offset = 1;

        var start = Math.Min(offset - 1, lines.Length);
        var take = limit is null ? lines.Length - start : Math.Max(0, limit.Value);
        var slice = lines.Skip(start).Take(take);

        var sb = new StringBuilder();
        var lineNo = start + 1;
        foreach (var line in slice)
        {
            sb.Append(lineNo);
            sb.Append('|');
            sb.AppendLine(line);
            lineNo++;
        }

        return Ok(call, sb.Length == 0 ? "(empty)" : sb.ToString().TrimEnd());
    }

    private async Task<DysonToolCallResult> CreateFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var content = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var overwrite = doc.RootElement.TryGetProperty("overwrite", out var o)
            && o.ValueKind == JsonValueKind.True;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (File.Exists(resolved.Value) && !overwrite)
            return Error(call, $"File already exists: {path.Value}");

        var dir = Path.GetDirectoryName(resolved.Value);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(resolved.Value, content, cancellationToken).ConfigureAwait(false);
        return Ok(call, $"Created {path.Value} ({content.Length} chars).");
    }

    private async Task<DysonToolCallResult> WriteFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value)
            && !(doc.RootElement.TryGetProperty("content", out var fullContentProp)
                 && fullContentProp.ValueKind == JsonValueKind.String))
        {
            return Error(call, $"File not found: {path.Value}");
        }

        if (doc.RootElement.TryGetProperty("content", out var contentProp)
            && contentProp.ValueKind == JsonValueKind.String
            && !doc.RootElement.TryGetProperty("old_text", out _)
            && !doc.RootElement.TryGetProperty("edits", out _))
        {
            var dir = Path.GetDirectoryName(resolved.Value);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var full = contentProp.GetString() ?? "";
            await File.WriteAllTextAsync(resolved.Value, full, cancellationToken).ConfigureAwait(false);
            return Ok(call, $"Wrote full content to {path.Value} ({full.Length} chars).");
        }

        var text = await File.ReadAllTextAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var edits = new List<(string Old, string New, bool ReplaceAll)>();
        var defaultReplaceAll = GetBool(doc.RootElement, "replace_all");

        if (doc.RootElement.TryGetProperty("old_text", out var oldProp)
            && doc.RootElement.TryGetProperty("new_text", out var newProp))
        {
            edits.Add((oldProp.GetString() ?? "", newProp.GetString() ?? "", defaultReplaceAll));
        }

        if (doc.RootElement.TryGetProperty("edits", out var editsArr)
            && editsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in editsArr.EnumerateArray())
            {
                if (!edit.TryGetProperty("old_text", out var o) || !edit.TryGetProperty("new_text", out var n))
                    continue;
                var itemReplaceAll = edit.TryGetProperty("replace_all", out var ra)
                    ? ra.ValueKind == JsonValueKind.True
                    : defaultReplaceAll;
                edits.Add((o.GetString() ?? "", n.GetString() ?? "", itemReplaceAll));
            }
        }

        if (edits.Count == 0)
            return Error(call, "WriteFile: provide content, or old_text/new_text, or edits[].");

        var appliedEdits = 0;
        var replacementCount = 0;
        foreach (var (oldText, newText, replaceAll) in edits)
        {
            if (string.IsNullOrEmpty(oldText))
                return Error(call, "WriteFile: old_text must be non-empty.");

            var result = DysonTextEditApplier.TryReplace(text, oldText, newText, replaceAll);
            if (result.IsError)
            {
                var failure = result.Error;
                return Error(call, $"WriteFile: {failure.Message} ({path.Value})");
            }

            text = result.Value.Content;
            appliedEdits++;
            replacementCount += result.Value.ReplacementCount;
        }

        await File.WriteAllTextAsync(resolved.Value, text, cancellationToken).ConfigureAwait(false);
        return Ok(
            call,
            $"Applied {appliedEdits} edit(s) ({replacementCount} replacement(s)) to {path.Value}.");
    }

    private const int GrepMaxLineChars = 400;
    private const int GrepMaxResultChars = 48 * 1024;
    private const int LoadBinaryMaxBytes = 5 * 1024 * 1024;
    private const int GrepBinarySniffBytes = 512;

    private static readonly HashSet<string> GrepExcludedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", "packages", ".idea", "dist",
        "__pycache__", ".hg", ".svn", ".tox", ".venv", "venv",
    };

    private static readonly HashSet<string> GrepImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> GrepBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp",
        ".tif", ".tiff", ".pak", ".bin", ".so", ".dylib", ".o", ".a", ".lib", ".wasm",
        ".zip", ".7z", ".rar", ".gz", ".tar", ".bz2", ".xz", ".class", ".jar", ".nupkg",
        ".snupkg", ".ttf", ".otf", ".woff", ".woff2", ".eot", ".mp3", ".mp4", ".wav",
        ".avi", ".mov", ".webm", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".sqlite", ".db", ".dat", ".cache", ".ilk", ".exp", ".suo", ".user", ".vsidx",
    };

    private async Task<DysonToolCallResult> GrepAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var pattern = RequireString(doc.RootElement, "pattern");
        if (pattern.IsError)
            return Error(call, pattern.Error);

        var searchPath = doc.RootElement.TryGetProperty("path", out var pathProp)
            ? pathProp.GetString() ?? "."
            : ".";
        var glob = doc.RootElement.TryGetProperty("glob", out var globProp)
            ? globProp.GetString()
            : null;
        var caseInsensitive = doc.RootElement.TryGetProperty("caseInsensitive", out var ci)
            && ci.ValueKind == JsonValueKind.True;
        var maxMatches = GetInt(doc.RootElement, "maxMatches") ?? 100;

        var resolved = ResolveUnderWorkRoot(searchPath);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        Regex regex;
        try
        {
            regex = new Regex(
                pattern.Value,
                (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None)
                | RegexOptions.Compiled,
                TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Error(call, $"Invalid regex: {ex.Message}");
        }

        IEnumerable<string> files;
        if (File.Exists(resolved.Value))
        {
            files = [resolved.Value];
        }
        else if (Directory.Exists(resolved.Value))
        {
            files = EnumerateFilesSkippingExcluded(resolved.Value, glob);
        }
        else
        {
            return Error(call, $"Path not found: {searchPath}");
        }

        var sb = new StringBuilder();
        var matches = 0;
        var binaryHits = 0;
        var cappedByChars = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnderWorkRoot(file))
                continue;

            var rel = Path.GetRelativePath(_workRoot, file).Replace('\\', '/');
            var kind = ClassifyGrepFile(file);
            if (kind is GrepFileKind.Binary or GrepFileKind.Image)
            {
                // Path-only: never inline binary/image bytes. Emit when the relative path matches.
                if (!regex.IsMatch(rel))
                    continue;

                var label = kind == GrepFileKind.Image ? "image" : "binary";
                var line = $"{label}\t{rel}";
                if (sb.Length + line.Length + 1 > GrepMaxResultChars)
                {
                    cappedByChars = true;
                    break;
                }

                sb.AppendLine(line);
                matches++;
                binaryHits++;
                if (matches >= maxMatches)
                    break;
                continue;
            }

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                    continue;

                var content = TruncateGrepLine(lines[i], GrepMaxLineChars);
                var line = $"{rel}:{i + 1}:{content}";
                if (sb.Length + line.Length + 1 > GrepMaxResultChars)
                {
                    cappedByChars = true;
                    break;
                }

                sb.AppendLine(line);
                matches++;
                if (matches >= maxMatches)
                    break;
            }

            if (matches >= maxMatches || cappedByChars)
                break;
        }

        if (matches == 0)
            return Ok(call, "No matches.");

        if (binaryHits > 0)
            sb.AppendLine("Use LoadBinary to inspect binary/image files.");

        var text = sb.ToString().TrimEnd();
        if (matches >= maxMatches)
            text += $"\n… capped at {maxMatches} matches";
        else if (cappedByChars)
            text += $"\n… capped at {GrepMaxResultChars} chars";

        return Ok(call, text);
    }

    private async Task<DysonToolCallResult> LoadBinaryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Error(call, path.Error);

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Error(call, resolved.Error);

        if (!File.Exists(resolved.Value))
            return Error(call, $"File not found: {path.Value}");

        var info = new FileInfo(resolved.Value);
        if (info.Length > LoadBinaryMaxBytes)
        {
            return Error(call,
                $"LoadBinary: file is {info.Length} bytes; max is {LoadBinaryMaxBytes} bytes.");
        }

        var bytes = await File.ReadAllBytesAsync(resolved.Value, cancellationToken).ConfigureAwait(false);
        var fileName = Path.GetFileName(resolved.Value);
        var extension = Path.GetExtension(resolved.Value);
        var mimeType = MimeTypeFromExtension(extension);
        var attachment = new DysonBinaryAttachment
        {
            FileName = fileName,
            Extension = extension,
            MimeType = mimeType,
            Base64Data = Convert.ToBase64String(bytes),
        };

        var ack = JsonSerializer.Serialize(new
        {
            path = path.Value.Replace('\\', '/'),
            fileName,
            extension,
            mimeType,
            byteLength = bytes.Length,
        });

        return Ok(call, ack, attachment);
    }

    private enum GrepFileKind
    {
        Text,
        Binary,
        Image,
    }

    internal static string MimeTypeFromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "application/octet-stream";

        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".bmp" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".pdf" => "application/pdf",
            ".svg" => "image/svg+xml",
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".wasm" => "application/wasm",
            ".zip" => "application/zip",
            ".dll" or ".exe" => "application/octet-stream",
            _ => "application/octet-stream",
        };
    }

    private static GrepFileKind ClassifyGrepFile(string absolutePath)
    {
        var ext = Path.GetExtension(absolutePath);
        if (GrepImageExtensions.Contains(ext))
            return GrepFileKind.Image;
        if (GrepBinaryExtensions.Contains(ext))
            return GrepFileKind.Binary;

        if (FileLooksBinaryByNulSniff(absolutePath))
            return GrepFileKind.Binary;

        return GrepFileKind.Text;
    }

    private static bool FileLooksBinaryByNulSniff(string absolutePath)
    {
        try
        {
            using var stream = File.OpenRead(absolutePath);
            var buf = new byte[GrepBinarySniffBytes];
            var read = stream.Read(buf, 0, buf.Length);
            for (var i = 0; i < read; i++)
            {
                if (buf[i] == 0)
                    return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFilesSkippingExcluded(string rootDir, string? glob)
    {
        var pattern = string.IsNullOrWhiteSpace(glob) ? "*" : glob;
        var stack = new Stack<string>();
        stack.Push(rootDir);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (GrepExcludedDirNames.Contains(name))
                    continue;
                stack.Push(sub);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;
        }
    }

    private static string TruncateGrepLine(string line, int maxChars)
    {
        if (line.Length <= maxChars)
            return line;
        return line[..maxChars] + "…";
    }

    private Task<DysonToolCallResult> ListDirectoryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Task.FromResult(Error(call, path.Error));

        var recursive = doc.RootElement.TryGetProperty("recursive", out var r)
            && r.ValueKind == JsonValueKind.True;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Task.FromResult(Error(call, resolved.Error));

        if (!Directory.Exists(resolved.Value))
            return Task.FromResult(Error(call, $"Directory not found: {path.Value}"));

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var entries = Directory.EnumerateFileSystemEntries(resolved.Value, "*", option)
            .Take(500)
            .Select(e =>
            {
                var rel = Path.GetRelativePath(_workRoot, e);
                var kind = Directory.Exists(e) ? "dir" : "file";
                return $"{kind}\t{rel}";
            });

        var text = string.Join('\n', entries);
        return Task.FromResult(Ok(call, string.IsNullOrEmpty(text) ? "(empty)" : text));
    }

    private Task<DysonToolCallResult> CreateDirectoryAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var path = RequireString(doc.RootElement, "path");
        if (path.IsError)
            return Task.FromResult(Error(call, path.Error));

        var createParents = !doc.RootElement.TryGetProperty("createParents", out var cp)
            || cp.ValueKind != JsonValueKind.False;

        var resolved = ResolveUnderWorkRoot(path.Value);
        if (resolved.IsError)
            return Task.FromResult(Error(call, resolved.Error));

        if (createParents)
            Directory.CreateDirectory(resolved.Value);
        else
            Directory.CreateDirectory(resolved.Value); // CreateDirectory always creates parents on .NET

        return Task.FromResult(Ok(call, $"Created directory {path.Value}."));
    }

    private async Task<DysonToolCallResult> ShellExecuteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var shellName = RequireString(doc.RootElement, "shell");
        if (shellName.IsError)
            return Error(call, shellName.Error);

        var command = RequireString(doc.RootElement, "command");
        if (command.IsError)
            return Error(call, command.Error);

        var resolve = ResolveConfiguredShell(shellName.Value);
        if (resolve.IsError)
            return Error(call, resolve.Error);

        var workDirRel = doc.RootElement.TryGetProperty("workingDirectory", out var wdProp)
            ? wdProp.GetString()
            : null;
        var workDir = string.IsNullOrWhiteSpace(workDirRel)
            ? Result<string, string>.AsValue(_workRoot)
            : ResolveUnderWorkRoot(workDirRel);
        if (workDir.IsError)
            return Error(call, workDir.Error);

        if (!Directory.Exists(workDir.Value))
            return Error(call, $"Working directory not found: {workDirRel ?? "."}");

        var timeoutMs = GetInt(doc.RootElement, "timeoutMs");
        var run = await DysonWindowsShell
            .ExecuteWithPathAsync(
                resolve.Value.ExecutablePath,
                command.Value,
                workDir.Value,
                timeoutMs,
                cancellationToken,
                resolve.Value.FixedArgs)
            .ConfigureAwait(false);
        if (run.IsError)
            return Error(call, run.Error);

        var r = run.Value;
        var sb = new StringBuilder();
        sb.Append("exitCode=");
        sb.Append(r.ExitCode);
        if (r.TimedOut)
            sb.Append(" timedOut=true");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(r.Stdout))
        {
            sb.AppendLine("--- stdout ---");
            sb.AppendLine(r.Stdout.TrimEnd());
        }

        if (!string.IsNullOrEmpty(r.Stderr))
        {
            sb.AppendLine("--- stderr ---");
            sb.AppendLine(r.Stderr.TrimEnd());
        }

        var content = sb.ToString().TrimEnd();
        if (string.IsNullOrEmpty(content))
            content = "(no output)";

        // Soft Plan gate: command already ran; reinforce read-only shell policy in the result.
        var planMode = string.Equals(_session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase);
        content = DysonMcpPipeline.PrefixPlanShellWarning(planMode, content);

        return r.TimedOut || r.ExitCode != 0
            ? Error(call, content)
            : Ok(call, content);
    }

    private async Task<DysonToolCallResult> StartLongRunningShellAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var shellName = RequireString(doc.RootElement, "shell");
        if (shellName.IsError)
            return Error(call, shellName.Error);

        var command = RequireString(doc.RootElement, "command");
        if (command.IsError)
            return Error(call, command.Error);

        var resolve = ResolveConfiguredShell(shellName.Value);
        if (resolve.IsError)
            return Error(call, resolve.Error);

        var workDirRel = doc.RootElement.TryGetProperty("workingDirectory", out var wdProp)
            ? wdProp.GetString()
            : null;
        var workDir = string.IsNullOrWhiteSpace(workDirRel)
            ? Result<string, string>.AsValue(_workRoot)
            : ResolveUnderWorkRoot(workDirRel);
        if (workDir.IsError)
            return Error(call, workDir.Error);

        if (!Directory.Exists(workDir.Value))
            return Error(call, $"Working directory not found: {workDirRel ?? "."}");

        var started = await DysonLongRunningShellRegistry
            .StartAsync(
                _workDirectoryId,
                resolve.Value.Name,
                resolve.Value.ExecutablePath,
                command.Value,
                workDir.Value,
                cancellationToken,
                resolve.Value.FixedArgs)
            .ConfigureAwait(false);
        if (started.IsError)
            return Error(call, started.Error);

        var info = started.Value;
        // Wall-clock 1s so early boot output lands in the ring (not ReadTail timeoutMs=1000,
        // which returns on the first pump signal).
        await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

        var tailText = "(no output)";
        var tail = await DysonLongRunningShellRegistry
            .ReadTailAsync(
                _workDirectoryId,
                info.Id,
                maxChars: 8 * 1024,
                sinceOffset: null,
                timeoutMs: 0,
                cancellationToken)
            .ConfigureAwait(false);
        if (!tail.IsError && !string.IsNullOrEmpty(tail.Value.Text))
            tailText = tail.Value.Text.TrimEnd();

        var content =
            $"longRunningShellId={info.Id}\nstatus={info.Status}\nshell={info.ShellName}\ncommand={info.Command}" +
            "\n---\n" +
            tailText;

        var planMode = string.Equals(_session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase);
        content = DysonMcpPipeline.PrefixPlanShellWarning(planMode, content);
        return Ok(call, content);
    }

    private DysonToolCallResult ListLongRunningShells(DysonToolCall call)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        var list = DysonLongRunningShellRegistry.List(_workDirectoryId);
        if (list.Count == 0)
            return Ok(call, "[]");

        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            var s = list[i];
            var cmd = s.Command.Length > 80 ? s.Command[..80] + "…" : s.Command;
            sb.Append('{');
            sb.Append("\"id\":").Append(s.Id);
            sb.Append(",\"status\":\"").Append(s.Status).Append('"');
            sb.Append(",\"shell\":").Append(JsonSerializer.Serialize(s.ShellName));
            sb.Append(",\"command\":").Append(JsonSerializer.Serialize(cmd));
            if (s.ExitCode is int code)
                sb.Append(",\"exitCode\":").Append(code);
            sb.Append(",\"startedUtc\":\"").Append(s.StartedUtc.ToString("O")).Append('"');
            sb.Append('}');
        }

        sb.Append(']');
        return Ok(call, sb.ToString());
    }

    private Result<DysonConfiguredShellSpec, string> ResolveConfiguredShell(string shellName)
    {
        var available = _session.Config.AvailableShells;
        var match = available.FirstOrDefault(
            s => string.Equals(s.Name, shellName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var listed = string.Join(", ", available.Select(s => s.Name));
            return Result<DysonConfiguredShellSpec, string>.AsError(
                string.IsNullOrEmpty(listed)
                    ? $"Shell '{shellName}' is not available for this session."
                    : $"Shell '{shellName}' is not available for this session. Available: {listed}.");
        }

        return Result<DysonConfiguredShellSpec, string>.AsValue(match);
    }

    private async Task<DysonToolCallResult> ReadLongRunningShellTailAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var id = GetInt(doc.RootElement, "longRunningShellId");
        if (id is null)
            return Error(call, "Missing required integer field 'longRunningShellId'.");

        var maxChars = GetInt(doc.RootElement, "maxChars") ?? 8 * 1024;
        var timeoutMs = GetInt(doc.RootElement, "timeoutMs") ?? 0;

        var tail = await DysonLongRunningShellRegistry
            .ReadTailAsync(_workDirectoryId, id.Value, maxChars, sinceOffset: null, timeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (tail.IsError)
            return Error(call, tail.Error);

        var t = tail.Value;
        var sb = new StringBuilder();
        sb.Append("longRunningShellId=");
        sb.Append(id.Value);
        sb.Append(" status=");
        sb.Append(t.Status);
        if (t.ExitCode is int code)
        {
            sb.Append(" exitCode=");
            sb.Append(code);
        }

        sb.Append(" nextOffset=");
        sb.Append(t.NextOffset);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(t.Text))
            sb.Append(t.Text.TrimEnd());
        else
            sb.Append("(no output)");

        return Ok(call, sb.ToString());
    }

    private async Task<DysonToolCallResult> AbortLongRunningShellAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var id = GetInt(doc.RootElement, "longRunningShellId");
        if (id is null)
            return Error(call, "Missing required integer field 'longRunningShellId'.");

        var timeoutMs = GetInt(doc.RootElement, "timeoutMs") ?? 10_000;
        var result = await DysonLongRunningShellRegistry
            .AbortAsync(_workDirectoryId, id.Value, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError)
            return Error(call, result.Error);

        var status = DysonLongRunningShellRegistry.TryGet(_workDirectoryId, id.Value, out var shell) && shell is not null
            ? shell.Status.ToString()
            : "Aborted";
        return Ok(call, $"longRunningShellId={id.Value} status={status} aborted=true");
    }

    private async Task<DysonToolCallResult> RequestLongRunningShellCancellationAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var id = GetInt(doc.RootElement, "longRunningShellId");
        if (id is null)
            return Error(call, "Missing required integer field 'longRunningShellId'.");

        var timeoutMs = GetInt(doc.RootElement, "timeoutMs") ?? 10_000;
        var result = await DysonLongRunningShellRegistry
            .RequestCancellationAsync(_workDirectoryId, id.Value, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsError)
            return Error(call, result.Error);

        var status = DysonLongRunningShellRegistry.TryGet(_workDirectoryId, id.Value, out var shell) && shell is not null
            ? shell.Status.ToString()
            : "unknown";
        return Ok(call, $"longRunningShellId={id.Value} status={status} cancelRequested=true");
    }

    private async Task<DysonToolCallResult> LongRunningShellInteractAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var id = GetInt(doc.RootElement, "longRunningShellId");
        if (id is null)
            return Error(call, "Missing required integer field 'longRunningShellId'.");

        var input = RequireString(doc.RootElement, "input");
        if (input.IsError)
            return Error(call, input.Error);

        var timeoutMs = GetInt(doc.RootElement, "timeoutMs") ?? 5_000;
        var result = await DysonLongRunningShellRegistry
            .InteractAsync(_workDirectoryId, id.Value, input.Value, timeoutMs, cancellationToken)
            .ConfigureAwait(false);

        return result.IsError
            ? Error(call, result.Error)
            : Ok(call, $"longRunningShellId={id.Value} written=true");
    }

    private DysonToolCallResult SubscribeToLongRunningShellCompletion(DysonToolCall call)
    {
        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory id is required for long-running shells.");

        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var id = GetInt(doc.RootElement, "longRunningShellId");
        if (id is null)
            return Error(call, "Missing required integer field 'longRunningShellId'.");

        var maxChars = GetInt(doc.RootElement, "includeTailMaxChars")
            ?? DysonLongRunningShellExitedFlow.DefaultIncludeTailMaxChars;

        var result = DysonLongRunningShellRegistry.SubscribeToCompletion(
            _workDirectoryId, id.Value, _session, maxChars);
        return result.IsError
            ? Error(call, result.Error)
            : Ok(call, $"longRunningShellId={id.Value} subscribed=true includeTailMaxChars={maxChars}");
    }

    private async Task<DysonToolCallResult> FreeSearchAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var options = ParseSearchOptions(call, defaultCount: 10, waterfallDefault: false, enrichDefault: false);
        if (options.IsError)
            return Error(call, options.Error);

        var result = await SearchOrchestrator.FreeSearchAsync(options.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        return await SummarizeWebOkAsync(
                call,
                SearchOrchestrator.ToJson(result.Value),
                ReadSummarizePrompt(call),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> FreeSearchAdvancedAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var options = ParseSearchOptions(call, defaultCount: 5, waterfallDefault: true, enrichDefault: true);
        if (options.IsError)
            return Error(call, options.Error);

        var result = await SearchOrchestrator.FreeSearchAdvancedAsync(options.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        return await SummarizeWebOkAsync(
                call,
                SearchOrchestrator.ToJson(result.Value),
                ReadSummarizePrompt(call),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> SearchWithSynthesisAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        var options = ParseSearchOptions(call, defaultCount: 10, waterfallDefault: true, enrichDefault: true);
        if (options.IsError)
            return Error(call, options.Error);

        var result = await SearchOrchestrator.SearchWithSynthesisAsync(options.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        return await SummarizeWebOkAsync(
                call,
                SearchOrchestrator.ToJson(result.Value),
                ReadSummarizePrompt(call),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> FreeExtractAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var root = doc.RootElement;
        var url = RequireString(root, "url");
        if (url.IsError)
            return Error(call, url.Error);

        var maxLength = GetInt(root, "maxLength") ?? 5000;
        var summarizePrompt = GetOptionalString(root, "summarizePrompt");
        var result = await SearchFetch.FreeExtractAsync(url.Value, maxLength, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        return await SummarizeWebOkAsync(call, result.Value, summarizePrompt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> WebFetchAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var root = doc.RootElement;
        var url = RequireString(root, "url");
        if (url.IsError)
            return Error(call, url.Error);

        var fullHtml = GetBool(root, "fullHtml");
        var summarizePrompt = GetOptionalString(root, "summarizePrompt");
        // Explicit maxBytes wins; else 2MB for fullHtml, 64KB when summarizing.
        var maxBytes = GetInt(root, "maxBytes") ?? (fullHtml ? 2_000_000 : 64_000);
        var result = await SearchFetch.WebFetchAsync(url.Value, maxBytes, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        var payload = SearchFetch.WebFetchToJson(result.Value);
        if (fullHtml)
            return Ok(call, payload);

        return await SummarizeWebOkAsync(call, payload, summarizePrompt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DysonToolCallResult> FetchGithubReadmeAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
        var root = doc.RootElement;
        var url = RequireString(root, "url");
        if (url.IsError)
            return Error(call, url.Error);

        var summarizePrompt = GetOptionalString(root, "summarizePrompt");
        var result = await SearchFetch.FetchGithubReadmeAsync(url.Value, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            return Error(call, result.Error);

        return await SummarizeWebOkAsync(call, result.Value, summarizePrompt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Returns summary-only MCP Content when policy says summarize; otherwise raw
    /// (already ≤1500 tokens). Parent never sees raw when summarization runs.
    /// </summary>
    private async Task<DysonToolCallResult> SummarizeWebOkAsync(
        DysonToolCall call,
        string rawContent,
        string? summarizePrompt,
        CancellationToken cancellationToken)
    {
        var tokens = new DysonTiktokenTokenCounter();
        if (!DysonWebSearchSummarizer.ShouldSummarize(call.ToolName, rawContent, tokens))
            return Ok(call, rawContent);

        var provider = ResolveSummarizerProvider();
        if (provider is null)
            return Ok(call, rawContent);

        var summary = await DysonWebSearchSummarizer
            .SummarizeAsync(
                provider,
                _http,
                call.ToolName,
                call.ArgumentsJson ?? "{}",
                rawContent,
                summarizePrompt,
                tokens,
                cancellationToken)
            .ConfigureAwait(false);

        return Ok(call, summary);
    }

    private OpenAiCompatibleAgentProvider? ResolveSummarizerProvider()
    {
        if (_session.Config.SummarizerProvider is OpenAiCompatibleAgentProvider configured)
            return configured;

        return _session.Provider as OpenAiCompatibleAgentProvider;
    }

    private static string? ReadSummarizePrompt(DysonToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            return GetOptionalString(doc.RootElement, "summarizePrompt");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Result<SearchOptions, string> ParseSearchOptions(
        DysonToolCall call,
        int defaultCount,
        bool waterfallDefault,
        bool enrichDefault)
    {
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            var query = RequireString(root, "query");
            if (query.IsError)
                return Result<SearchOptions, string>.AsError(query.Error);

            List<string>? engines = null;
            if (root.TryGetProperty("engines", out var enginesProp) && enginesProp.ValueKind == JsonValueKind.Array)
            {
                engines = [];
                foreach (var item in enginesProp.EnumerateArray())
                {
                    var s = item.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        engines.Add(s);
                }
            }

            List<string>? includeDomains = ReadStringArray(root, "includeDomains");
            List<string>? excludeDomains = ReadStringArray(root, "excludeDomains");

            var waterfall = waterfallDefault;
            if (root.TryGetProperty("waterfall", out var wf))
                waterfall = wf.ValueKind != JsonValueKind.False;

            var enrich = enrichDefault;
            if (root.TryGetProperty("enrich", out var en))
                enrich = en.ValueKind != JsonValueKind.False;

            double waterfallMinConfidence = 0.6;
            if (root.TryGetProperty("waterfallMinConfidence", out var wmc)
                && wmc.ValueKind == JsonValueKind.Number
                && wmc.TryGetDouble(out var wmcVal))
            {
                waterfallMinConfidence = wmcVal;
            }

            return Result<SearchOptions, string>.AsValue(new SearchOptions
            {
                Query = query.Value,
                Count = GetInt(root, "count") ?? defaultCount,
                Engines = engines,
                MinConfidence = GetInt(root, "minConfidence") ?? 1,
                IncludeDomains = includeDomains,
                ExcludeDomains = excludeDomains,
                Waterfall = waterfall,
                WaterfallMinResults = GetInt(root, "waterfallMinResults") ?? 3,
                WaterfallMinConfidence = waterfallMinConfidence,
                Enrich = enrich,
                EnrichMax = GetInt(root, "enrichMax") ?? 3,
                BraveApiKey = SearchOrchestrator.ResolveBraveApiKey(_session.Config),
            });
        }
        catch (JsonException)
        {
            return Result<SearchOptions, string>.AsError($"{call.ToolName}: invalid JSON arguments.");
        }
    }

    private static List<string>? ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }

        return list.Count > 0 ? list : null;
    }

    private Result<string, string> ResolveUnderWorkRoot(string path)
    {
        try
        {
            var combined = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(_workRoot, path));

            if (!IsUnderWorkRoot(combined))
                return Result<string, string>.AsError($"Path escapes work directory: {path}");

            return Result<string, string>.AsValue(combined);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }
    }

    private bool IsUnderWorkRoot(string fullPath)
    {
        var root = _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(fullPath);
        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                _workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        return full.StartsWith(
            root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string ArgsOrEmpty(DysonToolCall call) =>
        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson;

    private static Result<string, string> RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return Result<string, string>.AsError($"Missing required string field '{name}'.");

        var value = prop.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return Result<string, string>.AsError($"Field '{name}' must be non-empty.");

        return Result<string, string>.AsValue(value);
    }

    private static int? GetInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var i))
            return i;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var parsed))
            return parsed;
        return null;
    }

    private static bool GetBool(JsonElement root, string name, bool defaultValue = false)
    {
        if (!root.TryGetProperty(name, out var prop))
            return defaultValue;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue,
        };
    }

    private static string? GetOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;
        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static Result<DysonSessionTodoStatus?, string> TryParseOptionalTodoStatus(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return Result<DysonSessionTodoStatus?, string>.AsValue(null);

        if (prop.ValueKind != JsonValueKind.String)
            return Result<DysonSessionTodoStatus?, string>.AsError($"Field '{name}' must be a string.");

        var raw = prop.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return Result<DysonSessionTodoStatus?, string>.AsValue(null);

        if (Enum.TryParse<DysonSessionTodoStatus>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return Result<DysonSessionTodoStatus?, string>.AsValue(parsed);
        }

        return Result<DysonSessionTodoStatus?, string>.AsError(
            $"Field '{name}' must be one of: pending, ongoing, complete.");
    }

    private static Result<IReadOnlyList<string>?, string> TryParseOptionalStringArray(
        JsonElement root,
        string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind == JsonValueKind.Null)
            return Result<IReadOnlyList<string>?, string>.AsValue(null);

        if (prop.ValueKind != JsonValueKind.Array)
            return Result<IReadOnlyList<string>?, string>.AsError($"Field '{name}' must be an array.");

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return Result<IReadOnlyList<string>?, string>.AsError($"Field '{name}' items must be strings.");

            list.Add(item.GetString() ?? "");
        }

        return Result<IReadOnlyList<string>?, string>.AsValue(list);
    }

    private static string SerializeTodos(IReadOnlyList<DysonSessionTodo> todos) =>
        JsonSerializer.Serialize(todos.Select(ToTodoDto));

    private static string SerializeTodo(DysonSessionTodo todo) =>
        JsonSerializer.Serialize(ToTodoDto(todo));

    private static object ToTodoDto(DysonSessionTodo todo) => new
    {
        id = todo.Id,
        sessionId = todo.SessionId,
        taskCode = todo.TaskCode,
        displayName = todo.DisplayName,
        status = todo.Status.ToString().ToLowerInvariant(),
        comments = todo.Comments,
        sequence = todo.Sequence,
        createdUtc = todo.CreatedUtc,
        updatedUtc = todo.UpdatedUtc,
    };
}
