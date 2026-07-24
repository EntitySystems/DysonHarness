using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Executes workspace-scoped MCP tools against a work directory root, plus RenameSession,
/// GetDateTime, ShellExecute, subagent spawn/report tools, and in-process web search/fetch tools.
/// Other catalog tools return a not-implemented stub result.
/// </summary>
public sealed class DysonWorkspaceToolExecutor
{
    private readonly DysonAgentSession _session;
    private readonly string _workRoot;
    private readonly HttpClient _http;
    private readonly DysonSessionStore? _store;

    public DysonWorkspaceToolExecutor(
        DysonAgentSession session,
        string workDirectoryAbsolutePath,
        HttpClient http,
        DysonSessionStore? store = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectoryAbsolutePath);
        _workRoot = Path.GetFullPath(workDirectoryAbsolutePath);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _store = store;
    }

    public async Task<DysonToolCallResult> ExecuteAsync(
        DysonToolCall call,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            return call.ToolName switch
            {
                "RenameSession" => await RenameSessionAsync(call, cancellationToken).ConfigureAwait(false),
                "GetDateTime" => await GetDateTimeAsync(call, cancellationToken).ConfigureAwait(false),
                "StartSubagent" => await StartSubagentAsync(call, cancellationToken).ConfigureAwait(false),
                "WaitForSubagent" => await WaitForSubagentAsync(call, cancellationToken).ConfigureAwait(false),
                "InspectSubagentLog" => await InspectSubagentLogAsync(call, cancellationToken).ConfigureAwait(false),
                "StopSubagent" => await StopSubagentAsync(call, cancellationToken).ConfigureAwait(false),
                "SubmitSubagentReport" => await SubmitSubagentReportAsync(call, cancellationToken).ConfigureAwait(false),
                "ReadFile" => await ReadFileAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateFile" => await CreateFileAsync(call, cancellationToken).ConfigureAwait(false),
                "WriteFile" => await WriteFileAsync(call, cancellationToken).ConfigureAwait(false),
                "Grep" => await GrepAsync(call, cancellationToken).ConfigureAwait(false),
                "ListDirectory" => await ListDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "CreateDirectory" => await CreateDirectoryAsync(call, cancellationToken).ConfigureAwait(false),
                "ShellExecute" => await ShellExecuteAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeSearch" => await FreeSearchAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeSearchAdvanced" => await FreeSearchAdvancedAsync(call, cancellationToken).ConfigureAwait(false),
                "SearchWithSynthesis" => await SearchWithSynthesisAsync(call, cancellationToken).ConfigureAwait(false),
                "FreeExtract" => await FreeExtractAsync(call, cancellationToken).ConfigureAwait(false),
                "WebFetch" => await WebFetchAsync(call, cancellationToken).ConfigureAwait(false),
                "FetchGithubReadme" => await FetchGithubReadmeAsync(call, cancellationToken).ConfigureAwait(false),
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

    private static DysonToolCallResult Ok(DysonToolCall call, string content) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.ToolName,
            Stage = call.Stage,
            IsError = false,
            Content = content,
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

    private async Task<DysonToolCallResult> StartSubagentAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        string? agentMode;
        string? task;
        string? context;
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
        }
        catch (JsonException)
        {
            return Error(call, "StartSubagent: invalid JSON arguments.");
        }

        var started = await _session.CreateChildAsync(agentMode, task, context, cancellationToken)
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
        }));
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
        }
        catch (JsonException)
        {
            return Error(call, "SubmitSubagentReport: invalid JSON arguments.");
        }

        var submitted = await _session.SubmitSubagentReportAsync(summary, failed, cancellationToken)
            .ConfigureAwait(false);
        if (submitted.IsError)
            return Error(call, submitted.Error);

        await PersistSessionStatusAsync(_session, _session.Status, summary, cancellationToken)
            .ConfigureAwait(false);

        if (_session.Parent is not null && _store is not null && _session.Parent.PersistenceId != Guid.Empty)
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
        var edits = new List<(string Old, string New)>();

        if (doc.RootElement.TryGetProperty("old_text", out var oldProp)
            && doc.RootElement.TryGetProperty("new_text", out var newProp))
        {
            edits.Add((oldProp.GetString() ?? "", newProp.GetString() ?? ""));
        }

        if (doc.RootElement.TryGetProperty("edits", out var editsArr)
            && editsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var edit in editsArr.EnumerateArray())
            {
                if (!edit.TryGetProperty("old_text", out var o) || !edit.TryGetProperty("new_text", out var n))
                    continue;
                edits.Add((o.GetString() ?? "", n.GetString() ?? ""));
            }
        }

        if (edits.Count == 0)
            return Error(call, "WriteFile: provide content, or old_text/new_text, or edits[].");

        var applied = 0;
        foreach (var (oldText, newText) in edits)
        {
            if (string.IsNullOrEmpty(oldText))
                return Error(call, "WriteFile: old_text must be non-empty.");

            var idx = text.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0)
                return Error(call, $"WriteFile: old_text not found in {path.Value}.");

            text = string.Concat(text.AsSpan(0, idx), newText, text.AsSpan(idx + oldText.Length));
            applied++;
        }

        await File.WriteAllTextAsync(resolved.Value, text, cancellationToken).ConfigureAwait(false);
        return Ok(call, $"Applied {applied} edit(s) to {path.Value}.");
    }

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
            var option = SearchOption.AllDirectories;
            files = string.IsNullOrWhiteSpace(glob)
                ? Directory.EnumerateFiles(resolved.Value, "*", option)
                : Directory.EnumerateFiles(resolved.Value, glob, option);
        }
        else
        {
            return Error(call, $"Path not found: {searchPath}");
        }

        var sb = new StringBuilder();
        var matches = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnderWorkRoot(file))
                continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                continue;
            }

            var rel = Path.GetRelativePath(_workRoot, file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                    continue;

                sb.Append(rel);
                sb.Append(':');
                sb.Append(i + 1);
                sb.Append(':');
                sb.AppendLine(lines[i]);
                matches++;
                if (matches >= maxMatches)
                    return Ok(call, sb.ToString().TrimEnd() + $"\n… capped at {maxMatches} matches");
            }
        }

        return Ok(call, matches == 0 ? "No matches." : sb.ToString().TrimEnd());
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

        if (!Enum.TryParse<DysonShellType>(shellName.Value, ignoreCase: true, out var shellType))
            return Error(call, $"Unknown shell '{shellName.Value}'.");

        var available = _session.Config.AvailableShellTypes;
        if (!available.Contains(shellType))
        {
            var listed = string.Join(", ", available);
            return Error(call, $"Shell '{shellType}' is not available for this session. Available: {listed}.");
        }

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
        DysonShell shell;
        try
        {
            shell = DysonShell.Create(shellType);
        }
        catch (Exception ex)
        {
            return Error(call, ex.Message);
        }

        var run = await shell.ExecuteAsync(command.Value, workDir.Value, timeoutMs, cancellationToken)
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
        return r.TimedOut || r.ExitCode != 0
            ? Error(call, content)
            : Ok(call, string.IsNullOrEmpty(content) ? "(no output)" : content);
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
}
